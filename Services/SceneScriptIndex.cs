using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Works out which funscripts in a folder are scenes and which are the per-axis
/// <c>{scene}.{axis}.funscript</c> siblings belonging to one.
/// </summary>
/// <remarks>
/// This used to be implicit: the merger walked every funscript in sorted order and skipped a
/// file only because the scene that owned it had already claimed it. That holds only while the
/// axis name sorts after the literal "funscript" - true of surge/sway/twist/roll/pitch, but not
/// of a custom axis starting with a-e or a digit, which was therefore merged as a scene of its
/// own: wrong axis, a bogus chapter, and every later scene shifted. Classifying up front removes
/// the dependency on sort order entirely.
/// </remarks>
public static class SceneScriptIndex
{
    /// <summary>Scenes in <paramref name="folder"/>, in merge order.</summary>
    /// <param name="videoExtensions">
    /// The configured video extension list, or null for <see cref="MediaFileScanner.VideoExtensions"/>.
    /// It has to match what the merge itself will scan with, or a scene could be paired here
    /// with a video the merge does not see.
    /// </param>
    public static List<SceneScripts> Build(string folder, IReadOnlyCollection<string>? videoExtensions = null) =>
        Build(
            MediaFileScanner.FindFunscripts(folder),
            MediaFileScanner.FindVideos(folder, videoExtensions));

    /// <summary>
    /// Scenes described by <paramref name="scriptPaths"/> and <paramref name="videoPaths"/>,
    /// in merge order. A scene is emitted only when it has a script to contribute; a video
    /// with no script of any kind is left for <see cref="ScenePairing"/> to report.
    /// </summary>
    public static List<SceneScripts> Build(IEnumerable<string> scriptPaths, IEnumerable<string> videoPaths) {
        // A base name that owns a video is a scene by definition, whatever its name looks like.
        // Seeding with those is what keeps a scene legitimately called "My.Video" from being
        // read as the "Video" axis of a scene called "My".
        var sceneNames = new HashSet<string>(
            videoPaths.Select(Path.GetFileNameWithoutExtension).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        var mainScripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var siblings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Shortest first, so an owner is always settled before anything that could hang off it.
        IEnumerable<string> ordered = scriptPaths
            .OrderBy(path => Path.GetFileNameWithoutExtension(path).Length)
            .ThenBy(path => path, StringComparer.Ordinal);

        foreach (string path in ordered) {
            string name = Path.GetFileNameWithoutExtension(path);
            string? owner = sceneNames.Contains(name) ? null : FindOwner(name, sceneNames);

            if (owner is null) {
                sceneNames.Add(name);
                mainScripts[name] = path;
            } else {
                if (!siblings.TryGetValue(owner, out List<string>? group)) {
                    group = [];
                    siblings[owner] = group;
                }
                group.Add(path);
            }
        }

        var contributing = new HashSet<string>(mainScripts.Keys, StringComparer.OrdinalIgnoreCase);
        contributing.UnionWith(siblings.Keys);

        return contributing
            .Select(name => new SceneScripts(
                name,
                mainScripts.GetValueOrDefault(name),
                siblings.TryGetValue(name, out List<string>? group)
                    ? group.Order(StringComparer.Ordinal).ToList()
                    : []))
            .OrderBy(scene => scene.SortKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The longest known scene name that <paramref name="name"/> hangs off, or <c>null</c>
    /// when it hangs off none and is therefore a scene itself. Longest wins so that a nested
    /// name attaches to the most specific scene that claims it.
    /// </summary>
    private static string? FindOwner(string name, IEnumerable<string> sceneNames) {
        string? best = null;

        foreach (string candidate in sceneNames) {
            if (!name.StartsWith(candidate + ".", StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || candidate.Length > best.Length) best = candidate;
        }

        return best;
    }

    /// <summary>
    /// The axis alias a sibling contributes: everything between the scene name and the
    /// extension, lowercased. "Scene.twist.funscript" under scene "Scene" gives "twist".
    /// </summary>
    public static string AxisAliasOf(SceneScripts scene, string siblingPath) {
        string siblingName = Path.GetFileNameWithoutExtension(siblingPath);

        return siblingName.Length > scene.Name.Length + 1
            ? siblingName[(scene.Name.Length + 1)..].ToLowerInvariant()
            : siblingName.ToLowerInvariant();
    }
}
