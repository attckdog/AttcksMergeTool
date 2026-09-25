namespace AttcksMergeTool.Models;

/// <summary>
/// One normalized intermediate produced by the encode pass, and how long it actually
/// turned out to be.
/// </summary>
/// <remarks>
/// <see cref="DurationMs"/> is measured from the encoded file rather than from its source,
/// because forcing a common frame rate and rounding trim boundaries to frames means the two
/// differ slightly - and that difference accumulates across scenes. It is null when the
/// segment could not be probed.
/// </remarks>
/// <param name="IsGap">
/// True for a generated stretch of black rather than a re-encoded source. It occupies real
/// time in the output, so it counts towards every offset - but it is not a scene: it has no
/// span of its own to be retimed against, and it extends the preceding chapter instead of
/// starting one. <see cref="SourcePath"/> then names the scene it leads into, which is all
/// that is wanted from it in a log line.
/// </param>
public sealed record EncodedSegment(string SourcePath, string SegmentPath, int? DurationMs, bool IsGap = false)
{
    /// <summary>The scene this segment came from, which is also its chapter title.</summary>
    public string SceneName => Path.GetFileNameWithoutExtension(SourcePath);

    /// <summary>
    /// This segment's line in the concat demuxer's list file. The demuxer wants forward
    /// slashes regardless of platform.
    /// </summary>
    public string ConcatEntry => $"file '{SegmentPath.Replace('\\', '/')}'";
}
