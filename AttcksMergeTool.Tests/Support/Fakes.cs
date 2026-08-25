using AttcksMergeTool.Models;
using AttcksMergeTool.Services;

namespace AttcksMergeTool.Tests.Support;

/// <summary>Captures everything a merge logged, so tests can assert on the warnings.</summary>
internal sealed class FakeJobLogger : IJobLogger
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    /// <remarks>Guarded: the parallel encode pass logs from worker threads.</remarks>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries {
        get { lock (_gate) return [.. _entries]; }
    }

    public void Log(string message, LogLevel level = LogLevel.Info) {
        lock (_gate) _entries.Add((level, message));
    }

    public IEnumerable<string> Warnings =>
        Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message);

    public bool WarnedAbout(string fragment) =>
        Warnings.Any(message => message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Stands in for ffmpeg. Records what it was asked to run and never launches anything.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Lock _gate = new();
    private readonly List<Invocation> _invocations = [];

    public IReadOnlyList<Invocation> Invocations {
        get { lock (_gate) return [.. _invocations]; }
    }

    /// <summary>Commands that should be reported as absent from PATH.</summary>
    public HashSet<string> MissingCommands { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lets a test make one particular invocation fail or return output.</summary>
    public Func<Invocation, string>? Respond { get; set; }

    public Task<string> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        var invocation = new Invocation(fileName, [.. arguments]);
        lock (_gate) _invocations.Add(invocation);

        return Task.FromResult(Respond?.Invoke(invocation) ?? string.Empty);
    }

    public Task<bool> CommandExistsAsync(string command, CancellationToken cancellationToken = default) =>
        Task.FromResult(!MissingCommands.Contains(command));

    internal sealed record Invocation(string FileName, IReadOnlyList<string> Arguments);
}

/// <summary>
/// Stands in for ffprobe, keyed by file name so tests can state a duration without caring
/// which folder the file ends up in.
/// </summary>
internal sealed class FakeMediaProbe : IMediaProbe
{
    private readonly Dictionary<string, int?> _durations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What to report for a file no test set up. Null means "could not be read".</summary>
    public int? DefaultDurationMs { get; set; }

    public FakeMediaProbe WithDuration(string fileName, int? durationMs) {
        _durations[fileName] = durationMs;
        return this;
    }

    public Task<int?> GetDurationMsAsync(string filePath, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        string name = Path.GetFileName(filePath);

        return Task.FromResult(_durations.TryGetValue(name, out int? duration) ? duration : DefaultDurationMs);
    }
}

/// <summary>
/// Builds the script-merge result the video stage consumes, without running a merge.
/// </summary>
internal static class MergeResults
{
    /// <summary>
    /// A result carrying <paramref name="bookmarks"/> as its markers, with one span per
    /// bookmark running to the next (and to <paramref name="totalDurationMs"/> for the last).
    /// </summary>
    public static FunscriptMergeResult WithBookmarks(int totalDurationMs, params Bookmark[] bookmarks) {
        var spans = new List<SceneSpan>(bookmarks.Length);

        for (int i = 0; i < bookmarks.Length; i++) {
            int endMs = i + 1 < bookmarks.Length ? bookmarks[i + 1].Time : totalDurationMs;
            spans.Add(new SceneSpan(bookmarks[i].Name, bookmarks[i].Time, endMs - bookmarks[i].Time));
        }

        var document = new Funscript { Actions = [], Bookmarks = [.. bookmarks] };

        return new FunscriptMergeResult(document, spans, totalDurationMs);
    }
}
