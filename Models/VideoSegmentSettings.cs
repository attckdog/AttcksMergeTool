namespace AttcksMergeTool.Models;

/// <summary>
/// Per-video trim configuration, edited in the side panel and applied to both the
/// video encode and the funscript timeline so the two stay in sync.
/// </summary>
public sealed class VideoSegmentSettings
{
    public string FilePath { get; set; } = string.Empty;

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Seconds into the source where the kept segment begins.</summary>
    public double StartTime { get; set; }

    /// <summary>Seconds into the source where it ends; zero means run to the end.</summary>
    public double EndTime { get; set; }

    public bool UseTrim { get; set; }

    /// <summary>
    /// An independent copy. A running job takes one of these per video so the UI thread
    /// cannot change a trim out from under it via <c>Apply to Selected Video</c>.
    /// </summary>
    public VideoSegmentSettings Clone() => new() {
        FilePath = FilePath,
        StartTime = StartTime,
        EndTime = EndTime,
        UseTrim = UseTrim
    };

    /// <summary>Drives the display text in the video list box.</summary>
    public override string ToString() =>
        FileName + (UseTrim ? $" [Trim: {StartTime}s - {EndTime}s]" : string.Empty);
}
