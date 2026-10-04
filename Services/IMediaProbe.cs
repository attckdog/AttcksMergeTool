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

    /// <summary>
    /// Whether <paramref name="filePath"/> has at least one audio stream. <c>true</c> when it
    /// could not be determined, so an unreadable file fails in ffmpeg with a real error rather
    /// than being silently encoded as a mute one.
    /// </summary>
    Task<bool> HasAudioAsync(string filePath, CancellationToken cancellationToken = default);
}
