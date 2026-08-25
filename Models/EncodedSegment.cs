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
public sealed record EncodedSegment(string SourcePath, string SegmentPath, int? DurationMs)
{
    /// <summary>The scene this segment came from, which is also its chapter title.</summary>
    public string SceneName => Path.GetFileNameWithoutExtension(SourcePath);

    /// <summary>
    /// This segment's line in the concat demuxer's list file. The demuxer wants forward
    /// slashes regardless of platform.
    /// </summary>
    public string ConcatEntry => $"file '{SegmentPath.Replace('\\', '/')}'";
}
