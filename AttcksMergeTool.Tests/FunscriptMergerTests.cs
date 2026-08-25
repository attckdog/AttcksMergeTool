using System.Text.Json;

using AttcksMergeTool.Models;
using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

/// <summary>
/// Exercises the timeline arithmetic end to end - real files in, a real merged script out -
/// with ffprobe replaced by known durations so the expected offsets are exact.
/// </summary>
public class FunscriptMergerTests
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Nothing_to_merge_produces_no_result() {
        using var workspace = new TempWorkspace();
        var merger = new FunscriptMerger(
            new FakeJobLogger(), workspace.Options(nameof(Nothing_to_merge_produces_no_result)),
            TrimLookup.Empty, new FakeMediaProbe());

        Assert.Null(await merger.MergeAsync([]));
        Assert.Empty(TimelinePlan.Build([], []).Entries);
    }

    [Fact]
    public async Task Each_scene_starts_where_the_previous_video_ended() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (0, 0), (1000, 100));
        Seed(workspace, "B", (0, 0), (500, 50));

        var probe = new FakeMediaProbe().WithDuration("A.mp4", 2000).WithDuration("B.mp4", 5000);

        Funscript merged = await MergeAsync(workspace, nameof(Each_scene_starts_where_the_previous_video_ended), probe);

        Assert.Equal([0, 2000], merged.Bookmarks!.Select(bookmark => bookmark.Time));
        Assert.Equal(["A", "B"], merged.Bookmarks!.Select(bookmark => bookmark.Name));
        Assert.Equal(7, merged.Metadata!.Duration);
    }

    /// <remarks>
    /// The video's length wins over the script's because it includes any silent tail the
    /// script does not bother to describe.
    /// </remarks>
    [Fact]
    public async Task The_video_duration_wins_over_the_last_keyframe() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (0, 0), (1000, 100));
        Seed(workspace, "B", (0, 0));

        var probe = new FakeMediaProbe().WithDuration("A.mp4", 9000).WithDuration("B.mp4", 1000);

        Funscript merged = await MergeAsync(workspace, nameof(The_video_duration_wins_over_the_last_keyframe), probe);

        Assert.Equal(9000, merged.Bookmarks![1].Time);
    }

    /// <remarks>
    /// There is no video to stay in sync with, so every scene's own length is all there is
    /// to go on and nothing is left out.
    /// </remarks>
    [Fact]
    public async Task A_script_only_run_advances_by_each_scenes_last_keyframe() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1500, 100)));
        workspace.WriteScript("B.funscript", ScriptBuilder.Basic((0, 0), (500, 50)));

        Funscript merged = await MergeAsync(
            workspace, nameof(A_script_only_run_advances_by_each_scenes_last_keyframe), new FakeMediaProbe());

        Assert.Equal(["A", "B"], merged.Bookmarks!.Select(bookmark => bookmark.Name));
        Assert.Equal([0, 1500], merged.Bookmarks!.Select(bookmark => bookmark.Time));
    }

    /// <remarks>
    /// It has nowhere to sit on a timeline the videos define: merging it anyway would push
    /// every later scene ahead of the video by its whole length, which is the desync this
    /// whole plan exists to prevent.
    /// </remarks>
    [Fact]
    public async Task A_scene_with_no_video_is_left_off_a_video_timeline() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1500, 100)));
        Seed(workspace, "B", (0, 0));

        var probe = new FakeMediaProbe().WithDuration("B.mp4", 1000);

        Funscript merged = await MergeAsync(workspace, nameof(A_scene_with_no_video_is_left_off_a_video_timeline), probe);

        Assert.Equal(["B"], merged.Bookmarks!.Select(bookmark => bookmark.Name));
        Assert.Equal([0], merged.Bookmarks!.Select(bookmark => bookmark.Time));
    }

    /// <remarks>
    /// The other half of the same guarantee: the video is concatenated whether or not anything
    /// scripts it, so it has to hold its place on the script's timeline as silence. Skipping it
    /// used to pull every later scene forward by its whole length.
    /// </remarks>
    [Fact]
    public async Task A_video_with_no_script_reserves_its_length_as_silence() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (0, 0), (1000, 100));
        workspace.WriteVideo("B.mp4");
        Seed(workspace, "C", (0, 0), (500, 50));

        var probe = new FakeMediaProbe()
            .WithDuration("A.mp4", 2000)
            .WithDuration("B.mp4", 5000)
            .WithDuration("C.mp4", 1000);

        Funscript merged = await MergeAsync(workspace, nameof(A_video_with_no_script_reserves_its_length_as_silence), probe);

        Assert.Equal(["A", "B", "C"], merged.Bookmarks!.Select(bookmark => bookmark.Name));
        Assert.Equal([0, 2000, 7000], merged.Bookmarks!.Select(bookmark => bookmark.Time));

        // Nothing was appended for B, so C's keyframes still open at C's own start.
        Assert.Equal([0, 1000, 7000, 7500], merged.Actions!.Select(action => action.At));
    }

    [Fact]
    public async Task An_unreadable_video_falls_back_to_the_last_keyframe_and_says_so() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (0, 0), (1500, 100));
        Seed(workspace, "B", (0, 0));

        var logger = new FakeJobLogger();
        var probe = new FakeMediaProbe().WithDuration("A.mp4", null).WithDuration("B.mp4", 1000);

        Funscript merged = await MergeAsync(
            workspace, nameof(An_unreadable_video_falls_back_to_the_last_keyframe_and_says_so), probe, logger: logger);

        Assert.Equal(1500, merged.Bookmarks![1].Time);
        Assert.True(logger.WarnedAbout("Could not read the duration"));
    }

    /// <remarks>
    /// The regression guard for a trimmed scene whose video could not be probed. The keyframes
    /// appended are rebased onto the trimmed timeline, so the fallback length has to be
    /// measured the same way - taking it from the source timestamps overshot by the trim's
    /// start offset and pushed every later scene along.
    /// </remarks>
    [Fact]
    public async Task A_trimmed_scene_falls_back_to_its_trimmed_length_not_its_source_length() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (2000, 10), (4000, 40), (6000, 60));
        Seed(workspace, "B", (0, 0));

        var probe = new FakeMediaProbe().WithDuration("A.mp4", null).WithDuration("B.mp4", 1000);
        TrimLookup trims = TrimFor(workspace, "A.mp4", startSeconds: 2, endSeconds: 5);

        Funscript merged = await MergeAsync(
            workspace, nameof(A_trimmed_scene_falls_back_to_its_trimmed_length_not_its_source_length), probe, trims);

        // Keyframes rebase to 0 and 2000; the one at 6000 is outside the window entirely.
        Assert.Equal([0, 2000], merged.Actions!.Take(2).Select(action => action.At));
        Assert.Equal(2000, merged.Bookmarks![1].Time);
    }

    [Fact]
    public async Task A_trimmed_scene_occupies_only_the_kept_part_of_its_video() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (2000, 10), (4000, 40));
        Seed(workspace, "B", (0, 0));

        var probe = new FakeMediaProbe().WithDuration("A.mp4", 10_000).WithDuration("B.mp4", 1000);
        TrimLookup trims = TrimFor(workspace, "A.mp4", startSeconds: 2, endSeconds: 5);

        Funscript merged = await MergeAsync(
            workspace, nameof(A_trimmed_scene_occupies_only_the_kept_part_of_its_video), probe, trims);

        Assert.Equal(3000, merged.Bookmarks![1].Time);
    }

    [Fact]
    public async Task A_trim_end_past_the_video_is_clamped_to_the_video() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (0, 0), (1000, 100));
        Seed(workspace, "B", (0, 0));

        var probe = new FakeMediaProbe().WithDuration("A.mp4", 4000).WithDuration("B.mp4", 1000);
        TrimLookup trims = TrimFor(workspace, "A.mp4", startSeconds: 1, endSeconds: 30);

        Funscript merged = await MergeAsync(
            workspace, nameof(A_trim_end_past_the_video_is_clamped_to_the_video), probe, trims);

        Assert.Equal(3000, merged.Bookmarks![1].Time);
    }

    /// <remarks>
    /// Without this the device would snap from wherever the last scene left off to wherever
    /// the next one opens, at the exact moment the video cuts.
    /// </remarks>
    [Fact]
    public async Task The_seam_is_anchored_and_the_transition_window_is_collapsed() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (0, 0), (1000, 100));
        Seed(workspace, "B", (100, 10), (200, 20), (600, 60));

        var probe = new FakeMediaProbe().WithDuration("A.mp4", 2000).WithDuration("B.mp4", 1000);

        Funscript merged = await MergeAsync(
            workspace, nameof(The_seam_is_anchored_and_the_transition_window_is_collapsed), probe);

        Assert.Equal(
            [(0, 0), (1000, 100), (2000, 100), (2500, 20), (2600, 60)],
            merged.Actions!.Select(action => (action.At, action.Pos)));
    }

    [Fact]
    public async Task An_embedded_axis_alias_maps_onto_its_canonical_id() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)).WithAxis("twist", (0, 50)));
        workspace.WriteVideo("A.mp4");

        Funscript merged = await MergeAsync(
            workspace, nameof(An_embedded_axis_alias_maps_onto_its_canonical_id),
            new FakeMediaProbe().WithDuration("A.mp4", 2000));

        Assert.Equal("R0", Assert.Single(merged.Axes!).Id);
        Assert.Equal("multiaxis", merged.Metadata!.Type);
    }

    [Fact]
    public async Task A_sibling_axis_file_merges_onto_its_canonical_id() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (0, 0), (1000, 100));
        workspace.WriteScript("A.twist.funscript", ScriptBuilder.Basic((0, 50), (900, 90)));

        Funscript merged = await MergeAsync(
            workspace, nameof(A_sibling_axis_file_merges_onto_its_canonical_id),
            new FakeMediaProbe().WithDuration("A.mp4", 2000));

        FunscriptAxis axis = Assert.Single(merged.Axes!);
        Assert.Equal("R0", axis.Id);
        Assert.Equal([0, 900], axis.Actions!.Select(action => action.At));
    }

    /// <remarks>
    /// The regression guard for the sibling-as-scene bug: "alpha" sorts before "funscript",
    /// so the file used to be reached first and merged as a scene of its own - a bookmark that
    /// should not exist, keyframes on the root axis instead of theirs, and every later scene
    /// pushed along the timeline.
    /// </remarks>
    [Fact]
    public async Task An_axis_sorting_before_its_own_scene_does_not_become_a_scene() {
        using var workspace = new TempWorkspace();
        Seed(workspace, "A", (0, 0), (1000, 100));
        workspace.WriteScript("A.alpha.funscript", ScriptBuilder.Basic((0, 50), (900, 90)));
        Seed(workspace, "B", (0, 0));

        var probe = new FakeMediaProbe().WithDuration("A.mp4", 2000).WithDuration("B.mp4", 1000);

        Funscript merged = await MergeAsync(
            workspace, nameof(An_axis_sorting_before_its_own_scene_does_not_become_a_scene), probe);

        Assert.Equal(["A", "B"], merged.Bookmarks!.Select(bookmark => bookmark.Name));
        Assert.Equal([0, 2000], merged.Bookmarks!.Select(bookmark => bookmark.Time));

        FunscriptAxis axis = Assert.Single(merged.Axes!);
        Assert.Equal("alpha", axis.Id);
        Assert.Equal([0, 900], axis.Actions!.Select(action => action.At));

        // The root axis carries only what the two main scripts contributed - A's keyframes, the
        // seam anchor at A's final position, then B's - and nothing from the alpha file.
        Assert.Equal(
            [(0, 0), (1000, 100), (2000, 100), (2000, 0)],
            merged.Actions!.Select(action => (action.At, action.Pos)));
    }

    [Fact]
    public async Task Metadata_from_every_input_is_unioned_rather_than_overwritten() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)).WithMetadata(new FunscriptMetadata {
            Creator = "Alice",
            Notes = "First half",
            Performers = ["Ana", "Ben"],
            Tags = ["pov"]
        }));
        workspace.WriteScript("B.funscript", ScriptBuilder.Basic((0, 0)).WithMetadata(new FunscriptMetadata {
            Creator = "Bob",
            Notes = "Second half",
            Performers = ["ana", "Cara"],
            Tags = ["pov", "vr"]
        }));
        workspace.WriteVideo("A.mp4");
        workspace.WriteVideo("B.mp4");

        var probe = new FakeMediaProbe().WithDuration("A.mp4", 2000).WithDuration("B.mp4", 1000);

        Funscript merged = await MergeAsync(
            workspace, nameof(Metadata_from_every_input_is_unioned_rather_than_overwritten), probe);

        Assert.Equal("Alice, Bob", merged.Metadata!.Creator);
        Assert.Equal(["Ana", "Ben", "Cara"], merged.Metadata.Performers);
        Assert.Equal(["pov", "vr"], merged.Metadata.Tags);
        Assert.Contains("First half", merged.Metadata.Notes);
        Assert.Contains("Second half", merged.Metadata.Notes);

        // Nothing set a licence, so the field is omitted rather than written empty.
        Assert.Null(merged.Metadata.License);
    }

    private static void Seed(TempWorkspace workspace, string sceneName, params (int At, int Pos)[] actions) {
        workspace.WriteScript($"{sceneName}.funscript", ScriptBuilder.Basic(actions));
        workspace.WriteVideo($"{sceneName}.mp4");
    }

    private static TrimLookup TrimFor(TempWorkspace workspace, string videoName, double startSeconds, double endSeconds) =>
        new([
            new VideoSegmentSettings {
                FilePath = workspace.Path(videoName),
                StartTime = startSeconds,
                EndTime = endSeconds,
                UseTrim = true
            }
        ]);

    private static async Task<Funscript> MergeAsync(
        TempWorkspace workspace,
        string outputName,
        FakeMediaProbe probe,
        TrimLookup? trims = null,
        FakeJobLogger? logger = null) {
        MergeOptions options = workspace.Options(outputName);

        var merger = new FunscriptMerger(logger ?? new FakeJobLogger(), options, trims ?? TrimLookup.Empty, probe);

        TimelinePlan plan = TimelinePlan.Build(
            SceneScriptIndex.Build(workspace.Root), MediaFileScanner.FindVideos(workspace.Root));

        await merger.MergeAsync(plan.Entries);

        return JsonSerializer.Deserialize<Funscript>(File.ReadAllText(options.OutputScriptPath), ReadOptions)!;
    }
}
