using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// The single ordered list of scenes that both halves of the merge walk.
/// </summary>
/// <remarks>
/// The merged script and the merged video used to be built by two independent walks over two
/// independently built lists - funscripts for one, videos for the other - and nothing checked
/// that the two described the same scenes. They planned the same timeline twice and could
/// disagree about it, which is what let an unpaired file on either side silently desync the
/// output while the run still reported success.
/// <para>
/// There is one walk now. When there are videos they define the timeline: the plan is the video
/// list in concat order, each entry carrying whatever funscripts share its base name. A video
/// with no script therefore keeps its place and contributes its length as silence instead of
/// pulling every later scene forward by it.
/// </para>
/// <para>
/// A funscript with no video has nowhere to sit on that timeline - merging it anyway pushes
/// every later scene ahead of the video by its whole length - so it is left out and reported.
/// A script-only run has no video to stay in sync with, so there the plan is simply the scenes
/// and nothing is skipped.
/// </para>
/// </remarks>
/// <param name="SkippedVideos">
/// Videos left out because nothing scripts them, when the job asked for that. Empty otherwise:
/// a video that stays on the timeline unscripted is an <see cref="UnscriptedVideos"/> entry.
/// </param>
public sealed record TimelinePlan(
    IReadOnlyList<TimelineEntry> Entries,
    IReadOnlyList<SceneScripts> SkippedScenes,
    IReadOnlyList<TimelineEntry> SkippedVideos)
{
    /// <summary>Videos kept on the timeline that no funscript describes.</summary>
    public IReadOnlyList<TimelineEntry> UnscriptedVideos =>
        Entries.Where(entry => entry.VideoPath is not null && entry.Scripts is null).ToList();

    /// <param name="skipUnscriptedVideos">
    /// Leave a video out entirely when no funscript shares its name. Off by default, which is
    /// what the merge did before the option existed; the window turns it on.
    /// </param>
    public static TimelinePlan Build(
        IReadOnlyList<SceneScripts> scenes,
        IReadOnlyList<string> videoPaths,
        bool skipUnscriptedVideos = false) {
        if (videoPaths.Count == 0) {
            return new TimelinePlan(
                scenes.Select(scene => new TimelineEntry(scene.Name, scene, null)).ToList(), [], []);
        }

        var byName = new Dictionary<string, SceneScripts>(StringComparer.OrdinalIgnoreCase);
        foreach (SceneScripts scene in scenes) byName[scene.Name] = scene;

        var entries = new List<TimelineEntry>(videoPaths.Count);
        var paired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string videoPath in videoPaths) {
            string name = Path.GetFileNameWithoutExtension(videoPath);

            SceneScripts? scripts = byName.GetValueOrDefault(name);
            if (scripts is not null) paired.Add(name);

            entries.Add(new TimelineEntry(name, scripts, videoPath));
        }

        List<SceneScripts> skippedScenes = scenes.Where(scene => !paired.Contains(scene.Name)).ToList();

        if (!skipUnscriptedVideos) return new TimelinePlan(entries, skippedScenes, []);

        // Partitioned rather than filtered away, so the report can still name what went and why.
        List<TimelineEntry> skippedVideos = entries.Where(entry => entry.Scripts is null).ToList();

        return new TimelinePlan(
            entries.Where(entry => entry.Scripts is not null).ToList(), skippedScenes, skippedVideos);
    }

    /// <summary>
    /// Names every file that could not be paired and says what happens to it. Silent when
    /// everything paired up.
    /// </summary>
    public async Task ReportAsync(IJobLogger logger, CancellationToken cancellationToken = default) {
        foreach (TimelineEntry entry in UnscriptedVideos) {
            logger.Log(
                $"  -> No funscript found for video '{entry.Name}'. It keeps its place on the "
                + "timeline and plays unscripted.",
                LogLevel.Warning);
        }

        foreach (TimelineEntry entry in SkippedVideos) {
            logger.Log(
                $"  -> Skipping video '{entry.Name}': no funscript of that name is in the input folder.",
                LogLevel.Warning);
        }

        foreach (SceneScripts scene in SkippedScenes) {
            // Read only to say what leaving it out costs. It is a handful of small files, and
            // the number is the difference between a warning the user can act on and one they
            // cannot.
            int lengthMs = await ScriptReader.LastKeyframeMsAsync(scene, cancellationToken);

            logger.Log(
                $"  -> No video found for funscript '{scene.Name}'. Skipped: merging it would "
                + $"push every later scene {lengthMs}ms ahead of the video.",
                LogLevel.Warning);
        }

        if (SkippedVideos.Count > 0) {
            logger.Log(
                $"{SkippedVideos.Count} video(s) were skipped because nothing scripts them. Give "
                + "each one a funscript of the same name, or turn off \"Skip videos with no "
                + "funscript\" in Options > Merge to include them unscripted.",
                LogLevel.Warning);
        }

        if (SkippedScenes.Count == 0) return;

        logger.Log(
            $"{SkippedScenes.Count} funscript scene(s) were left out so the merged script stays "
            + "in sync. Give each one a video of the same name to include it, or merge the "
            + "scripts on their own.",
            LogLevel.Warning);
    }
}
