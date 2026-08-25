namespace AttcksMergeTool.Services;

/// <summary>Reads properties of a media file.</summary>
/// <remarks>
/// Split from <see cref="FFprobe"/> so the timeline arithmetic that depends on durations can
/// be tested against known values instead of against whatever ffprobe says about a real file.
/// </remarks>
public interface IMediaProbe
{
    /// <summary>
    /// Duration of <paramref name="filePath"/> in milliseconds, or <c>null</c> when it could
    /// not be determined.
    /// </summary>
    Task<int?> GetDurationMsAsync(string filePath, CancellationToken cancellationToken = default);
}
