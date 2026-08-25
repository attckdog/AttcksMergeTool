using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

public class MediaFileScannerTests
{
    [Fact]
    public void Video_extensions_are_matched_without_regard_to_case() {
        Assert.True(MediaFileScanner.IsVideoFile(@"C:\in\Scene.MP4"));
        Assert.True(MediaFileScanner.IsVideoFile(@"C:\in\Scene.MkV"));
        Assert.False(MediaFileScanner.IsVideoFile(@"C:\in\Scene.funscript"));
        Assert.False(MediaFileScanner.IsVideoFile(@"C:\in\Scene"));
    }

    /// <remarks>
    /// The listing order decides where each scene lands on the merged timeline, so it has to
    /// be the same on every machine. The default string comparer is culture-sensitive.
    /// </remarks>
    [Fact]
    public void Listings_are_ordered_ordinally() {
        using var workspace = new TempWorkspace();
        foreach (string name in new[] { "b.mp4", "A.mp4", "a-b.mp4", "ab.mp4" }) {
            workspace.WriteVideo(name);
        }

        List<string> videos = MediaFileScanner.FindVideos(workspace.Root);

        Assert.Equal(
            ["A.mp4", "a-b.mp4", "ab.mp4", "b.mp4"],
            videos.Select(Path.GetFileName));
    }

    [Fact]
    public void Only_media_files_are_listed() {
        using var workspace = new TempWorkspace();
        workspace.WriteVideo("Scene.mp4");
        workspace.WriteScript("Scene.funscript", ScriptBuilder.Basic((0, 0)));
        File.WriteAllText(workspace.Path("notes.txt"), "ignore me");

        Assert.Equal(["Scene.mp4"], MediaFileScanner.FindVideos(workspace.Root).Select(Path.GetFileName));
        Assert.Equal(["Scene.funscript"], MediaFileScanner.FindFunscripts(workspace.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void A_missing_folder_lists_nothing_rather_than_throwing() {
        Assert.Empty(MediaFileScanner.FindVideos(@"C:\definitely\not\here"));
        Assert.Empty(MediaFileScanner.FindFunscripts(@"C:\definitely\not\here"));
    }
}
