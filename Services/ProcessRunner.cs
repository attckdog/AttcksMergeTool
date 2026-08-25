using System.Diagnostics;

namespace AttcksMergeTool.Services;

/// <summary>
/// Launches the external ffmpeg/ffprobe executables.
/// </summary>
/// <remarks>
/// Arguments are passed as a list and forwarded to <see cref="ProcessStartInfo.ArgumentList"/>,
/// so the runtime does the escaping and paths containing spaces work by construction. Both
/// standard streams are drained concurrently with the wait, which is what stops a chatty
/// child from filling its pipe buffer and blocking on write while we block on the wait.
/// </remarks>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>How many trailing lines of tool output a failure carries into the log.</summary>
    private const int DiagnosticLineLimit = 20;

    /// <summary>The real implementation, used everywhere except tests.</summary>
    public static IProcessRunner Default { get; } = new ProcessRunner();

    /// <inheritdoc />
    public async Task<string> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default) {
        var startInfo = new ProcessStartInfo {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Start draining before waiting - see the deadlock note on the class.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        using (cancellationToken.Register(() => TryKill(process))) {
            // Deliberately not passing the token: the registration above ends the process, and
            // reaping it unconditionally here guarantees both readers complete rather than
            // being abandoned mid-pipe.
            await process.WaitForExitAsync(CancellationToken.None);
        }

        string output = await standardOutput;
        string error = await standardError;

        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0) {
            throw new ExternalToolException(fileName, process.ExitCode, Summarize(error, output));
        }

        return output;
    }

    /// <inheritdoc />
    public async Task<bool> CommandExistsAsync(
        string command,
        CancellationToken cancellationToken = default) {
        try {
            await RunAsync(command, ["-version"], cancellationToken);
            return true;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception) {
            // Missing from PATH, not executable, or it reported failure - all mean "unusable".
            return false;
        }
    }

    private static void TryKill(Process process) {
        try {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        } catch (InvalidOperationException) {
            // Already exited between the check and the kill.
        } catch (System.ComponentModel.Win32Exception) {
            // Exited while the kill was in flight.
        }
    }

    /// <summary>
    /// The tail of what the tool said, preferring stderr - ffmpeg's <c>-loglevel error</c>
    /// output lands there, and only the last few lines are worth putting in the log.
    /// </summary>
    private static string Summarize(string error, string output) {
        string detail = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        if (detail.Length == 0) return string.Empty;

        string[] lines = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> tail = lines.Length > DiagnosticLineLimit
            ? lines[^DiagnosticLineLimit..]
            : lines;

        return string.Join(Environment.NewLine, tail.Select(line => line.TrimEnd('\r')));
    }
}
