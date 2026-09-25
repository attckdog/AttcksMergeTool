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

    public VideoMerger(
        IJobLogger logger,
        MergeOptions options,
        TrimLookup trims,
        IProcessRunner? runner = null,
        IMediaProbe? probe = null) {
        _logger = logger;
        _options = options;
        _trims = trims;
        _runner = runner ?? ProcessRunner.Default;
        _probe = probe ?? FFprobe.Default;
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

                _logger.Log($"Encoding: {Path.GetFileName(job.SourcePath)}");

                await _runner.RunAsync(
                    _options.FfmpegPath,
                    FFmpegArguments.BuildEncode(job.SourcePath, segmentPath, trim, _options),
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
