using System.Text;

using AttcksMergeTool.Models;
using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

/// <summary>
/// Covers the settings file itself: that it round-trips, and that none of the ways it can be
/// broken take the app down with it.
/// </summary>
public class SettingsStoreTests
{
    [Fact]
    public void Settings_round_trip_through_the_file() {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Path("settings.json"));

        var saved = new AppSettings {
            InputFolder = @"D:\Scenes",
            OutputFolder = @"D:\Merged",
            FfmpegPath = @"C:\tools\ffmpeg.exe",
            MaxParallelEncodes = 6,
            TargetResolution = "3840:2160",
            Av1Quality = 24,
            X264Preset = "slow",
            TransitionMs = 250,
            VideoExtensions = [".mp4", ".mkv"],
            LogFontSize = 12F,
            WindowWidth = 1200,
            WindowHeight = 800
        };

        Assert.True(store.TrySave(saved, out string? error));
        Assert.Null(error);

        AppSettings loaded = store.Load();

        Assert.Equal(saved.InputFolder, loaded.InputFolder);
        Assert.Equal(saved.OutputFolder, loaded.OutputFolder);
        Assert.Equal(saved.FfmpegPath, loaded.FfmpegPath);
        Assert.Equal(6, loaded.MaxParallelEncodes);
        Assert.Equal("3840:2160", loaded.TargetResolution);
        Assert.Equal(24, loaded.Av1Quality);
        Assert.Equal("slow", loaded.X264Preset);
        Assert.Equal(250, loaded.TransitionMs);
        Assert.Equal([".mp4", ".mkv"], loaded.VideoExtensions);
        Assert.Equal(12F, loaded.LogFontSize);
        Assert.Equal(1200, loaded.WindowWidth);
    }

    /// <summary>First launch has no file, and that is not something to report.</summary>
    [Fact]
    public void A_missing_file_loads_the_defaults() {
        using var workspace = new TempWorkspace();

        AppSettings loaded = new SettingsStore(workspace.Path("settings.json")).Load();

        Assert.Equal(new AppSettings().InputFolder, loaded.InputFolder);
        Assert.Equal(3, loaded.MaxParallelEncodes);
        Assert.False(workspace.Exists("settings.json.bak"));
    }

    /// <summary>
    /// The file is meant to be hand-editable, so a typo has to cost the user their settings
    /// but not the file they can look at to find the typo.
    /// </summary>
    [Fact]
    public void A_corrupt_file_loads_the_defaults_and_is_kept_as_a_backup() {
        using var workspace = new TempWorkspace();
        string path = workspace.Path("settings.json");

        // Valid as far as it goes, then cut off mid-property - what a crash during a
        // write, or a truncating editor, would leave behind.
        File.WriteAllText(path, "{ \"inputFolder\": \"D:/Scenes\", \"targ");

        AppSettings loaded = new SettingsStore(path).Load();

        Assert.Equal(new AppSettings().InputFolder, loaded.InputFolder);
        Assert.True(workspace.Exists("settings.json.bak"));
        Assert.Contains("D:", workspace.ReadText("settings.json.bak"));
    }

    /// <summary>
    /// Settings written by a later build must not stop an earlier one from starting, and a
    /// property this build no longer has is exactly that case.
    /// </summary>
    [Fact]
    public void Unknown_properties_are_ignored() {
        using var workspace = new TempWorkspace();
        string path = workspace.Path("settings.json");

        File.WriteAllText(
            path,
            "{ \"targetFps\": 30, \"somethingFromTheFuture\": [1, 2, 3] }",
            new UTF8Encoding(false));

        AppSettings loaded = new SettingsStore(path).Load();

        Assert.Equal(30, loaded.TargetFps);
    }

    /// <summary>Values are repaired on the way in, not just on the way out of the dialog.</summary>
    [Fact]
    public void A_hand_edited_value_out_of_range_is_normalized_on_load() {
        using var workspace = new TempWorkspace();
        string path = workspace.Path("settings.json");

        File.WriteAllText(path, "{ \"maxParallelEncodes\": 999, \"targetResolution\": \"junk\" }");

        AppSettings loaded = new SettingsStore(path).Load();

        Assert.Equal(16, loaded.MaxParallelEncodes);
        Assert.Equal(AppSettings.DefaultTargetResolution, loaded.TargetResolution);
    }

    /// <summary>
    /// The write goes via a temp file so a crash cannot truncate the real one; that temp file
    /// must not survive the successful case.
    /// </summary>
    [Fact]
    public void A_successful_save_leaves_no_scratch_file_behind() {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Path("settings.json"));

        Assert.True(store.TrySave(new AppSettings(), out _));

        Assert.True(workspace.Exists("settings.json"));
        Assert.False(workspace.Exists("settings.json.tmp"));
    }

    [Fact]
    public void Saving_over_an_existing_file_replaces_it() {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Path("settings.json"));

        store.TrySave(new AppSettings { TargetFps = 30 }, out _);
        store.TrySave(new AppSettings { TargetFps = 120 }, out _);

        Assert.Equal(120, store.Load().TargetFps);
    }

    /// <summary>
    /// A read-only or otherwise unwritable location is reported, not thrown - losing a
    /// preference must never be what takes the window down.
    /// </summary>
    [Fact]
    public void An_unwritable_path_is_reported_rather_than_thrown() {
        using var workspace = new TempWorkspace();

        // A directory where the file should be: the write cannot succeed and never will.
        string path = workspace.Path("settings.json");
        Directory.CreateDirectory(path);

        bool saved = new SettingsStore(path).TrySave(new AppSettings(), out string? error);

        Assert.False(saved);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
