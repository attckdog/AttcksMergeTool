using System.Text.Json;

using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>Reads funscript documents off disk. One place, one set of parse options.</summary>
public static class ScriptReader
{
    private static readonly JsonSerializerOptions ReadOptions = new() {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<Funscript> ReadAsync(string path, CancellationToken cancellationToken = default) {
        string content = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<Funscript>(content, ReadOptions) ?? new Funscript();
    }

    /// <summary>
    /// How much timeline a scene would occupy: its latest keyframe across the main document,
    /// its embedded axes and its sibling files. Used to say what leaving a scene out costs;
    /// an unreadable file counts as zero rather than failing the run over a warning.
    /// </summary>
    public static async Task<int> LastKeyframeMsAsync(SceneScripts scene, CancellationToken cancellationToken = default) {
        int lastMs = 0;

        IEnumerable<string> paths = scene.MainScriptPath is null
            ? scene.SiblingScriptPaths
            : scene.SiblingScriptPaths.Prepend(scene.MainScriptPath);

        foreach (string path in paths) {
            Funscript script;

            try {
                script = await ReadAsync(path, cancellationToken);
            } catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
                continue;
            }

            lastMs = Math.Max(lastMs, LatestOf(script.Actions));

            if (script.Axes is not { Count: > 0 }) continue;

            foreach (FunscriptAxis axis in script.Axes) lastMs = Math.Max(lastMs, LatestOf(axis.Actions));
        }

        return lastMs;
    }

    private static int LatestOf(List<ActionPoint>? actions) {
        int latest = 0;

        if (actions is not { Count: > 0 }) return latest;

        foreach (ActionPoint action in actions) latest = Math.Max(latest, action.At);

        return latest;
    }
}
