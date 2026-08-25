namespace AttcksMergeTool.Services;

/// <summary>Sink for a merge job's human-readable progress commentary.</summary>
public interface IJobLogger
{
    void Log(string message, LogLevel level = LogLevel.Info);
}

public static class JobLoggerExtensions
{
    private const string Rule = "========================================================";

    /// <summary>Writes the ruled banner that separates the major merge steps.</summary>
    public static void LogSection(this IJobLogger logger, string title, bool leadingBlankLine = false) {
        logger.Log((leadingBlankLine ? Environment.NewLine : string.Empty) + Rule, LogLevel.Heading);
        logger.Log(title, LogLevel.Heading);
        logger.Log(Rule, LogLevel.Heading);
    }
}
