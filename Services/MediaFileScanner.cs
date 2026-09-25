using AttcksMergeTool.Models;

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
    /// <param name="scan">
    /// Whether subfolders are walked too, and which folders are kept out of that walk. The
    /// default is the top level only, which is what the scan always did.
    /// </param>
    public static List<string> FindVideos(
        string folder, IReadOnlyCollection<string>? extensions = null, InputScan scan = default) =>
        Enumerate(folder, "*.*", scan)
            .Where(path => IsVideoFile(path, extensions))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every funscript in <paramref name="folder"/>, ordered by path. This includes the
    /// per-axis sibling files; use <see cref="SceneScriptIndex"/> to tell the two apart.
    /// </summary>
    public static List<string> FindFunscripts(string folder, InputScan scan = default) =>
        Enumerate(folder, "*" + FunscriptExtension, scan).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The base names that more than one of <paramref name="paths"/> carries, ordered like a
    /// listing. Everything downstream identifies a scene by base name, so these are the names
    /// a recursive scan has made ambiguous.
    /// </summary>
    public static List<string> DuplicateBaseNames(IEnumerable<string> paths) =>
        paths
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// What to tell the user when the same base name turned up in more than one of
    /// <paramref name="listings"/>, or null when every name is unique.
    /// </summary>
    /// <remarks>
    /// Worth saying only for a recursive scan, and the caller decides that: a folder holding
    /// both "Scene.mp4" and "Scene.mkv" is the same ambiguity, but it is one the tool has
    /// always had and not one this warning would be explaining.
    /// </remarks>
    public static string? AmbiguousNameWarning(params IEnumerable<string>[] listings) {
        List<string> duplicates = [.. listings
            .SelectMany(DuplicateBaseNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)];

        return duplicates.Count == 0
            ? null
            : $"The same name is used in more than one input folder: {string.Join(", ", duplicates)}. "
              + "Scenes are matched by filename rather than by folder, so each video of one of "
              + "these names is paired with the same funscript, and only one funscript of each "
              + "name is read. Rename them to keep the scenes apart.";
    }

    /// <summary>
    /// The raw listing, before any ordering: the files matching <paramref name="pattern"/>,
    /// recursively when asked for and minus anything inside an excluded folder.
    /// </summary>
    /// <remarks>
    /// A missing folder lists nothing rather than throwing - the input folder is created when
    /// a job starts, and the window scans before that - and an unreadable subfolder is stepped
    /// over rather than taking the whole listing down with it.
    /// </remarks>
    private static IEnumerable<string> Enumerate(string folder, string pattern, InputScan scan) {
        if (!Directory.Exists(folder)) return [];

        var options = new EnumerationOptions {
            RecurseSubdirectories = scan.Recursive,
            IgnoreInaccessible = true,
            // GetFiles, which this replaced, returned hidden and system files; a scan that
            // quietly dropped them would be a change to what the top level of the folder holds.
            AttributesToSkip = 0,
            // For the same reason: GetFiles matched its pattern the way the shell does, and
            // EnumerationOptions otherwise defaults to the stricter form, under which "*.*"
            // stops matching a file that has no extension at all.
            MatchType = MatchType.Win32
        };

        IEnumerable<string> files = Directory.EnumerateFiles(folder, pattern, options);
        IReadOnlyList<string> excluded = scan.ExcludedSubtreesOf(folder);

        return excluded.Count == 0 ? files : files.Where(path => !InputScan.IsUnder(path, excluded));
    }
}
