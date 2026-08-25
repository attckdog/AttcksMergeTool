using System.Text;
using System.Text.Json;

using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Reads and writes <c>settings.json</c>, which lives beside the executable so the whole
/// folder stays portable.
/// </summary>
/// <remarks>
/// Instance-based with a <see cref="Default"/> singleton, like <see cref="ProcessRunner"/>,
/// so tests can point one at a scratch folder instead of the build output. Nothing here
/// throws: a settings file is a convenience, and failing to read or write one must never be
/// what stops the app from starting or a job from running.
/// </remarks>
public sealed class SettingsStore
{
    public const string DefaultFileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // The file is meant to be hand-editable, so tolerate what a human would leave behind.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public SettingsStore(string? filePath = null) =>
        FilePath = filePath ?? MergeOptions.ResolvePath(DefaultFileName);

    /// <summary>The real store, used everywhere except tests.</summary>
    public static SettingsStore Default { get; } = new();

    public string FilePath { get; }

    /// <summary>Where a file we could not parse is moved before being replaced.</summary>
    public string BackupPath => FilePath + ".bak";

    private string TempPath => FilePath + ".tmp";

    /// <summary>
    /// The stored settings, or defaults when there is no file yet. A file that cannot be
    /// parsed is set aside as <see cref="BackupPath"/> rather than silently overwritten, so
    /// a typo in a hand-edit does not cost the user the rest of their settings.
    /// </summary>
    public AppSettings Load() {
        AppSettings settings = new();

        if (File.Exists(FilePath)) {
            try {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), SerializerOptions)
                    ?? new AppSettings();
            } catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) {
                TryBackup();
                settings = new AppSettings();
            }
        }

        settings.Normalize();

        return settings;
    }

    /// <summary>
    /// Writes <paramref name="settings"/>. Returns <c>false</c> with a reason instead of
    /// throwing, so the caller can log it and carry on.
    /// </summary>
    /// <remarks>
    /// Written to a temp file and moved into place: a crash or a full disk part-way through
    /// then leaves the previous settings intact rather than a truncated file that will not parse.
    /// </remarks>
    public bool TrySave(AppSettings settings, out string? error) {
        try {
            string json = JsonSerializer.Serialize(settings, SerializerOptions);

            File.WriteAllText(TempPath, json, new UTF8Encoding(false));
            File.Move(TempPath, FilePath, overwrite: true);

            error = null;
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
            error = exception.Message;
            TryDeleteTemp();

            return false;
        }
    }

    private void TryBackup() {
        try {
            File.Move(FilePath, BackupPath, overwrite: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // Keeping the unreadable file is a nicety; defaults still load without it.
        }
    }

    private void TryDeleteTemp() {
        try {
            if (File.Exists(TempPath)) File.Delete(TempPath);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // Nothing reads it, so a leftover temp file is noise rather than a problem.
        }
    }
}
