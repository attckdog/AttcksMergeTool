namespace AttcksMergeTool.Services;

/// <summary>
/// Launches the external command-line tools a merge depends on.
/// </summary>
/// <remarks>
/// An interface rather than a static call so the merge services can be exercised without a
/// real ffmpeg on PATH. <see cref="ProcessRunner.Default"/> is the production implementation.
/// </remarks>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> to completion and returns everything it wrote to
    /// stdout. Cancelling kills the child process and its descendants.
    /// </summary>
    /// <exception cref="ExternalToolException">The process exited with a non-zero code.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    Task<string> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="command"/> can be launched from PATH and reports success.
    /// </summary>
    Task<bool> CommandExistsAsync(string command, CancellationToken cancellationToken = default);
}
