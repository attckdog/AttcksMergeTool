namespace AttcksMergeTool.Models;

/// <summary>
/// A millisecond trim range applied to one scene. <see cref="None"/> (both bounds
/// zero) means "keep everything"; an <see cref="EndMs"/> of zero means "run to the
/// end of the source".
/// </summary>
public readonly record struct TrimWindow(int StartMs, int EndMs)
{
    public static TrimWindow None { get; } = new(0, 0);

    public static TrimWindow FromSeconds(double startSeconds, double endSeconds) =>
        new((int)(startSeconds * 1000), (int)(endSeconds * 1000));

    /// <summary>Whether a keyframe at <paramref name="atMs"/> falls outside the window.</summary>
    public bool Excludes(int atMs) =>
        (StartMs > 0 && atMs < StartMs) || (EndMs > StartMs && atMs > EndMs);

    /// <summary>
    /// Rebases a source timestamp onto the trimmed timeline, so scripts stay in sync
    /// with the trimmed video.
    /// </summary>
    public int Rebase(int atMs) => Math.Max(0, atMs - StartMs);
}
