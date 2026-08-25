namespace AttcksMergeTool.Services;

/// <summary>
/// Severity of a job log line. Services speak in levels so they stay free of any
/// UI types; the presentation layer decides what each level looks like.
/// </summary>
public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>Section banners and run-level milestones.</summary>
    Heading
}
