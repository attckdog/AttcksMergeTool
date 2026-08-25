namespace AttcksMergeTool.Models;

/// <summary>
/// What the script merge produced that the video stage still needs: the merged document
/// itself, where each scene landed on its timeline, and the summed length.
/// </summary>
/// <remarks>
/// The document is carried rather than only its file path because the video stage retimes it
/// onto the measured segment lengths and writes it out a second time.
/// </remarks>
public sealed record FunscriptMergeResult(
    Funscript Document,
    IReadOnlyList<SceneSpan> Spans,
    int TotalDurationMs)
{
    /// <summary>The per-scene markers, which double as the fallback chapter boundaries.</summary>
    public IReadOnlyList<Bookmark> Bookmarks => Document.Bookmarks ?? [];
}
