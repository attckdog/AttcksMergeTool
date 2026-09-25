using AttcksMergeTool.Models;
using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

/// <summary>
/// Covers the encode/concat pass with ffmpeg and ffprobe faked out, so what is under test is
/// the ordering, the chapter boundaries and the failure handling rather than the encoder.
/// </summary>
public class VideoMergerTests
{
    /// <remarks>
    /// Segment numbers come from each video's position in the list, not from the order the
    /// parallel encodes happen to start in - otherwise the merged video and the merged script
    /// could disagree about which scene comes first.
    /// </remarks>
    [Fact]
    public async Task Each_source_encodes_into_the_segment_slot_matching_its_position() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(Each_source_encodes_into_the_segment_slot_matching_its_position));
        var runner = new FakeProcessRunner();

        await Merge(workspace, options, runner, Probe(2000, 5000, 3000), ["A.mp4", "B.mp4", "C.mp4"]);

        Assert.Equal("0001.mkv", SegmentFor(runner, "A.mp4"));
        Assert.Equal("0002.mkv", SegmentFor(runner, "B.mp4"));
        Assert.Equal("0003.mkv", SegmentFor(runner, "C.mp4"));
    }

    /// <remarks>
    /// Chapters describe the file that is actually produced. Deriving them from the sources
    /// instead leaves them a frame or two out per scene, and the error accumulates.
    /// </remarks>
    [Fact]
    public async Task Chapters_come_from_the_measured_segments() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(Chapters_come_from_the_measured_segments));

        await Merge(workspace, options, new FakeProcessRunner(), Probe(2000, 5000), ["A.mp4", "B.mp4"]);

        string metadata = workspace.ReadText("ffmetadata.txt");

        Assert.Contains("START=0\r\nEND=2000\r\ntitle=A", metadata.ReplaceLineEndings("\r\n"));
        Assert.Contains("START=2000\r\nEND=7000\r\ntitle=B", metadata.ReplaceLineEndings("\r\n"));
    }

    [Fact]
    public async Task The_chapter_file_is_handed_to_the_concat() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(The_chapter_file_is_handed_to_the_concat));
        var runner = new FakeProcessRunner();

        await Merge(workspace, options, runner, Probe(2000), ["A.mp4"]);

        FakeProcessRunner.Invocation concat = runner.Invocations.Last();

        Assert.Contains("-map_metadata", concat.Arguments);
        Assert.Contains(options.ChapterMetadataFile, concat.Arguments);
    }

    [Fact]
    public async Task An_unmeasurable_segment_falls_back_to_the_scripts_own_offsets() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(An_unmeasurable_segment_falls_back_to_the_scripts_own_offsets));
        var logger = new FakeJobLogger();

        FunscriptMergeResult scriptResult = MergeResults.WithBookmarks(
            7000, new Bookmark { Name = "A", Time = 0 }, new Bookmark { Name = "B", Time = 2000 });

        await Merge(workspace, options, new FakeProcessRunner(), Probe(2000, null), ["A.mp4", "B.mp4"], scriptResult, logger);

        Assert.True(logger.WarnedAbout("Could not measure every encoded segment"));
        Assert.Contains("title=B", workspace.ReadText("ffmetadata.txt"));
    }

    /// <remarks>
    /// The caller retimes the merged script against these, so a segment order or length that
    /// did not come back would silently leave the script on the sources' timings.
    /// </remarks>
    [Fact]
    public async Task The_measured_segments_come_back_in_concat_order() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(The_measured_segments_come_back_in_concat_order));

        IReadOnlyList<EncodedSegment> segments = await Merge(
            workspace, options, new FakeProcessRunner(),
            new FakeMediaProbe().WithDuration("0001.mkv", 2048).WithDuration("0002.mkv", 5120),
            ["A.mp4", "B.mp4"]);

        Assert.Equal(["A", "B"], segments.Select(segment => segment.SceneName));
        Assert.Equal([2048, 5120], segments.Select(segment => segment.DurationMs));
    }

    /// <remarks>
    /// The lengths come from the script merge rather than being worked out again here, because
    /// the merged script has already placed its keyframes inside them.
    /// </remarks>
    [Fact]
    public async Task A_gap_is_generated_in_front_of_every_scene_the_script_left_room_for() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(
            nameof(A_gap_is_generated_in_front_of_every_scene_the_script_left_room_for));

        var runner = new FakeProcessRunner();

        IReadOnlyList<EncodedSegment> segments = await Merge(
            workspace, options, runner, Probe(2000, 1000, 5000, 1000, 3000), ["A.mp4", "B.mp4", "C.mp4"],
            MergeResults.WithGaps(sceneMs: 2000, gapMs: 1000, "A", "B", "C"));

        // Scene, gap, scene, gap, scene - and the gaps are not scenes.
        Assert.Equal([false, true, false, true, false], segments.Select(segment => segment.IsGap));
        Assert.Equal(["A", "B", "B", "C", "C"], segments.Select(segment => segment.SceneName));

        // Numbered by position in the plan rather than by the order the parallel encodes
        // happened to start in, so the concat order is the plan order.
        Assert.Equal(
            ["0001.mkv", "0002.mkv", "0003.mkv", "0004.mkv", "0005.mkv"],
            segments.Select(segment => Path.GetFileName(segment.SegmentPath)));

        // The two gaps are the two lavfi invocations, and each asked for the planned length.
        List<string[]> black = [
            .. runner.Invocations
                .Select(invocation => invocation.Arguments.ToArray())
                .Where(arguments => arguments.Contains("lavfi"))
        ];

        Assert.Equal(2, black.Count);
        Assert.All(black, arguments => Assert.Equal("1", arguments[Array.IndexOf(arguments, "-t") + 1]));
    }

    /// <remarks>
    /// Read while the concat is running, because the list file is a scratch file the merge
    /// deletes on its way out.
    /// </remarks>
    [Fact]
    public async Task The_generated_gaps_take_their_place_in_the_concat_list() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(The_generated_gaps_take_their_place_in_the_concat_list));

        string[] listed = [];

        var runner = new FakeProcessRunner {
            Respond = invocation => {
                if (invocation.Arguments.Contains("concat")) listed = File.ReadAllLines(options.ConcatListFile);
                return string.Empty;
            }
        };

        await Merge(
            workspace, options, runner, Probe(2000, 1000, 5000), ["A.mp4", "B.mp4"],
            MergeResults.WithGaps(sceneMs: 2000, gapMs: 1000, "A", "B"));

        // The gap is the middle line: it is concatenated as a segment like any other.
        Assert.Equal(
            ["0001.mkv", "0002.mkv", "0003.mkv"],
            listed.Select(line => Path.GetFileName(line.TrimEnd('\'')).Trim()));
    }

    /// <remarks>
    /// There are no axes to move, so there is nothing for a gap to buy time for.
    /// </remarks>
    [Fact]
    public async Task A_run_with_no_script_gets_no_gaps() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(A_run_with_no_script_gets_no_gaps));

        IReadOnlyList<EncodedSegment> segments = await Merge(
            workspace, options, new FakeProcessRunner(), Probe(2000, 5000), ["A.mp4", "B.mp4"]);

        Assert.All(segments, segment => Assert.False(segment.IsGap));
    }

    /// <remarks>
    /// Lead-ins are read out of the spans by position, so a list that does not line up is not
    /// one they can be trusted from. Better a merge with no gaps than one whose black lands in
    /// the wrong places.
    /// </remarks>
    [Fact]
    public async Task Spans_that_do_not_line_up_with_the_videos_are_not_read_for_lead_ins() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(Spans_that_do_not_line_up_with_the_videos_are_not_read_for_lead_ins));
        var logger = new FakeJobLogger();

        IReadOnlyList<EncodedSegment> segments = await Merge(
            workspace, options, new FakeProcessRunner(), Probe(2000, 5000), ["A.mp4", "B.mp4"],
            MergeResults.WithGaps(sceneMs: 2000, gapMs: 1000, "A", "B", "C"),
            logger);

        Assert.All(segments, segment => Assert.False(segment.IsGap));
        Assert.True(logger.WarnedAbout("no transition gaps"));
    }

    [Fact]
    public async Task A_successful_run_clears_its_scratch_files() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(A_successful_run_clears_its_scratch_files));

        await Merge(workspace, options, new FakeProcessRunner(), Probe(2000), ["A.mp4"]);

        Assert.False(Directory.Exists(options.TempFolder));
        Assert.False(File.Exists(options.ConcatListFile));
    }

    /// <remarks>
    /// Nothing resumes from a kept segment, so leaving the folder behind only cost the user
    /// disk. The cleanup runs from a finally block, which is what gets it here at all.
    /// </remarks>
    [Fact]
    public async Task A_failed_concat_still_clears_its_scratch_files() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(A_failed_concat_still_clears_its_scratch_files));

        var runner = new FakeProcessRunner {
            Respond = invocation => invocation.Arguments.Contains("concat")
                ? throw new ExternalToolException("ffmpeg", 1, "Invalid data found when processing input")
                : string.Empty
        };

        ExternalToolException failure = await Assert.ThrowsAsync<ExternalToolException>(
            () => Merge(workspace, options, runner, Probe(2000), ["A.mp4"]));

        // The cleanup must not have replaced ffmpeg's own diagnostics with an IO error.
        Assert.Contains("Invalid data found", failure.Message);

        Assert.False(Directory.Exists(options.TempFolder));
        Assert.False(File.Exists(options.ConcatListFile));
    }

    private static FakeMediaProbe Probe(params int?[] segmentDurations) {
        var probe = new FakeMediaProbe();

        for (int index = 0; index < segmentDurations.Length; index++) {
            probe.WithDuration($"{index + 1:D4}.mkv", segmentDurations[index]);
        }

        return probe;
    }

    private static string? SegmentFor(FakeProcessRunner runner, string sourceName) =>
        runner.Invocations
            .Where(invocation => invocation.Arguments.Any(argument => argument.EndsWith(sourceName, StringComparison.Ordinal)))
            .Select(invocation => Path.GetFileName(invocation.Arguments[^1]))
            .FirstOrDefault();

    /// <remarks>
    /// The tool paths are configurable, so what gets launched has to come from the options.
    /// Launching the literal "ffmpeg" would silently ignore a configured path and fail on any
    /// machine that does not have ffmpeg on PATH.
    /// </remarks>
    [Fact]
    public async Task The_configured_ffmpeg_is_what_gets_launched() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(
            nameof(The_configured_ffmpeg_is_what_gets_launched), @"C:\tools\ffmpeg\bin\ffmpeg.exe");

        var runner = new FakeProcessRunner();

        await Merge(workspace, options, runner, Probe(2000), ["A.mp4"]);

        Assert.NotEmpty(runner.Invocations);
        Assert.All(
            runner.Invocations,
            invocation => Assert.Equal(@"C:\tools\ffmpeg\bin\ffmpeg.exe", invocation.FileName));
    }

    private static async Task<IReadOnlyList<EncodedSegment>> Merge(
        TempWorkspace workspace,
        MergeOptions options,
        FakeProcessRunner runner,
        FakeMediaProbe probe,
        string[] videoNames,
        FunscriptMergeResult? scriptResult = null,
        FakeJobLogger? logger = null) {
        List<string> videos = [.. videoNames.Select(workspace.WriteVideo)];

        var merger = new VideoMerger(logger ?? new FakeJobLogger(), options, TrimLookup.Empty, runner, probe);

        return await merger.MergeAsync(videos, scriptResult);
    }
}
