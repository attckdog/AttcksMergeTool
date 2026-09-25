namespace AttcksMergeTool.Models;

/// <summary>
/// How the input folder is walked: its top level only, as it always was, or every folder
/// underneath it as well.
/// </summary>
/// <remarks>
/// A recursive walk can reach folders the job itself writes into - the temp folder and the
/// output folder sit wherever the user pointed them, which may well be inside the input tree -
/// and a scan that picked those up would feed a run its own intermediates or the video it
/// produced last time. <see cref="ExcludedFolders"/> is what keeps them out. It is ignored for
/// a non-recursive scan, which cannot reach a subfolder to begin with, so nothing about the
/// old behaviour depends on it.
/// </remarks>
/// <param name="ExcludedFolders">
/// Folders whose contents are left out of a recursive scan. Only those lying inside the folder
/// being scanned mean anything, and the scanned folder itself is never excluded by one of them:
/// pointing the output at the input folder is a choice about where files land, not a request
/// for an empty scan.
/// </param>
public readonly record struct InputScan(bool Recursive = false, IReadOnlyList<string>? ExcludedFolders = null)
{
    /// <summary>
    /// The excluded folders that actually lie inside <paramref name="root"/>, each in the form
    /// a path under it starts with. Empty for a non-recursive scan.
    /// </summary>
    public IReadOnlyList<string> ExcludedSubtreesOf(string root) {
        if (!Recursive || ExcludedFolders is not { Count: > 0 }) return [];

        string rootPrefix = AsPrefix(root);

        return [.. ExcludedFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(AsPrefix)
            // Longer than the root, so a folder that is the root rather than inside it drops out.
            .Where(folder => folder.Length > rootPrefix.Length
                             && folder.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Whether <paramref name="path"/> sits inside one of <paramref name="excluded"/>.</summary>
    public static bool IsUnder(string path, IReadOnlyList<string> excluded) =>
        excluded.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A folder written the way the paths inside it are: absolute, and with a trailing
    /// separator so "Input\Temp" cannot be read as a prefix of "Input\TempScenes".
    /// </summary>
    private static string AsPrefix(string folder) {
        string resolved = MergeOptions.ResolvePath(folder);

        return resolved.EndsWith(Path.DirectorySeparatorChar) ? resolved : resolved + Path.DirectorySeparatorChar;
    }
}
