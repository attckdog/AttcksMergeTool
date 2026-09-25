namespace AttcksMergeTool.Services;

/// <summary>
/// How long the black stretch between two scenes has to be for every axis to get where the
/// next scene opens without exceeding a maximum speed.
/// </summary>
/// <remarks>
/// Scenes used to be butt-joined, which left the seam no time at all: the device was asked to
/// jump from wherever one scene ended to wherever the next opens, and collapsing the incoming
/// keyframes only spread that jump over the window - it did not make it slower.
/// <para>
/// The gap buys the time instead. Every axis parks at <see cref="MidPosition"/> halfway
/// through, so the move splits into two legs of equal duration: out of the previous scene's
/// final position, and into the next scene's first. The longest single leg across all axes is
/// what the gap has to cover, since they all move at once. One keyframe per axis is enough -
/// players interpolate between points, so the ease is theirs to draw.
/// </para>
/// </remarks>
public static class TransitionGap
{
    /// <summary>Where every axis is parked in the middle of the gap.</summary>
    public const int MidPosition = 50;

    /// <summary>
    /// Shortest gap worth generating once any movement is needed.
    /// </summary>
    /// <remarks>
    /// A gap of a frame or two reads as a glitch in the video rather than a transition, and
    /// the travel it buys is not worth that. Anything that has to move at all gets at least
    /// this long; something already parked at <see cref="MidPosition"/> on both sides gets no
    /// gap at all.
    /// </remarks>
    public const int MinGapMs = 100;

    /// <summary>
    /// The gap that must precede a scene whose axes open at <paramref name="nextPositions"/>,
    /// given they currently sit at <paramref name="lastPositions"/>. Zero when nothing has to
    /// move.
    /// </summary>
    /// <param name="lastPositions">
    /// Final position per axis from the previous scene. An axis missing here has not appeared
    /// yet, so its first leg costs nothing - there is nowhere it has to travel from.
    /// </param>
    /// <param name="nextPositions">
    /// First position per axis in the upcoming scene. An axis missing here is not scripted by
    /// it, so its second leg costs nothing: it parks at <see cref="MidPosition"/> and holds.
    /// </param>
    /// <param name="maxAxisSpeed">Ceiling on axis travel, in position units per second.</param>
    /// <param name="targetFps">
    /// Frame rate the generated segment is encoded at. The gap is rounded up to a whole number
    /// of frames so the black lands on a frame boundary rather than being truncated back below
    /// the speed limit it was sized for.
    /// </param>
    public static int DurationMs(
        IReadOnlyDictionary<string, int> lastPositions,
        IReadOnlyDictionary<string, int> nextPositions,
        int maxAxisSpeed,
        int targetFps) {
        if (maxAxisSpeed <= 0) return 0;

        int longestLeg = 0;

        foreach (string axisId in ActiveAxes(lastPositions, nextPositions)) {
            int outLeg = lastPositions.TryGetValue(axisId, out int from) ? Math.Abs(from - MidPosition) : 0;
            int inLeg = nextPositions.TryGetValue(axisId, out int to) ? Math.Abs(MidPosition - to) : 0;

            // Both legs get half the gap each, so the gap is set by the longer of the two -
            // not by their sum, and not by any one axis' total travel.
            longestLeg = Math.Max(longestLeg, Math.Max(outLeg, inLeg));
        }

        if (longestLeg == 0) return 0;

        // Ceiling rather than rounding: landing a millisecond short would put the move back
        // over the limit the whole gap exists to enforce. Two legs, hence the 2000.
        int rawMs = DivideRoundingUp(2000 * longestLeg, maxAxisSpeed);

        return ToWholeFrames(Math.Max(rawMs, MinGapMs), targetFps);
    }

    /// <summary>Every axis either side of the seam knows about, without repeats.</summary>
    public static IEnumerable<string> ActiveAxes(
        IReadOnlyDictionary<string, int> lastPositions,
        IReadOnlyDictionary<string, int> nextPositions) =>
        lastPositions.Keys.Union(nextPositions.Keys, StringComparer.Ordinal);

    /// <remarks>
    /// Integer throughout. A frame is rarely a whole number of milliseconds, and rounding one
    /// up in floating point lands a hair above the exact answer often enough that a gap of
    /// exactly n frames would come back as n plus one.
    /// </remarks>
    private static int ToWholeFrames(int durationMs, int targetFps) {
        if (targetFps <= 0) return durationMs;

        int frames = DivideRoundingUp(durationMs * targetFps, 1000);

        return DivideRoundingUp(frames * 1000, targetFps);
    }

    private static int DivideRoundingUp(int dividend, int divisor) => (dividend + divisor - 1) / divisor;
}
