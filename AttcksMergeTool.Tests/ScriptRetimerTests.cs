using AttcksMergeTool.Models;
using AttcksMergeTool.Services;

namespace AttcksMergeTool.Tests;

/// <summary>
/// The correction that stops the difference between a source video and the segment it encodes
/// to accumulating across a merge - about 21ms per scene on the run that prompted it, which is
/// nothing at the first chapter and nearly a minute out by the last.
/// </summary>
public class ScriptRetimerTests
{
    [Fact]
    public void Every_scene_is_shifted_onto_its_segments_real_start() {
        Funscript document = Document(
            Actions((0, 0), (1000, 100), (2000, 0), (2500, 50)),
            Marks(("A", 0), ("B", 2000)));

        ScriptRetimer.Result result = ScriptRetimer.Retime(
            document,
            [new SceneSpan("A", 0, 2000), new SceneSpan("B", 2000, 5000)],
            [Segment("A.mp4", 2048), Segment("B.mp4", 5120)]);

        Assert.True(result.Applied);
        Assert.Equal(7168, result.TotalDurationMs);
        Assert.Equal(48, result.MaxShiftMs);

        // Scene A does not move; scene B and everything in it moves by A's 48ms of padding.
        Assert.Equal([0, 1000, 2048, 2548], document.Actions!.Select(action => action.At));
        Assert.Equal([0, 2048], document.Bookmarks!.Select(bookmark => bookmark.Time));
        Assert.Equal(7, document.Metadata!.Duration);
    }

    /// <remarks>
    /// The scenes are laid end to end, so a shift is not a constant - it is the sum of every
    /// earlier scene's difference, which is exactly why the old error accumulated.
    /// </remarks>
    [Fact]
    public void The_correction_accumulates_across_scenes() {
        Funscript document = Document(Actions((0, 0), (100, 10), (200, 20)), Marks(("A", 0), ("B", 100), ("C", 200)));

        ScriptRetimer.Result result = ScriptRetimer.Retime(
            document,
            [new SceneSpan("A", 0, 100), new SceneSpan("B", 100, 100), new SceneSpan("C", 200, 100)],
            [Segment("A.mp4", 120), Segment("B.mp4", 120), Segment("C.mp4", 120)]);

        Assert.True(result.Applied);
        Assert.Equal([0, 120, 240], document.Actions!.Select(action => action.At));
        Assert.Equal(40, result.MaxShiftMs);
    }

    [Fact]
    public void Auxiliary_axes_move_with_their_scene() {
        Funscript document = Document(Actions((0, 0)), Marks(("A", 0), ("B", 2000)));
        document.Axes = [new FunscriptAxis { Id = "R0", Actions = [Point(500), Point(2000), Point(2400)] }];

        Assert.True(ScriptRetimer
            .Retime(document, [new SceneSpan("A", 0, 2000), new SceneSpan("B", 2000, 1000)],
                [Segment("A.mp4", 2048), Segment("B.mp4", 1000)])
            .Applied);

        Assert.Equal([500, 2048, 2448], document.Axes[0].Actions!.Select(action => action.At));
    }

    /// <remarks>
    /// A scene whose keyframes run past the length it was allotted - which the merge permits -
    /// would otherwise be timestamped after the next scene's opening keyframe once the two got
    /// different corrections. Every player assumes the list is sorted.
    /// </remarks>
    [Fact]
    public void Keyframes_stay_in_order_when_a_scene_overruns_its_own_length() {
        Funscript document = Document(Actions((0, 0), (2500, 50), (2000, 0)), Marks(("A", 0), ("B", 2000)));

        Assert.True(ScriptRetimer
            .Retime(document, [new SceneSpan("A", 0, 2000), new SceneSpan("B", 2000, 1000)],
                [Segment("A.mp4", 1800), Segment("B.mp4", 1000)])
            .Applied);

        int[] times = [.. document.Actions!.Select(action => action.At)];

        Assert.Equal(times, times.Order());
    }

    [Fact]
    public void An_unmeasured_segment_leaves_the_document_alone() {
        Funscript document = Document(Actions((0, 0), (1000, 100)), Marks(("A", 0)));

        ScriptRetimer.Result result = ScriptRetimer.Retime(
            document, [new SceneSpan("A", 0, 2000)], [Segment("A.mp4", null)]);

        Assert.False(result.Applied);
        Assert.Contains("could not be measured", result.Reason);
        Assert.Equal([0, 1000], document.Actions!.Select(action => action.At));
    }

    [Fact]
    public void A_count_that_does_not_match_leaves_the_document_alone() {
        Funscript document = Document(Actions((0, 0)), Marks(("A", 0)));

        ScriptRetimer.Result result = ScriptRetimer.Retime(
            document, [new SceneSpan("A", 0, 2000)], [Segment("A.mp4", 2048), Segment("B.mp4", 1000)]);

        Assert.False(result.Applied);
        Assert.Contains("1 scenes", result.Reason);
    }

    /// <remarks>
    /// Position is what the concat honours, but a name that disagrees means the two lists were
    /// built from different things - so every offset below it would be wrong too.
    /// </remarks>
    [Fact]
    public void A_segment_naming_a_different_scene_leaves_the_document_alone() {
        Funscript document = Document(Actions((0, 0)), Marks(("A", 0)));

        ScriptRetimer.Result result = ScriptRetimer.Retime(
            document, [new SceneSpan("A", 0, 2000)], [Segment("Elsewhere.mp4", 2048)]);

        Assert.False(result.Applied);
        Assert.Contains("Elsewhere", result.Reason);
    }

    [Fact]
    public void Nothing_to_retime_is_reported_rather_than_applied() {
        Assert.False(ScriptRetimer.Retime(Document([], []), [], []).Applied);
    }

    private static Funscript Document(List<ActionPoint> actions, List<Bookmark> bookmarks) => new() {
        Actions = actions,
        Bookmarks = bookmarks,
        Metadata = new FunscriptMetadata()
    };

    private static List<ActionPoint> Actions(params (int At, int Pos)[] points) =>
        [.. points.Select(point => new ActionPoint { At = point.At, Pos = point.Pos })];

    private static List<Bookmark> Marks(params (string Name, int TimeMs)[] marks) =>
        [.. marks.Select(mark => new Bookmark { Name = mark.Name, Time = mark.TimeMs })];

    private static ActionPoint Point(int atMs) => new() { At = atMs, Pos = 50 };

    private static EncodedSegment Segment(string sourceName, int? durationMs) =>
        new(Path.Combine(@"C:\in", sourceName), Path.Combine(@"C:\temp", "0001.mkv"), durationMs);
}
