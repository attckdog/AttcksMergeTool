using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Turns a merge into the chapter spans the output video should carry.
/// </summary>
/// <remarks>
/// <see cref="FromSegments"/> is the accurate source and the one normally used: it measures
/// the intermediates the output is actually built from. <see cref="FromBookmarks"/> is the
/// fallback for when a segment could not be probed - it uses the script's own scene offsets,
/// which come from the <em>source</em> videos and therefore drift, but a drifting chapter list
/// beats none at all.
/// </remarks>
public static class ChapterBuilder
{
    /// <summary>
    /// Chapters laid end to end from the measured segment durations. Returns an empty list if
    /// any segment is unmeasured, because one unknown length invalidates every boundary after it.
    /// </summary>
    /// <remarks>
    /// A transition gap gets no chapter of its own. It extends the one before it instead, so
    /// every chapter starts on its scene's first real frame - seeking to one lands on content
    /// rather than on a second of black. The gap's time is still counted, so the chapters stay
    /// contiguous and still cover the whole output.
    /// </remarks>
    public static IReadOnlyList<Chapter> FromSegments(IReadOnlyList<EncodedSegment> segments) {
        var chapters = new List<Chapter>(segments.Count);
        int startMs = 0;

        foreach (EncodedSegment segment in segments) {
            if (segment.DurationMs is not > 0) return [];

            int endMs = startMs + segment.DurationMs.Value;

            if (segment.IsGap) {
                // A leading gap has no chapter to extend. The merger never emits one, but if
                // it ever did the black would simply belong to no chapter rather than
                // displacing the first scene's title onto it.
                if (chapters.Count > 0) chapters[^1] = chapters[^1] with { EndMs = endMs };
            } else {
                chapters.Add(new Chapter(segment.SceneName, startMs, endMs));
            }

            startMs = endMs;
        }

        return chapters;
    }

    /// <summary>
    /// Chapters spanning each scene marker to the next, with the merged timeline length
    /// closing the last one.
    /// </summary>
    /// <remarks>
    /// Markers sit on their scene's first real frame, and each chapter runs to the next marker
    /// - so a transition gap falls inside the chapter before it here too, without this having
    /// to know gaps exist.
    /// </remarks>
    public static IReadOnlyList<Chapter> FromBookmarks(FunscriptMergeResult mergeResult) {
        IReadOnlyList<Bookmark> markers = mergeResult.Bookmarks;
        var chapters = new List<Chapter>(markers.Count);

        for (int i = 0; i < markers.Count; i++) {
            int startMs = markers[i].Time;
            int endMs = i + 1 < markers.Count ? markers[i + 1].Time : mergeResult.TotalDurationMs;

            // A zero-length scene has nothing to navigate to, and ffmpeg rejects the span.
            if (endMs <= startMs) continue;

            chapters.Add(new Chapter(markers[i].Name, startMs, endMs));
        }

        return chapters;
    }

    /// <summary>End of the last chapter, which is the whole span the chapters cover.</summary>
    public static int TotalDurationMs(IReadOnlyList<Chapter> chapters) =>
        chapters.Count == 0 ? 0 : chapters[^1].EndMs;
}
