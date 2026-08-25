namespace AttcksMergeTool.Services;

/// <summary>
/// An external tool (ffmpeg or ffprobe) exited with a non-zero code. Carries the tool's
/// own diagnostics so a failed run is reported as a failure with a reason, instead of
/// being indistinguishable from a successful one.
/// </summary>
public sealed class ExternalToolException : Exception
{
    public ExternalToolException(string toolName, int exitCode, string diagnostics)
        : base(BuildMessage(toolName, exitCode, diagnostics)) {
        ToolName = toolName;
        ExitCode = exitCode;
        Diagnostics = diagnostics;
    }

    public string ToolName { get; }

    public int ExitCode { get; }

    /// <summary>Whatever the tool wrote to stderr, or to stdout when stderr was empty.</summary>
    public string Diagnostics { get; }

    private static string BuildMessage(string toolName, int exitCode, string diagnostics) =>
        string.IsNullOrWhiteSpace(diagnostics)
            ? $"{toolName} exited with code {exitCode}."
            : $"{toolName} exited with code {exitCode}: {diagnostics}";
}
