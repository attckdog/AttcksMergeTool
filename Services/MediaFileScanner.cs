namespace AttcksMergeTool.Services;

/// <summary>
/// Single source of truth for what counts as an input file and how the Input folder
/// is enumerated. Previously the extension list was duplicated at three call sites.
/// </summary>
/// <remarks>
/// Every listing is ordered with <see cref="StringComparer.Ordinal"/>. This is the default
/// merge order - the window can rearrange it, and a job carries its own arrangement - so it
/// has to be reproducible: the default string comparer is culture-sensitive and would let the
/// machine's locale change the merge.
/// </remarks>
public static class MediaFileScanner
{
    public const string FunscriptExtension = ".funscript";

    /// <summary>
    /// What is treated as a video when the caller has nothing more specific to say. The user
    /// can override the list in the options, which is what the optional parameters below are for.
    /// </summary>
    public static readonly string[] VideoExtensions = {
        ".mp4", ".mkv", ".avi", ".webm", ".m4v", ".ts", ".mov"
    };

    /// <param name="extensions">
    /// The configured extension list, already lowercased and dot-prefixed by
    /// <see cref="Models.AppSettings.Normalize"/>. Null falls back to <see cref="VideoExtensions"/>.
    /// </param>
    public static bool IsVideoFile(string path, IReadOnlyCollection<string>? extensions = null) =>
        (extensions ?? VideoExtensions).Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Videos in <paramref name="folder"/>, ordered by path for a stable merge order.</summary>
    public static List<string> FindVideos(string folder, IReadOnlyCollection<string>? extensions = null) =>
        !Directory.Exists(folder)
            ? []
            : Directory.GetFiles(folder, "*.*")
                .Where(path => IsVideoFile(path, extensions))
                .Order(StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// Every funscript in <paramref name="folder"/>, ordered by path. This includes the
    /// per-axis sibling files; use <see cref="SceneScriptIndex"/> to tell the two apart.
    /// </summary>
    public static List<string> FindFunscripts(string folder) =>
        !Directory.Exists(folder)
            ? []
            : Directory.GetFiles(folder, "*" + FunscriptExtension).Order(StringComparer.Ordinal).ToList();
}
