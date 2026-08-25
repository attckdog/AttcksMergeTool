using System.Globalization;

namespace AttcksMergeTool.Services;

/// <summary>Reads media properties via the ffprobe executable.</summary>
public sealed class FFprobe : IMediaProbe
{
    private readonly IProcessRunner _runner;
    private readonly string _executable;

    /// <param name="executable">
    /// The ffprobe to launch. A bare name is searched for on PATH; see
    /// <see cref="Models.MergeOptions.FfprobePath"/>.
    /// </param>
    public FFprobe(IProcessRunner runner, string executable = "ffprobe") {
        _runner = runner;
        _executable = executable;
    }

    /// <summary>The real implementation, used everywhere except tests.</summary>
    public static IMediaProbe Default { get; } = new FFprobe(ProcessRunner.Default);

    /// <summary>
    /// Duration of <paramref name="filePath"/> in milliseconds, or <c>null</c> when ffprobe
    /// failed or reported something unparseable.
    /// </summary>
    /// <remarks>
    /// Nullable rather than zero on purpose: the merger treats a missing duration as "no
    /// companion video" and advances the timeline by the last keyframe instead, which shifts
    /// every later scene. The caller has to be able to tell the two apart to warn about it.
    /// </remarks>
    public async Task<int?> GetDurationMsAsync(
        string filePath,
        CancellationToken cancellationToken = default) {
        string output;

        try {
            output = await _runner.RunAsync(_executable, [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                filePath
            ], cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception) {
            // One unreadable file must not abort the whole merge; the caller warns instead.
            return null;
        }

        return double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds)
            ? (int)(seconds * 1000)
            : null;
    }
}
