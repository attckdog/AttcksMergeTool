using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AttcksMergeTool.Services;

/// <summary>One clip of a pack and where sorting puts it.</summary>
/// <param name="Source">Path inside the pack, with forward slashes.</param>
/// <param name="Target">Path inside the sorted folder, or <c>null</c> when the clip is skipped.</param>
/// <param name="SkipReason">Why the clip is left out, when it is.</param>
public sealed record AudioSortEntry(string Source, string? Target, string? SkipReason);

/// <summary>What a sort would do, worked out before anything is written.</summary>
public sealed class AudioSortPlan(string sourceRoot, IReadOnlyList<AudioSortEntry> entries)
{
    public string SourceRoot { get; } = sourceRoot;

    public IReadOnlyList<AudioSortEntry> Entries { get; } = entries;

    public IEnumerable<AudioSortEntry> Sorted => Entries.Where(entry => entry.Target is not null);

    public int SortedCount => Sorted.Count();

    public int SkippedCount => Entries.Count - SortedCount;

    /// <summary>Clip counts per target folder, in folder order.</summary>
    public IReadOnlyList<(string Folder, int Count)> FolderCounts =>
        [.. Sorted.GroupBy(entry => entry.Target![..entry.Target!.LastIndexOf('/')], StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.Count()))];

    /// <summary>Skipped clips per reason, most common first.</summary>
    public IReadOnlyList<(string Reason, int Count)> SkipCounts =>
        [.. Entries.Where(entry => entry.SkipReason is not null)
            .GroupBy(entry => entry.SkipReason!.StartsWith(AudioSorter.DuplicateReason, StringComparison.Ordinal)
                ? AudioSorter.DuplicateReason
                : entry.SkipReason!)
            .OrderByDescending(group => group.Count())
            .Select(group => (group.Key, group.Count()))];
}

/// <summary>What <see cref="AudioSorter.Apply"/> did.</summary>
public sealed record AudioSortResult(int Linked, int Copied, int AlreadyThere, string ManifestPath);

/// <summary>
/// Sorts a voice pack into <c>Voice/Category/Intensity</c> folders, using
/// <see cref="AudioClassifier"/> to read each clip's path.
/// </summary>
/// <remarks>
/// The sorted folder holds hard links rather than copies: the clips take no extra space, and
/// the pack's own layout is left exactly as its authors shipped it. A destination on another
/// drive cannot hold a hard link, so clips are copied there instead.
/// </remarks>
public static class AudioSorter
{
    public const string DefaultFolderName = "Sorted";
    public const string ManifestFileName = "_manifest.csv";
    internal const string DuplicateReason = "identical to ";

    private const int ProgressInterval = 250;

    /// <summary>
    /// Reads every clip under <paramref name="sourceRoot"/> and decides where each one goes.
    /// Reads file contents to find exact duplicates, so it can take a while on a large pack.
    /// </summary>
    /// <param name="excludeRoot">A folder to leave out of the scan - the destination, when it sits inside the source.</param>
    public static AudioSortPlan Plan(
        string sourceRoot,
        string? excludeRoot = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) {
        string root = Path.GetFullPath(sourceRoot);
        string? exclude = excludeRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludeRoot));

        progress?.Report("Finding clips...");

        List<string> files = [.. Directory.EnumerateFiles(root, "*", new EnumerationOptions {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            })
            .Where(AudioLibrary.IsAudioFile)
            .Where(file => exclude is null || !IsUnder(file, exclude))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

        var known = new HashSet<string>(files, StringComparer.Ordinal);

        // Only a clip that shares its size with another can be a byte-for-byte duplicate, so
        // only those are hashed.
        Dictionary<long, int> sizeCounts = files
            .GroupBy(file => new FileInfo(Path.Combine(root, file)).Length)
            .ToDictionary(group => group.Key, group => group.Count());

        var firstWithHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<AudioSortEntry>(files.Count);

        for (int i = 0; i < files.Count; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            if (i % ProgressInterval == 0) progress?.Report($"Reading clips... {i:N0} of {files.Count:N0}");

            string file = files[i];
            string? reason = SkipReason(file, known);

            if (reason is null) {
                string full = Path.Combine(root, file);

                if (sizeCounts[new FileInfo(full).Length] > 1) {
                    string hash = HashFile(full);

                    if (firstWithHash.TryGetValue(hash, out string? original)) reason = DuplicateReason + original;
                    else firstWithHash[hash] = file;
                }
            }

            if (reason is not null) {
                entries.Add(new AudioSortEntry(file, null, reason));
                continue;
            }

            entries.Add(new AudioSortEntry(file, UniqueTarget(file, used), null));
        }

        return new AudioSortPlan(root, entries);
    }

    /// <summary>
    /// Builds the sorted folder from <paramref name="plan"/>. Clips already in place are left
    /// alone, so sorting again after adding a pack only adds the new ones. The pack's licence
    /// documents at its top level are carried across too, and a manifest of every decision is
    /// written beside them.
    /// </summary>
    public static AudioSortResult Apply(
        AudioSortPlan plan,
        string destinationRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) {
        string destination = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destination);

        int linked = 0, copied = 0, existing = 0, done = 0;
        List<AudioSortEntry> sorted = [.. plan.Sorted];

        foreach (AudioSortEntry entry in sorted) {
            cancellationToken.ThrowIfCancellationRequested();
            if (done++ % ProgressInterval == 0) progress?.Report($"Sorting clips... {done - 1:N0} of {sorted.Count:N0}");

            string target = Path.Combine(destination, entry.Target!);

            if (File.Exists(target)) {
                existing++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (LinkOrCopy(Path.Combine(plan.SourceRoot, entry.Source), target)) linked++;
            else copied++;
        }

        // The terms and notices the pack keeps at its top level travel with the clips.
        foreach (string document in Directory.EnumerateFiles(plan.SourceRoot).Where(file => !AudioLibrary.IsAudioFile(file))) {
            string target = Path.Combine(destination, Path.GetFileName(document));
            if (!File.Exists(target)) LinkOrCopy(document, target);
        }

        string manifest = Path.Combine(destination, ManifestFileName);
        WriteManifest(plan, manifest);

        return new AudioSortResult(linked, copied, existing, manifest);
    }

    /// <summary>
    /// Near-duplicates of a take: packs often ship the raw recording beside a cleaned-up one,
    /// or the same take through a filter, and drawing both would play the take twice.
    /// </summary>
    internal static string? SkipReason(string file, IReadOnlySet<string> known) {
        int slash = file.LastIndexOf('/');
        string folder = slash < 0 ? string.Empty : file[..slash];
        string name = file[(slash + 1)..];
        string lower = file.ToLowerInvariant();

        if (known.Contains($"{folder}/Processed/{name}")) return "raw copy of a Processed take";
        if (lower.Contains("/originals/") && known.Contains(file.Replace("/Originals/", "/Processed/"))) return "raw copy of a Processed take";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, "/processed ?- ?thin")) return "thin-filter variant";
        if (lower.Contains("/raw_singles/")) return "raw copy of Edited_Singles";

        return null;
    }

    /// <summary>
    /// "Performer - file name" inside the clip's target folder, numbered when two performers'
    /// files would otherwise collide. The performer stays in the name so credit is never lost.
    /// </summary>
    private static string UniqueTarget(string file, HashSet<string> used) {
        string[] parts = file.Split('/');
        string performer = parts.Length >= 3 && parts[0] is "Female" or "Male" ? parts[1] : parts[0];
        string name = parts.Length > 1 ? $"{performer} - {parts[^1]}" : parts[^1];
        string folder = AudioClassifier.TargetFolder(file);

        string target = $"{folder}/{name}";

        for (int n = 2; !used.Add(target); n++) {
            target = $"{folder}/{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
        }

        return target;
    }

    /// <summary>Hard-links <paramref name="source"/> at <paramref name="target"/>, or copies it when that fails.</summary>
    /// <returns><c>true</c> for a link, <c>false</c> for a copy.</returns>
    private static bool LinkOrCopy(string source, string target) {
        if (CreateHardLink(target, source, IntPtr.Zero)) return true;

        int error = Marshal.GetLastPInvokeError();
        const int NotSameDevice = 17;
        const int NotSupported = 50;
        const int InvalidFunction = 1;

        // Another drive, or a filesystem without hard links: a copy is the next best thing.
        if (error is NotSameDevice or NotSupported or InvalidFunction) {
            File.Copy(source, target);
            return false;
        }

        throw new IOException($"Could not link {target}: {new Win32Exception(error).Message}");
    }

    private static string HashFile(string path) {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream));
    }

    private static bool IsUnder(string file, string folder) =>
        file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void WriteManifest(AudioSortPlan plan, string path) {
        var csv = new StringBuilder("source,target,skipped\r\n");

        foreach (AudioSortEntry entry in plan.Entries) {
            csv.Append(Quote(entry.Source)).Append(',')
                .Append(Quote(entry.Target ?? string.Empty)).Append(',')
                .Append(Quote(entry.SkipReason ?? string.Empty)).Append("\r\n");
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
