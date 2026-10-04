using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Normalizes every input video into a uniform intermediate segment (in parallel),
/// then stream-copies the segments together into the final output with chapters
/// attached. Re-encoding up front is what makes the lossless concat possible.
/// </summary>
/// <remarks>
/// Generated stretches of black are interleaved between the scenes, one wherever the merged
/// script asked for a transition. They are encoded exactly like a real segment - same encoder,
/// container and audio layout - so the concat cannot tell them apart, and they are measured
/// like one too, because the time they occupy has to be accounted for by everything
/// downstream that walks the output's timeline.
/// </remarks>
public sealed class VideoMerger
{
    private readonly IJobLogger _logger;
    private readonly MergeOptions _options;
    private readonly TrimLookup _trims;
    private readonly IProcessRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly AudioLibrary _audio;

    /// <param name="audio">
    /// Where voice clips are drawn from for videos that inject them. Omitted, nothing is injected.
    /// </param>
    public VideoMerger(
        IJobLogger logger,
        MergeOptions options,
        TrimLookup trims,
        IProcessRunner? runner = null,
        IMediaProbe? probe = null,
        AudioLibrary? audio = null) {
        _logger = logger;
        _options = options;
        _trims = trims;
        _runner = runner ?? ProcessRunner.Default;
        _probe = probe ?? FFprobe.Default;
        _audio = audio ?? AudioLibrary.Empty;
    }

    /// <param name="scriptResult">
    /// The script merge, used only as the fallback source of chapter boundaries when a
    /// segment cannot be measured. Null for a video-only run.
    /// </param>
    /// <returns>
    /// The measured segments, in concat order. They are what the caller retimes the merged
    /// script against, so the script's scene starts land exactly on the video's.
    /// </returns>
    public async Task<IReadOnlyList<EncodedSegment>> MergeAsync(
        IReadOnlyList<string> videoFiles,
        FunscriptMergeResult? scriptResult = null,
        IProgress<MergeProgress>? progress = null,
        CancellationToken cancellationToken = default) {
        _logger.LogSection($" Step 2: Merging Videos (Parallel x{_options.MaxParallelEncodes})", leadingBlankLine: true);

        PrepareWorkspace();

        IReadOnlyList<EncodedSegment> segments;

        try {
            segments = await EncodeSegmentsAsync(
                PlanJobs(videoFiles, scriptResult), progress, cancellationToken);

            // Chapters sit between the two phases because they describe the segments the
            // concat is about to join, and the concat is what consumes the file they go in.
            WriteChapters(segments, scriptResult);

            await ConcatenateAsync(segments, progress, cancellationToken);
        } finally {
            CleanupTempFiles();
        }

        return segments;
    }

    private void PrepareWorkspace() {
        if (File.Exists(_options.ConcatListFile)) File.Delete(_options.ConcatListFile);
        if (!Directory.Exists(_options.TempFolder)) Directory.CreateDirectory(_options.TempFolder);
    }

    /// <summary>
    /// The segments to produce, in concat order: each video, preceded by a stretch of black
    /// wherever the merged script left room for one.
    /// </summary>
    /// <remarks>
    /// The gap lengths come from the script merge rather than being recomputed here, because
    /// they were already spent - the merged script placed its keyframes inside them. Working
    /// them out a second time would risk the two halves disagreeing about a length only one of
    /// them can be right about.
    /// <para>
    /// A run with no script has no axes to move and so no gaps; a run whose spans do not line
    /// up with its videos is not one this can safely read lead-ins out of, so it gets none
    /// either and says so.
    /// </para>
    /// </remarks>
    private List<EncodeJob> PlanJobs(IReadOnlyList<string> videoFiles, FunscriptMergeResult? scriptResult) {
        IReadOnlyList<SceneSpan>? spans = scriptResult?.Spans;

        if (spans is not null && spans.Count != videoFiles.Count) {
            _logger.Log(
                $"The merged script covers {spans.Count} scenes but the video is being built "
                + $"from {videoFiles.Count}. Scenes will be joined with no transition gaps.",
                LogLevel.Warning);

            spans = null;
        }

        var jobs = new List<EncodeJob>(videoFiles.Count);

        for (int index = 0; index < videoFiles.Count; index++) {
            int leadInMs = spans?[index].LeadInMs ?? 0;

            // Ahead of the scene, matching where the script put the keyframes that cross it.
            if (leadInMs > 0) jobs.Add(new EncodeJob(videoFiles[index], leadInMs));

            jobs.Add(new EncodeJob(videoFiles[index], 0));
        }

        return jobs;
    }

    /// <summary>
    /// Produces each planned segment in a temp file, up to
    /// <see cref="MergeOptions.MaxParallelEncodes"/> at a time, and measures what came out.
    /// </summary>
    private async Task<IReadOnlyList<EncodedSegment>> EncodeSegmentsAsync(
        IReadOnlyList<EncodeJob> jobs,
        IProgress<MergeProgress>? progress,
        CancellationToken cancellationToken) {
        int completed = 0;

        // Indexed by position in the plan, so the concat order matches the script merge, which
        // walks the same scenes and the same gaps in the same order. Numbering at dispatch
        // time instead would follow scheduling order and could desync the two.
        var segments = new EncodedSegment[jobs.Count];

        progress?.Report(new MergeProgress(0, jobs.Count));

        var parallelOptions = new ParallelOptions {
            MaxDegreeOfParallelism = _options.MaxParallelEncodes,
            CancellationToken = cancellationToken
        };

        IEnumerable<(EncodeJob Job, int Index)> sources = jobs.Select((job, index) => (job, index));

        await Parallel.ForEachAsync(sources, parallelOptions, async (source, token) => {
            (EncodeJob job, int index) = source;

            string segmentName = $"{index + 1:D4}{FFmpegArguments.TempSegmentExtension(_options.UseAv1)}";
            string segmentPath = Path.Combine(_options.TempFolder, segmentName);

            if (job.IsGap) {
                _logger.Log($"Generating: {job.GapMs}ms transition before {Path.GetFileName(job.SourcePath)}");

                await _runner.RunAsync(
                    _options.FfmpegPath,
                    FFmpegArguments.BuildBlackSegment(segmentPath, job.GapMs, _options),
                    token);
            } else {
                VideoSegmentSettings? trim = _trims.ForFile(job.SourcePath);
                bool hasAudio = await _probe.HasAudioAsync(job.SourcePath, token);

                _logger.Log(hasAudio
                    ? $"Encoding: {Path.GetFileName(job.SourcePath)}"
                    : $"Encoding: {Path.GetFileName(job.SourcePath)} (no audio track, adding silence)");

                VoiceMix? voice = trim?.Voice is { IsActive: true } injection
                    ? await RenderVoiceAsync(job.SourcePath, trim, injection, index, token)
                    : null;

                await _runner.RunAsync(
                    _options.FfmpegPath,
                    FFmpegArguments.BuildEncode(job.SourcePath, segmentPath, trim, _options, hasAudio, voice),
                    token);
            }

            // Measured rather than inherited from the source: forcing a common frame rate and
            // rounding the trim to frames both move the boundary, and chapters built from the
            // source durations would drift a little further with every scene. A gap is
            // measured for the same reason - what it asked for and what ffmpeg wrote differ.
            int? durationMs = await _probe.GetDurationMsAsync(segmentPath, token);

            // Each slot is written by exactly one iteration, so no synchronization is needed.
            segments[index] = new EncodedSegment(job.SourcePath, segmentPath, durationMs, job.IsGap);

            progress?.Report(new MergeProgress(Interlocked.Increment(ref completed), jobs.Count));
        });

        return segments;
    }

    /// <summary>
    /// Picks the voice clips for one scene and renders them to tracks its encode can mix in,
    /// or returns <c>null</c> when there turns out to be nothing to mix.
    /// </summary>
    /// <remarks>
    /// The tracks go in the temp folder beside the segments, so the cleanup that removes those
    /// removes these too. Each job seeds its own generator: the encodes run in parallel, and a
    /// <see cref="Random"/> is not safe to share between them.
    /// </remarks>
    private async Task<VoiceMix?> RenderVoiceAsync(
        string sourcePath,
        VideoSegmentSettings settings,
        VoiceInjection injection,
        int index,
        CancellationToken cancellationToken) {
        string name = Path.GetFileName(sourcePath);

        foreach (string missing in _audio.MissingFolders(injection.Folders)) {
            _logger.Log($"Voice folder not found in the audio library: {missing}", LogLevel.Warning);
        }

        IReadOnlyList<string> pool = _audio.FilesUnder(injection.Folders);

        if (pool.Count == 0) {
            _logger.Log($"No voice clips to inject into {name}; its audio is left as it is.", LogLevel.Warning);
            return null;
        }

        int? windowMs = await SceneLengthMsAsync(sourcePath, settings, cancellationToken);

        if (windowMs is not > 0) {
            _logger.Log($"Could not measure {name}, so no voice clips were injected into it.", LogLevel.Warning);
            return null;
        }

        IReadOnlyList<VoicePlacement> placements = await VoicePlanner.PlanAsync(
            pool,
            windowMs.Value,
            injection,
            new Random(Random.Shared.Next()),
            (path, token) => _audio.GetDurationMsAsync(path, _probe, token),
            cancellationToken);

        if (placements.Count == 0) {
            _logger.Log($"No voice clip fits inside {name}; its audio is left as it is.", LogLevel.Warning);
            return null;
        }

        _logger.Log($"Voice: {placements.Count} clip{(placements.Count == 1 ? "" : "s")} (from a pool of {pool.Count}) into {name}");

        foreach (VoicePlacement placement in placements) {
            _logger.Log($"    {placement.StartMs / 1000.0,7:0.0}s  {Path.GetRelativePath(_audio.RootPath, placement.Path)}");
        }

        var tracks = new List<string>();

        foreach (VoicePlacement[] chunk in placements.Chunk(FFmpegArguments.MaxClipsPerVoiceTrack)) {
            string trackPath = Path.Combine(_options.TempFolder, $"{index + 1:D4}_voice{tracks.Count + 1}.wav");

            await _runner.RunAsync(
                _options.FfmpegPath, FFmpegArguments.BuildVoiceTrack(chunk, trackPath, _options), cancellationToken);

            tracks.Add(trackPath);
        }

        return new VoiceMix(tracks, injection.VoiceVolumePercent, injection.OriginalVolumePercent);
    }

    /// <summary>
    /// How long a scene runs once trimmed, which is the time its voice clips have to fit in.
    /// The same arithmetic the script merge applies, so the two agree on the scene's length.
    /// </summary>
    private async Task<int?> SceneLengthMsAsync(
        string sourcePath,
        VideoSegmentSettings settings,
        CancellationToken cancellationToken) {
        int? durationMs = await _probe.GetDurationMsAsync(sourcePath, cancellationToken);

        if (durationMs is not > 0 || !settings.UseTrim) return durationMs;

        TrimWindow trim = TrimWindow.FromSeconds(settings.StartTime, settings.EndTime);
        int endMs = trim.EndMs > trim.StartMs ? Math.Min(trim.EndMs, durationMs.Value) : durationMs.Value;

        return Math.Max(0, endMs - trim.StartMs);
    }

    /// <summary>One segment to produce.</summary>
    /// <param name="SourcePath">
    /// The video to encode, or for a gap the video it runs in front of - which is only used to
    /// name it in the log and in the chapter that swallows it.
    /// </param>
    /// <param name="GapMs">
    /// How long a stretch of black to generate, or zero to encode <paramref name="SourcePath"/>
    /// itself.
    /// </param>
    private readonly record struct EncodeJob(string SourcePath, int GapMs)
    {
        public bool IsGap => GapMs > 0;
    }

    /// <summary>
    /// Builds the chapter list from the measured segments and writes it out, falling back to
    /// the script's own scene offsets if any segment could not be measured.
    /// </summary>
    private void WriteChapters(IReadOnlyList<EncodedSegment> segments, FunscriptMergeResult? scriptResult) {
        IReadOnlyList<Chapter> chapters = ChapterBuilder.FromSegments(segments);

        if (chapters.Count == 0 && scriptResult is not null) {
            _logger.Log(
                "Could not measure every encoded segment. Falling back to the merged script's "
                + "own scene offsets for chapters, which may be slightly off.",
                LogLevel.Warning);

            chapters = ChapterBuilder.FromBookmarks(scriptResult);
        }

        new ChapterFileWriter(_logger, _options).Write(chapters);
    }

    private async Task ConcatenateAsync(
        IReadOnlyList<EncodedSegment> segments,
        IProgress<MergeProgress>? progress,
        CancellationToken cancellationToken) {
        File.WriteAllLines(_options.ConcatListFile, segments.Select(segment => segment.ConcatEntry));

        _logger.Log($"{Environment.NewLine}Concatenating files and embedding chapters...", LogLevel.Heading);

        // ffmpeg gives no usable completion percentage for a stream copy.
        progress?.Report(MergeProgress.Indeterminate);

        string? chapterFile = File.Exists(_options.ChapterMetadataFile) ? _options.ChapterMetadataFile : null;
        List<string> args = FFmpegArguments.BuildConcat(
            _options.ConcatListFile, _options.OutputVideoPath, chapterFile, _options);

        await _runner.RunAsync(_options.FfmpegPath, args, cancellationToken);

        _logger.Log($"Video merge complete! Output: {_options.OutputVideoPath}", LogLevel.Success);
    }

    /// <summary>
    /// Single exit point for scratch-file removal. Runs on every path out of the merge -
    /// success, failure and cancellation alike - so a run never leaves intermediates behind.
    /// </summary>
    /// <remarks>
    /// Reached from a finally block, so it must not throw: a delete that failed while the
    /// encode's own exception was propagating would replace the real ffmpeg diagnostics with
    /// an unrelated IO error. A segment ffmpeg still holds open is the likely cause, and it
    /// is worth a warning rather than losing the job's actual failure. The chapter metadata
    /// file is not touched here - it outlives the video stage, so
    /// <see cref="MergeCoordinator"/> owns its lifetime.
    /// </remarks>
    private void CleanupTempFiles() {
        try {
            if (Directory.Exists(_options.TempFolder)) Directory.Delete(_options.TempFolder, true);
            if (File.Exists(_options.ConcatListFile)) File.Delete(_options.ConcatListFile);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _logger.Log(
                $"Could not remove the intermediate files in {_options.TempFolder}: {exception.Message}",
                LogLevel.Warning);
        }
    }
}
