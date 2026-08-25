using System.Text.Json;

using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Reads what the video list displays about a scene: its video's duration and the axes its
/// funscripts carry.
/// </summary>
/// <remarks>
/// The axis count mirrors what <see cref="FunscriptMerger"/> would actually emit rather than
/// counting files - an axis embedded in the main document counts, and a sibling file with no
/// keyframes does not - so the number in the list is the number of axes a merge would produce.
/// Nothing here throws on a bad file: this feeds a list, and one unreadable script must not
/// take the scan for every other row down with it.
/// </remarks>
public static class SceneDetailsReader
{
    /// <summary>Details for <paramref name="videoPath"/> and the scene it belongs to.</summary>
    /// <param name="scene">The scene's scripts, or null when it has none.</param>
    public static async Task<SceneDetails> ReadAsync(
        string videoPath,
        SceneScripts? scene,
        IMediaProbe probe,
        CancellationToken cancellationToken = default) {
        int? durationMs = await probe.GetDurationMsAsync(videoPath, cancellationToken);

        return new SceneDetails(
            durationMs,
            scene is not null,
            scene is null ? 0 : await CountAxesAsync(scene, cancellationToken));
    }

    /// <summary>
    /// How many distinct axes <paramref name="scene"/> contributes: the root stroke track when
    /// the main document has keyframes, plus every axis embedded in it, plus one per
    /// <c>{scene}.{axis}.funscript</c> sibling that has keyframes.
    /// </summary>
    /// <remarks>
    /// Distinct, because an axis can arrive both ways - a document carrying an embedded "R0"
    /// next to a "Scene.twist.funscript" is one axis in the merged output, not two.
    /// </remarks>
    public static async Task<int> CountAxesAsync(
        SceneScripts scene,
        CancellationToken cancellationToken = default) {
        var axisIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (scene.MainScriptPath is not null
            && await TryReadAsync(scene.MainScriptPath, cancellationToken) is { } main) {
            if (main.Actions is { Count: > 0 }) axisIds.Add(FunscriptAxisMap.RootAxisId);

            foreach (FunscriptAxis axis in main.Axes ?? []) {
                Add(axisIds, FunscriptAxisMap.Resolve(axis.Id, axis.Id));
            }
        }

        foreach (string siblingPath in scene.SiblingScriptPaths) {
            cancellationToken.ThrowIfCancellationRequested();

            // Keyframes required, matching the merger: an empty sibling registers no axis.
            if (await TryReadAsync(siblingPath, cancellationToken) is not { Actions.Count: > 0 }) continue;

            string alias = SceneScriptIndex.AxisAliasOf(scene, siblingPath);
            Add(axisIds, FunscriptAxisMap.Resolve(alias, alias));
        }

        return axisIds.Count;
    }

    private static void Add(HashSet<string> axisIds, string axisId) {
        if (!string.IsNullOrWhiteSpace(axisId)) axisIds.Add(axisId);
    }

    /// <summary>The document at <paramref name="path"/>, or null when it cannot be read.</summary>
    private static async Task<Funscript?> TryReadAsync(string path, CancellationToken cancellationToken) {
        try {
            return await ScriptReader.ReadAsync(path, cancellationToken);
        } catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }
}
