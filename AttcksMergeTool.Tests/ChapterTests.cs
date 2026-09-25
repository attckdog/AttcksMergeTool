using AttcksMergeTool.Models;
using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

public class ChapterBuilderTests
{
    [Fact]
    public void Segments_are_laid_end_to_end_at_their_measured_lengths() {
        IReadOnlyList<Chapter> chapters = ChapterBuilder.FromSegments([
            Segment("A.mp4", 2000),
            Segment("B.mp4", 5000),
            Segment("C.mp4", 3000)
        ]);

        Assert.Equal(
            [new Chapter("A", 0, 2000), new Chapter("B", 2000, 7000), new Chapter("C", 7000, 10_000)],
            chapters);
    }

    /// <remarks>
    /// One unknown length invalidates every boundary after it, so there is no useful partial
    /// answer - the caller falls back to the script's offsets instead.
    /// </remarks>
    [Fact]
    public void An_unmeasured_segment_invalidates_the_whole_list() {
        Assert.Empty(ChapterBuilder.FromSegments([Segment("A.mp4", 2000), Segment("B.mp4", null)]));
        Assert.Empty(ChapterBuilder.FromSegments([Segment("A.mp4", 0)]));
    }

    [Fact]
    public void Bookmarks_span_from_each_marker_to_the_next() {
        FunscriptMergeResult result = MergeResults.WithBookmarks(
            10_000, Mark("A", 0), Mark("B", 2000), Mark("C", 7000));

        Assert.Equal(
            [new Chapter("A", 0, 2000), new Chapter("B", 2000, 7000), new Chapter("C", 7000, 10_000)],
            ChapterBuilder.FromBookmarks(result));
    }

    [Fact]
    public void A_zero_length_scene_gets_no_chapter() {
        FunscriptMergeResult result = MergeResults.WithBookmarks(
            5000, Mark("A", 0), Mark("Empty", 2000), Mark("B", 2000));

        Assert.Equal(
            [new Chapter("A", 0, 2000), new Chapter("B", 2000, 5000)],
            ChapterBuilder.FromBookmarks(result));
    }

    /// <remarks>
    /// A gap is black rather than content, so a chapter of its own would be a navigation target
    /// that shows nothing. Folding it into the chapter before it also keeps every scene's
    /// chapter starting on its first real frame.
    /// </remarks>
    [Fact]
    public void A_gap_extends_the_chapter_before_it_rather_than_starting_one() {
        IReadOnlyList<Chapter> chapters = ChapterBuilder.FromSegments([
            Segment("A.mp4", 2000),
            Gap("B.mp4", 1000),
            Segment("B.mp4", 5000)
        ]);

        Assert.Equal([new Chapter("A", 0, 3000), new Chapter("B", 3000, 8000)], chapters);
    }

    /// <remarks>
    /// The chapters have to describe the whole file, so the time the gaps take still has to be
    /// somewhere - they just do not get a title of their own.
    /// </remarks>
    [Fact]
    public void Gaps_keep_the_chapters_contiguous_and_covering_the_whole_output() {
        EncodedSegment[] segments = [
            Segment("A.mp4", 2000),
            Gap("B.mp4", 1000),
            Segment("B.mp4", 5000),
            Gap("C.mp4", 800),
            Segment("C.mp4", 3000)
        ];

        IReadOnlyList<Chapter> chapters = ChapterBuilder.FromSegments(segments);

        Assert.Equal(3, chapters.Count);
        Assert.Equal(0, chapters[0].StartMs);
        Assert.Equal(
            segments.Sum(segment => segment.DurationMs!.Value), ChapterBuilder.TotalDurationMs(chapters));

        // No hole and no overlap: each chapter picks up exactly where the last one left off.
        Assert.All(chapters.Zip(chapters.Skip(1)), pair => Assert.Equal(pair.First.EndMs, pair.Second.StartMs));
    }

    /// <remarks>
    /// The merger never emits one - there is nothing to ease out of before the first scene -
    /// but if it ever did, the black must not take the first scene's title with it.
    /// </remarks>
    [Fact]
    public void A_leading_gap_belongs_to_no_chapter() {
        IReadOnlyList<Chapter> chapters = ChapterBuilder.FromSegments([
            Gap("A.mp4", 500),
            Segment("A.mp4", 2000)
        ]);

        Assert.Equal([new Chapter("A", 500, 2500)], chapters);
    }

    [Fact]
    public void Total_duration_is_where_the_last_chapter_ends() {
        Assert.Equal(0, ChapterBuilder.TotalDurationMs([]));
        Assert.Equal(7000, ChapterBuilder.TotalDurationMs([new Chapter("A", 0, 2000), new Chapter("B", 2000, 7000)]));
    }

    private static EncodedSegment Segment(string sourceName, int? durationMs) =>
        new(Path.Combine(@"C:\in", sourceName), Path.Combine(@"C:\temp", "0001.mkv"), durationMs);

    /// <summary>Black in front of <paramref name="followingSourceName"/>.</summary>
    private static EncodedSegment Gap(string followingSourceName, int? durationMs) =>
        Segment(followingSourceName, durationMs) with { IsGap = true };

    private static Bookmark Mark(string name, int timeMs) => new() { Name = name, Time = timeMs };
}

public class ChapterFileWriterTests
{
    [Fact]
    public void Each_chapter_becomes_an_ffmetadata_block() {
        using var workspace = new TempWorkspace();
        MergeOptions options = workspace.Options(nameof(Each_chapter_becomes_an_ffmetadata_block));

        new ChapterFileWriter(new FakeJobLogger(), options)
            .Write([new Chapter("First", 0, 2000), new Chapter("Second", 2000, 7000)]);

        string[] lines = workspace.ReadText("ffmetadata.txt").ReplaceLineEndings("\n").Split('\n');

        Assert.Equal(";FFMETADATA1", lines[0]);
        Assert.Equal($"title={options.OutputName}", lines[1]);
        Assert.Equal(
            ["[CHAPTER]", "TIMEBASE=1/1000", "START=0", "END=2000", "title=First"],
            lines[2..7]);
        Assert.Equal(
            ["[CHAPTER]", "TIMEBASE=1/1000", "START=2000", "END=7000", "title=Second"],
            lines[7..12]);
    }

    [Fact]
    public void Nothing_is_written_when_there_are_no_chapters() {
        using var workspace = new TempWorkspace();

        new ChapterFileWriter(new FakeJobLogger(), workspace.Options(nameof(Nothing_is_written_when_there_are_no_chapters)))
            .Write([]);

        Assert.False(workspace.Exists("ffmetadata.txt"));
    }

    /// <remarks>
    /// ffmpeg rejects the whole metadata file over one bad span, which would cost every other
    /// scene its chapter as well.
    /// </remarks>
    [Fact]
    public void An_empty_or_inverted_span_is_skipped_rather_than_written() {
        using var workspace = new TempWorkspace();

        new ChapterFileWriter(new FakeJobLogger(), workspace.Options(nameof(An_empty_or_inverted_span_is_skipped_rather_than_written)))
            .Write([new Chapter("Empty", 1000, 1000), new Chapter("Backwards", 5000, 2000), new Chapter("Good", 0, 1000)]);

        string metadata = workspace.ReadText("ffmetadata.txt");

        Assert.DoesNotContain("Empty", metadata);
        Assert.DoesNotContain("Backwards", metadata);
        Assert.Contains("title=Good", metadata);
    }

    [Fact]
    public void The_file_is_written_without_a_byte_order_mark() {
        using var workspace = new TempWorkspace();

        new ChapterFileWriter(new FakeJobLogger(), workspace.Options(nameof(The_file_is_written_without_a_byte_order_mark)))
            .Write([new Chapter("First", 0, 2000)]);

        byte[] bytes = File.ReadAllBytes(workspace.Path("ffmetadata.txt"));

        Assert.Equal((byte)';', bytes[0]);
    }
}
