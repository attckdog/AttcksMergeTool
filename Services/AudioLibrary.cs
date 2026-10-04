using System.Collections.Concurrent;
using System.Text.Json;

namespace AttcksMergeTool.Services;

/// <summary>
/// The voice clips the user has dropped into the audio folder beside the executable, indexed
/// as a folder tree so a video can draw random clips from any set of its folders.
/// </summary>
/// <remarks>
/// Only file names, sizes and timestamps are read by a scan, which keeps it fast even for packs
/// with thousands of clips. Durations are probed lazily, for the clips a merge actually picks,
/// and kept in <see cref="IndexFileName"/> keyed by size and last-write time so a clip is only
/// ever probed once until it changes.
/// </remarks>
public sealed class AudioLibrary
{
    public const string DefaultFolder = "Audio";
    public const string IndexFileName = "audio-index.json";

    /// <summary>Which file extensions count as voice clips. Everything else in a pack is ignored.</summary>
    public static IReadOnlyList<string> AudioExtensions { get; } =
        [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".opus", ".aac", ".wma"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly EnumerationOptions ScanOptions = new() {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
    };

    private readonly Dictionary<string, AudioFolder> _folders;
    private readonly ConcurrentDictionary<string, AudioIndexEntry> _entries;
    private readonly string? _indexPath;
    private int _dirty;

    private AudioLibrary(
        string rootPath,
        AudioFolder root,
        ConcurrentDictionary<string, AudioIndexEntry> entries,
        string? indexPath,
        bool dirty) {
        RootPath = rootPath;
        Root = root;
        _entries = entries;
        _indexPath = indexPath;
        _dirty = dirty ? 1 : 0;
        _folders = new Dictionary<string, AudioFolder>(StringComparer.OrdinalIgnoreCase);

        Register(root);
    }

    /// <summary>The library's folder, resolved.</summary>
    public string RootPath { get; }

    /// <summary>The top of the tree. Its <see cref="AudioFolder.RelativePath"/> is empty.</summary>
    public AudioFolder Root { get; }

    /// <summary>Every clip in the library.</summary>
    public int FileCount => Root.TotalFiles;

    /// <summary>A library with nothing in it, for merges that inject no audio.</summary>
    public static AudioLibrary Empty { get; } = new(
        string.Empty, new AudioFolder(string.Empty, string.Empty, [], []), new(StringComparer.OrdinalIgnoreCase), null, false);

    /// <summary>
    /// Walks <paramref name="rootPath"/> and builds the tree. A missing folder gives an empty
    /// library rather than an error - the feature is optional and most users start without one.
    /// </summary>
    /// <param name="indexPath">
    /// Where durations are cached between runs, or <c>null</c> to keep them in memory only.
    /// </param>
    public static AudioLibrary Scan(string rootPath, string? indexPath = null) {
        string root = Path.GetFullPath(rootPath);
        Dictionary<string, AudioIndexEntry> previous = LoadIndex(indexPath);
        var entries = new ConcurrentDictionary<string, AudioIndexEntry>(StringComparer.OrdinalIgnoreCase);

        AudioFolder rootFolder = Directory.Exists(root)
            ? ScanFolder(root, root, previous, entries) ?? new AudioFolder(Path.GetFileName(root), string.Empty, [], [])
            : new AudioFolder(Path.GetFileName(root), string.Empty, [], []);

        // Anything gone, added or changed means the file on disk no longer describes the folder.
        bool dirty = previous.Count != entries.Count
                     || entries.Any(pair => !previous.TryGetValue(pair.Key, out AudioIndexEntry? old) || old != pair.Value);

        return new AudioLibrary(root, rootFolder, entries, indexPath, dirty);
    }

    /// <summary>The folder at <paramref name="relativePath"/>, or <c>null</c> when it is not in the library.</summary>
    public AudioFolder? FindFolder(string relativePath) =>
        _folders.GetValueOrDefault(NormalizeRelative(relativePath));

    /// <summary>
    /// Every clip in or below any of <paramref name="relativeFolders"/>, as full paths, once each
    /// even when a folder and one of its subfolders are both chosen. Folders not in the library
    /// are skipped.
    /// </summary>
    public IReadOnlyList<string> FilesUnder(IEnumerable<string> relativeFolders) {
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string relative in relativeFolders) {
            if (FindFolder(relative) is { } folder) folder.CollectFiles(files);
        }

        return [.. files];
    }

    /// <summary>The folders among <paramref name="relativeFolders"/> that are not in the library.</summary>
    public IReadOnlyList<string> MissingFolders(IEnumerable<string> relativeFolders) =>
        [.. relativeFolders.Where(relative => FindFolder(relative) is null)];

    /// <summary>
    /// How long the clip at <paramref name="fullPath"/> plays for, from the index when it has
    /// already been measured and from <paramref name="probe"/> otherwise.
    /// </summary>
    public async Task<int?> GetDurationMsAsync(string fullPath, IMediaProbe probe, CancellationToken cancellationToken = default) {
        string key = KeyFor(fullPath);

        if (_entries.TryGetValue(key, out AudioIndexEntry? entry) && entry.DurationMs is { } known) return known;

        int? durationMs = await probe.GetDurationMsAsync(fullPath, cancellationToken);

        if (durationMs is not null && entry is not null) {
            _entries[key] = entry with { DurationMs = durationMs };
            Interlocked.Exchange(ref _dirty, 1);
        }

        return durationMs;
    }

    /// <summary>
    /// Writes the index out if anything changed since it was read. Never throws: a cache that
    /// could not be saved only costs a re-probe next time.
    /// </summary>
    public bool TrySaveIndex(out string? error) {
        error = null;

        if (_indexPath is null || Interlocked.CompareExchange(ref _dirty, 0, 0) == 0) return true;

        try {
            var file = new AudioIndexFile(RootPath, [.. _entries.Values.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)]);
            string temp = _indexPath + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, _indexPath, overwrite: true);

            Interlocked.Exchange(ref _dirty, 0);
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Folder paths are stored with backslashes and no leading or trailing separator.</summary>
    public static string NormalizeRelative(string relativePath) =>
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private string KeyFor(string fullPath) => Path.GetRelativePath(RootPath, fullPath);

    private void Register(AudioFolder folder) {
        _folders[folder.RelativePath] = folder;
        foreach (AudioFolder child in folder.Children) Register(child);
    }

    /// <summary>One folder and everything below it, or <c>null</c> when none of it holds a clip.</summary>
    private static AudioFolder? ScanFolder(
        string root,
        string path,
        Dictionary<string, AudioIndexEntry> previous,
        ConcurrentDictionary<string, AudioIndexEntry> entries) {
        var children = new List<AudioFolder>();

        foreach (string directory in Directory.EnumerateDirectories(path, "*", ScanOptions)
                     .Order(StringComparer.OrdinalIgnoreCase)) {
            if (ScanFolder(root, directory, previous, entries) is { } child) children.Add(child);
        }

        var files = new List<string>();

        foreach (string file in Directory.EnumerateFiles(path, "*", ScanOptions)
                     .Where(IsAudioFile)
                     .Order(StringComparer.OrdinalIgnoreCase)) {
            var info = new FileInfo(file);
            string key = Path.GetRelativePath(root, file);
            long ticks = info.LastWriteTimeUtc.Ticks;

            // A clip keeps its measured duration only while it is still the same file.
            int? durationMs = previous.TryGetValue(key, out AudioIndexEntry? old)
                              && old.Size == info.Length && old.ModifiedTicks == ticks
                ? old.DurationMs
                : null;

            entries[key] = new AudioIndexEntry(key, info.Length, ticks, durationMs);
            files.Add(file);
        }

        if (files.Count == 0 && children.Count == 0) return null;

        string relative = Path.GetRelativePath(root, path);
        if (relative == ".") relative = string.Empty;

        return new AudioFolder(Path.GetFileName(path), relative, children, files);
    }

    private static Dictionary<string, AudioIndexEntry> LoadIndex(string? indexPath) {
        var entries = new Dictionary<string, AudioIndexEntry>(StringComparer.OrdinalIgnoreCase);

        if (indexPath is null || !File.Exists(indexPath)) return entries;

        try {
            AudioIndexFile? file = JsonSerializer.Deserialize<AudioIndexFile>(File.ReadAllText(indexPath), JsonOptions);

            foreach (AudioIndexEntry entry in file?.Files ?? []) {
                if (!string.IsNullOrEmpty(entry.Path)) entries[entry.Path] = entry;
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            // A cache, not a source of truth: an unreadable one is simply rebuilt.
        }

        return entries;
    }

    /// <summary>One clip as it is stored in the index file.</summary>
    /// <param name="Path">Relative to the library root.</param>
    private sealed record AudioIndexEntry(string Path, long Size, long ModifiedTicks, int? DurationMs);

    private sealed record AudioIndexFile(string Root, List<AudioIndexEntry> Files);
}

/// <summary>A folder of the audio library and what is in and under it.</summary>
public sealed class AudioFolder
{
    public AudioFolder(string name, string relativePath, IReadOnlyList<AudioFolder> children, IReadOnlyList<string> files) {
        Name = name;
        RelativePath = relativePath;
        Children = children;
        Files = files;
        TotalFiles = files.Count + children.Sum(child => child.TotalFiles);
    }

    public string Name { get; }

    /// <summary>Relative to the library root; empty for the root itself.</summary>
    public string RelativePath { get; }

    public IReadOnlyList<AudioFolder> Children { get; }

    /// <summary>The clips directly in this folder, as full paths.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>The clips in this folder and every folder below it.</summary>
    public int TotalFiles { get; }

    internal void CollectFiles(ISet<string> into) {
        foreach (string file in Files) into.Add(file);
        foreach (AudioFolder child in Children) child.CollectFiles(into);
    }
}
