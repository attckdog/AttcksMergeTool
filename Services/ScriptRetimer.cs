using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Moves a merged script off the timeline the source videos implied and onto the one the
/// encoded segments actually produced.
/// </summary>
/// <remarks>
/// The script merge has to run before the encode - otherwise a failed encode would leave no
/// script at all - so its scene offsets can only come from probing the source videos. The
/// output video is built from segments re-encoded at a forced frame rate with trim boundaries
/// rounded to frames, and each one comes out a little longer or shorter than its source. That
/// error used to accumulate: about 21ms per scene on a 135-scene merge, which is small at the
/// first chapter and seconds off by the last.
/// <para>
/// Retiming fixes it after the fact. Each scene is shifted bodily to where its segment really
/// starts, so keyframes keep their timing <em>within</em> a scene and every scene boundary
/// lands exactly on its chapter. Nothing is scaled, because the encode does not stretch the
/// content - the difference is padding at the segment's end.
/// </para>
/// </remarks>
public static class ScriptRetimer
{
    /// <summary>
    /// The outcome of a retime. <see cref="Applied"/> is false when the segments could not be
    /// matched to the plan, in which case the document is untouched and
    /// <see cref="Reason"/> says why.
    /// </summary>
    public sealed record Result(bool Applied, int MaxShiftMs, int TotalDurationMs, string? Reason)
    {
        public static Result Failed(string reason) => new(false, 0, 0, reason);
    }

    /// <summary>
    /// Rewrites every timestamp in <paramref name="document"/> so that the scene described by
    /// <c>spans[i]</c> starts where <c>segments[i]</c> starts in the concatenated output.
    /// </summary>
    public static Result Retime(
        Funscript document,
        IReadOnlyList<SceneSpan> spans,
        IReadOnlyList<EncodedSegment> segments) {
        if (spans.Count == 0 || segments.Count == 0) return Result.Failed("there was nothing to line up");

        if (spans.Count != segments.Count) {
            return Result.Failed(
                $"the script covers {spans.Count} scenes but the video was built from {segments.Count} segments");
        }

        var newStarts = new int[spans.Count];
        int measuredTotalMs = 0;

        for (int i = 0; i < spans.Count; i++) {
            if (segments[i].DurationMs is not > 0) {
                return Result.Failed($"segment {i + 1} ({segments[i].SceneName}) could not be measured");
            }

            // Position, not name, is what the concat honours - but a mismatch here means the
            // two lists were built from different things and every offset below would be wrong.
            if (!string.Equals(spans[i].Name, segments[i].SceneName, StringComparison.OrdinalIgnoreCase)) {
                return Result.Failed(
                    $"scene {i + 1} is '{spans[i].Name}' in the script but '{segments[i].SceneName}' in the video");
            }

            newStarts[i] = measuredTotalMs;
            measuredTotalMs += segments[i].DurationMs!.Value;
        }

        int maxShiftMs = 0;

        for (int i = 0; i < spans.Count; i++) {
            maxShiftMs = Math.Max(maxShiftMs, Math.Abs(newStarts[i] - spans[i].StartMs));
        }

        Shift(document.Actions, spans, newStarts);

        if (document.Axes is { Count: > 0 }) {
            foreach (FunscriptAxis axis in document.Axes) Shift(axis.Actions, spans, newStarts);
        }

        if (document.Bookmarks is { Count: > 0 }) {
            foreach (Bookmark bookmark in document.Bookmarks) {
                bookmark.Time += ShiftOf(bookmark.Time, spans, newStarts);
            }
        }

        if (document.Metadata is not null) document.Metadata.Duration = measuredTotalMs / 1000;

        return new Result(true, maxShiftMs, measuredTotalMs, null);
    }

    /// <summary>
    /// Shifts one axis' keyframes by their own scene's correction, keeping the list
    /// non-decreasing.
    /// </summary>
    /// <remarks>
    /// Neighbouring scenes get different corrections, so a scene whose keyframes run past the
    /// length it was allotted - which the merge permits - could otherwise end up timestamped
    /// after the first keyframe of the next scene. Clamping to the previous value keeps the
    /// axis sorted, which every player assumes.
    /// </remarks>
    private static void Shift(List<ActionPoint>? actions, IReadOnlyList<SceneSpan> spans, int[] newStarts) {
        if (actions is not { Count: > 0 }) return;

        int previousMs = 0;

        foreach (ActionPoint action in actions) {
            int shifted = action.At + ShiftOf(action.At, spans, newStarts);

            action.At = Math.Max(shifted, previousMs);
            previousMs = action.At;
        }
    }

    /// <summary>
    /// How far the scene containing <paramref name="timestampMs"/> moved. Timestamps before
    /// the first scene take the first scene's correction; anything past the last scene's start
    /// takes the last one's.
    /// </summary>
    private static int ShiftOf(int timestampMs, IReadOnlyList<SceneSpan> spans, int[] newStarts) {
        int low = 0;
        int high = spans.Count - 1;
        int found = 0;

        while (low <= high) {
            int mid = (low + high) / 2;

            if (spans[mid].StartMs <= timestampMs) {
                found = mid;
                low = mid + 1;
            } else {
                high = mid - 1;
            }
        }

        return newStarts[found] - spans[found].StartMs;
    }
}
