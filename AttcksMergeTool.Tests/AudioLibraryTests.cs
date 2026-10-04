using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

public class AudioLibraryTests
{
    /// <remarks>
    /// Voice packs ship with licence PDFs, shortcuts and readme files beside the clips. None of
    /// them can be mixed into a video, so none of them may end up in a pool.
    /// </remarks>
    [Fact]
    public void Only_audio_files_are_indexed() {
        using var workspace = new TempWorkspace();
        WriteClip(workspace, @"Pack\a.mp3");
        WriteClip(workspace, @"Pack\b.WAV");
        WriteClip(workspace, @"Pack\terms.pdf");
        WriteClip(workspace, @"Pack\site.url");

        AudioLibrary library = AudioLibrary.Scan(workspace.Root);

        Assert.Equal(2, library.FileCount);
        Assert.Equal(["a.mp3", "b.WAV"], library.FilesUnder(["Pack"]).Select(Path.GetFileName));
    }

    [Fact]
    public void A_folder_counts_every_clip_below_it() {
        using var workspace = new TempWorkspace();
        WriteClip(workspace, @"Female\Kaya\Moans\1.mp3");
        WriteClip(workspace, @"Female\Kaya\Moans\2.mp3");
        WriteClip(workspace, @"Female\Kaya\Oral\1.mp3");
        WriteClip(workspace, @"Female\Other\1.mp3");

        AudioLibrary library = AudioLibrary.Scan(workspace.Root);

        Assert.Equal(4, library.FindFolder("Female")?.TotalFiles);
        Assert.Equal(3, library.FindFolder(@"Female\Kaya")?.TotalFiles);
        Assert.Equal(2, library.FindFolder("Female/Kaya/Moans")?.TotalFiles);
    }

    /// <remarks>
    /// A folder holding nothing but documents would show up in the picker as a choice that
    /// yields no clips.
    /// </remarks>
    [Fact]
    public void Folders_without_audio_are_left_out() {
        using var workspace = new TempWorkspace();
        WriteClip(workspace, @"Pack\a.mp3");
        WriteClip(workspace, @"Docs\readme.txt");
        Directory.CreateDirectory(workspace.Path("Empty"));

        AudioLibrary library = AudioLibrary.Scan(workspace.Root);

        Assert.NotNull(library.FindFolder("Pack"));
        Assert.Null(library.FindFolder("Docs"));
        Assert.Null(library.FindFolder("Empty"));
    }

    [Fact]
    public void A_folder_and_its_subfolder_contribute_each_clip_once() {
        using var workspace = new TempWorkspace();
        WriteClip(workspace, @"Kaya\Moans\1.mp3");
        WriteClip(workspace, @"Kaya\Oral\1.mp3");

        AudioLibrary library = AudioLibrary.Scan(workspace.Root);

        Assert.Equal(2, library.FilesUnder(["Kaya", @"Kaya\Moans"]).Count);
    }

    [Fact]
    public void Missing_folders_are_skipped_and_reported() {
        using var workspace = new TempWorkspace();
        WriteClip(workspace, @"Kaya\1.mp3");

        AudioLibrary library = AudioLibrary.Scan(workspace.Root);

        Assert.Single(library.FilesUnder(["Kaya", "Gone"]));
        Assert.Equal(["Gone"], library.MissingFolders(["Kaya", "Gone"]));
    }

    [Fact]
    public void A_missing_library_folder_is_an_empty_library() {
        using var workspace = new TempWorkspace();

        AudioLibrary library = AudioLibrary.Scan(workspace.Path("Nowhere"));

        Assert.Equal(0, library.FileCount);
        Assert.Empty(library.FilesUnder([""]));
    }

    /// <remarks>
    /// Probing thousands of clips on every launch would take minutes, so a measured length is
    /// kept in the index and reused for as long as the file is unchanged.
    /// </remarks>
    [Fact]
    public async Task A_measured_duration_is_remembered_across_scans() {
        using var workspace = new TempWorkspace();
        string clip = WriteClip(workspace, @"Audio\Kaya\1.mp3");
        string index = workspace.Path("audio-index.json");

        AudioLibrary first = AudioLibrary.Scan(workspace.Path("Audio"), index);
        await first.GetDurationMsAsync(clip, new FakeMediaProbe().WithDuration("1.mp3", 4200));
        Assert.True(first.TrySaveIndex(out _));

        AudioLibrary second = AudioLibrary.Scan(workspace.Path("Audio"), index);
        var unreadable = new FakeMediaProbe();

        Assert.Equal(4200, await second.GetDurationMsAsync(clip, unreadable));
    }

    [Fact]
    public async Task A_changed_clip_is_measured_again() {
        using var workspace = new TempWorkspace();
        string clip = WriteClip(workspace, @"Audio\Kaya\1.mp3");
        string index = workspace.Path("audio-index.json");

        AudioLibrary first = AudioLibrary.Scan(workspace.Path("Audio"), index);
        await first.GetDurationMsAsync(clip, new FakeMediaProbe().WithDuration("1.mp3", 4200));
        first.TrySaveIndex(out _);

        File.WriteAllText(clip, "a longer recording than before");

        AudioLibrary second = AudioLibrary.Scan(workspace.Path("Audio"), index);

        Assert.Equal(9000, await second.GetDurationMsAsync(clip, new FakeMediaProbe().WithDuration("1.mp3", 9000)));
    }

    [Fact]
    public void A_corrupt_index_is_rebuilt() {
        using var workspace = new TempWorkspace();
        WriteClip(workspace, @"Audio\Kaya\1.mp3");
        string index = workspace.Path("audio-index.json");
        File.WriteAllText(index, "{ not json");

        AudioLibrary library = AudioLibrary.Scan(workspace.Path("Audio"), index);

        Assert.Equal(1, library.FileCount);
        Assert.True(library.TrySaveIndex(out _));
        Assert.Contains("1.mp3", File.ReadAllText(index));
    }

    private static string WriteClip(TempWorkspace workspace, string relativePath) {
        string path = workspace.Path(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "clip");
        return path;
    }
}
