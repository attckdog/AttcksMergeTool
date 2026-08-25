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

    [Fact]
    public void Total_duration_is_where_the_last_chapter_ends() {
        Assert.Equal(0, ChapterBuilder.TotalDurationMs([]));
        Assert.Equal(7000, ChapterBuilder.TotalDurationMs([new Chapter("A", 0, 2000), new Chapter("B", 2000, 7000)]));
    }

    private static EncodedSegment Segment(string sourceName, int? durationMs) =>
        new(Path.Combine(@"C:\in", sourceName), Path.Combine(@"C:\temp", "0001.mkv"), durationMs);

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
