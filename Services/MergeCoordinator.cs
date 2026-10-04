using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Runs a complete merge job: validate the environment, plan one timeline that both outputs
/// follow, merge the scripts onto it, merge the videos, then retime the script onto the
/// lengths the encode actually produced. This is the only entry point the UI needs.
/// </summary>
public sealed class MergeCoordinator
{
    private readonly IJobLogger _logger;
    private readonly MergeOptions _options;
    private readonly TrimLookup _trims;
    private readonly IProcessRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly AudioLibrary _audio;

    /// <summary>
    /// The video paths in the order the window listed them, which is the order the merge
    /// follows. Empty for a caller that arranged nothing, and then the scan's own order stands.
    /// </summary>
    private readonly IReadOnlyList<string> _configuredOrder;

    /// <summary>The video paths switched off in the window, which the merge leaves out.</summary>
    private readonly HashSet<string> _disabledVideos;

    /// <param name="videoSettings">
    /// A snapshot of the per-video trim settings, in merge order. Taken by value so worker
    /// threads never read a collection the UI thread might be mutating.
    /// </param>
    /// <param name="audio">The library voice clips are drawn from. Omitted, nothing is injected.</param>
    public MergeCoordinator(
        IJobLogger logger,
        MergeOptions options,
        IReadOnlyList<VideoSegmentSettings> videoSettings,
        IProcessRunner? runner = null,
        IMediaProbe? probe = null,
        AudioLibrary? audio = null) {
        _logger = logger;
        _options = options;
        _trims = new TrimLookup(videoSettings);
        _configuredOrder = [.. videoSettings.Select(setting => setting.FilePath)];
        _disabledVideos = new HashSet<string>(
            videoSettings.Where(setting => !setting.Enabled).Select(setting => setting.FilePath),
            StringComparer.OrdinalIgnoreCase);
        _runner = runner ?? ProcessRunner.Default;
        // Built from the options rather than FFprobe.Default so a configured ffprobe path is
        // honoured; Default is hard-wired to the bare name and would quietly ignore it.
        _probe = probe ?? new FFprobe(_runner, options.FfprobePath);
        _audio = audio ?? AudioLibrary.Empty;
    }

    /// <summary>
    /// Executes the job. Returns <c>false</c> when a precondition stopped it before any
    /// work happened.
    /// </summary>
    public async Task<bool> RunAsync(
        IProgress<MergeProgress>? progress = null,
        CancellationToken cancellationToken = default) {
        // Named individually: with both configurable, "not found" has to say which one and
        // which path was tried, or a typo in the options reads as a missing install.
        foreach (string tool in new[] { _options.FfmpegPath, _options.FfprobePath }) {
            if (await _runner.CommandExistsAsync(tool, cancellationToken)) continue;

            _logger.Log(
                $"Error: could not run '{tool}'. Check the tool paths in Options, or that it is on PATH.",
                LogLevel.Error);

            return false;
        }

        if (!Directory.Exists(_options.InputFolder)) {
            _logger.Log($"Folder '{_options.InputFolder}' not found. Creating it...", LogLevel.Warning);
            Directory.CreateDirectory(_options.InputFolder);
            _logger.Log("Please put your files in that folder and run this again.", LogLevel.Warning);
            return false;
        }

        // Scanned here rather than inside SceneScriptIndex.Build(folder) so both listings come
        // from one walk of the folder, and so the ambiguity check below can see both of them.
        InputScan scan = _options.InputScan;
        List<string> scriptFiles = MediaFileScanner.FindFunscripts(_options.InputFolder, scan);
        List<string> scannedVideos = MediaFileScanner.FindVideos(
            _options.InputFolder, _options.VideoExtensions, scan);

        ReportAmbiguousNames(scannedVideos, scriptFiles);

        // Classified against every video, disabled ones included: a disabled video still owns
        // its scene name, and without it there its axis scripts would be read as scenes.
        List<SceneScripts> scenes = SceneScriptIndex.Build(scriptFiles, scannedVideos);
        List<string> videoFiles = InConfiguredOrder(LeaveOutDisabled(scannedVideos, scenes));

        // Checked before the plan: with no videos left it would fall back to a script-only
        // merge, and put back every scene the user had just switched off.
        if (scannedVideos.Count > 0 && videoFiles.Count == 0) {
            _logger.Log(
                "Nothing to merge: every video is disabled. Right-click one in the list to enable it.",
                LogLevel.Warning);
            return false;
        }

        if (scenes.Count == 0 && videoFiles.Count == 0) {
            _logger.Log(
                $"Nothing to merge: '{_options.InputFolder}' contains no funscripts and no videos.",
                LogLevel.Warning);
            return false;
        }

        TimelinePlan plan = TimelinePlan.Build(scenes, videoFiles, _options.SkipVideosWithoutScripts);
        await plan.ReportAsync(_logger, cancellationToken);

        if (plan.Entries.Count == 0) {
            _logger.Log(
                "Nothing left to merge once the unpaired files were left out.", LogLevel.Warning);

            return false;
        }

        // Taken from the plan rather than from the scan: the plan is what decided which videos
        // are on the timeline, and an encode working from a different list would put scenes in
        // the video that the merged script knows nothing about.
        List<string> plannedVideos = [.. plan.Entries.Select(entry => entry.VideoPath).OfType<string>()];

        // The chapter file is written by the video stage and consumed by its concat, so it
        // outlives neither on its own and its lifetime is owned here. Clearing it up front is
        // what guarantees a stale file from an earlier run can never be applied to this one.
        DeleteChapterMetadata();

        try {
            var scriptMerger = new FunscriptMerger(_logger, _options, _trims, _probe);
            FunscriptMergeResult? mergeResult = await scriptMerger.MergeAsync(plan.Entries, progress, cancellationToken);

            if (plannedVideos.Count > 0) {
                var videoMerger = new VideoMerger(_logger, _options, _trims, _runner, _probe, _audio);

                IReadOnlyList<EncodedSegment> segments;

                try {
                    segments = await videoMerger.MergeAsync(plannedVideos, mergeResult, progress, cancellationToken);
                } finally {
                    // Whatever the voice clips were measured at is worth keeping, even from a run
                    // that went on to fail: the next one will pick many of the same clips.
                    if (!_audio.TrySaveIndex(out string? error)) {
                        _logger.Log($"Could not save the audio index: {error}", LogLevel.Warning);
                    }
                }

                if (mergeResult is not null) await RetimeScriptAsync(mergeResult, segments, cancellationToken);
            }
        } finally {
            // Deleted on every path out, so no run - failed or cancelled - can leave a file
            // behind for the next one to pick up.
            DeleteChapterMetadata();
        }

        _logger.Log($"{Environment.NewLine}All operations complete!", LogLevel.Heading);
        return true;
    }

    /// <summary>
    /// Warns when the scan turned up one base name in more than one folder, which is the one
    /// ambiguity a recursive walk introduces: a scene is identified by filename, so two files
    /// of a name in different folders are read as the same scene.
    /// </summary>
    /// <remarks>
    /// Only for a recursive scan. A single folder holding both "Scene.mp4" and "Scene.mkv" is
    /// the same collision, but it is one the tool has always had and not one this option would
    /// be explaining.
    /// </remarks>
    private void ReportAmbiguousNames(IReadOnlyList<string> videoFiles, IReadOnlyList<string> scriptFiles) {
        if (!_options.IncludeInputSubfolders) return;

        if (MediaFileScanner.AmbiguousNameWarning(videoFiles, scriptFiles) is { } warning) {
            _logger.Log($"  -> {warning}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// The scanned videos minus the ones disabled in the window, with the disabled videos'
    /// scenes taken out of <paramref name="scenes"/> too.
    /// </summary>
    /// <remarks>
    /// The scenes go as well because the plan would otherwise find them unpaired and report
    /// them as funscripts missing a video, which is not what happened. A scene is kept while
    /// any enabled video still shares its name - with subfolders scanned, two videos can.
    /// </remarks>
    private List<string> LeaveOutDisabled(List<string> scanned, List<SceneScripts> scenes) {
        if (_disabledVideos.Count == 0) return scanned;

        List<string> enabled = [.. scanned.Where(path => !_disabledVideos.Contains(path))];

        var enabledNames = new HashSet<string>(
            enabled.Select(Path.GetFileNameWithoutExtension).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (string path in scanned.Where(_disabledVideos.Contains)) {
            string name = Path.GetFileNameWithoutExtension(path);

            if (!enabledNames.Contains(name)) {
                scenes.RemoveAll(scene => string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            _logger.Log($"  -> Skipping video '{name}': it is disabled in the video list.", LogLevel.Warning);
        }

        return enabled;
    }

    /// <summary>
    /// Puts the scanned videos into the order the caller arranged them in. The scan decides
    /// what exists - a file deleted since the window last refreshed is gone from the merge
    /// however the list still shows it - and the arrangement decides only where each one sits.
    /// </summary>
    /// <remarks>
    /// A video the snapshot says nothing about - dropped into the folder since the last
    /// refresh - sorts to the end in the scan's own ordinal order, so an unexpected file can
    /// never displace a scene the user placed deliberately.
    /// </remarks>
    private List<string> InConfiguredOrder(List<string> scanned) {
        if (_configuredOrder.Count == 0) return scanned;

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < _configuredOrder.Count; index++) {
            rank.TryAdd(_configuredOrder[index], index);
        }

        // OrderBy is stable, so the unranked tail keeps the order the scan gave it.
        return scanned
            .OrderBy(path => rank.TryGetValue(path, out int index) ? index : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Moves the merged script off the source videos' durations and onto the encoded segments'
    /// measured ones, then writes it again.
    /// </summary>
    /// <remarks>
    /// The script has to be merged before the encode so that a failed encode still leaves one
    /// behind, which means its offsets can only start out as the sources' durations. Correcting
    /// them here is what stops the frame-rounding difference between a source and its segment
    /// accumulating across the merge. A retime that cannot be trusted is not applied at all -
    /// the already-written script is then left alone and the residual drift reported instead.
    /// </remarks>
    private async Task RetimeScriptAsync(
        FunscriptMergeResult mergeResult,
        IReadOnlyList<EncodedSegment> segments,
        CancellationToken cancellationToken) {
        _logger.Log($"{Environment.NewLine}Retiming the script onto the encoded video...", LogLevel.Heading);

        ScriptRetimer.Result retime = ScriptRetimer.Retime(mergeResult.Document, mergeResult.Spans, segments);

        if (!retime.Applied) {
            _logger.Log(
                $"Could not retime the script because {retime.Reason}. It keeps the source videos' "
                + "timings, so it may drift from the merged video.",
                LogLevel.Warning);
            return;
        }

        _logger.Log(
            $"Scene starts corrected by up to {retime.MaxShiftMs}ms; the script now covers "
            + $"{retime.TotalDurationMs}ms, exactly matching the merged video.",
            LogLevel.Success);

        await new ScriptFileWriter(_logger, _options).WriteAsync(mergeResult.Document, cancellationToken);
    }

    /// <remarks>
    /// Reached from a finally block, so it must not throw and mask the failure that got it
    /// there. A file that survives a failed delete is cleared at the top of the next run
    /// anyway, which is what stops it being applied as stale chapters.
    /// </remarks>
    private void DeleteChapterMetadata() {
        try {
            if (File.Exists(_options.ChapterMetadataFile)) File.Delete(_options.ChapterMetadataFile);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _logger.Log(
                $"Could not remove {_options.ChapterMetadataFile}: {exception.Message}",
                LogLevel.Warning);
        }
    }
}
