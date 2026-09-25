using AttcksMergeTool.Models;
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

    /// <remarks>
    /// The default, and what the scan did before the option existed: a file one folder down is
    /// not an input unless the user asked for subfolders.
    /// </remarks>
    [Fact]
    public void Subfolders_are_left_out_unless_the_scan_asks_for_them() {
        using var workspace = new TempWorkspace();
        workspace.WriteVideo("Top.mp4");
        workspace.WriteVideo(@"Nested\Deep.mp4");
        workspace.WriteScript(@"Nested\Deep.funscript", ScriptBuilder.Basic((0, 0)));

        Assert.Equal(["Top.mp4"], MediaFileScanner.FindVideos(workspace.Root).Select(Path.GetFileName));
        Assert.Empty(MediaFileScanner.FindFunscripts(workspace.Root));
    }

    [Fact]
    public void A_recursive_scan_lists_every_folder_beneath_the_root() {
        using var workspace = new TempWorkspace();
        workspace.WriteVideo("Top.mp4");
        workspace.WriteVideo(@"Nested\Deep.mp4");
        workspace.WriteVideo(@"Nested\Deeper\Deepest.mp4");
        workspace.WriteScript(@"Nested\Deep.funscript", ScriptBuilder.Basic((0, 0)));

        var scan = new InputScan(Recursive: true);

        Assert.Equal(
            ["Deep.mp4", "Deepest.mp4", "Top.mp4"],
            MediaFileScanner.FindVideos(workspace.Root, scan: scan).Select(Path.GetFileName).Order());

        Assert.Equal(
            ["Deep.funscript"],
            MediaFileScanner.FindFunscripts(workspace.Root, scan).Select(Path.GetFileName));
    }

    /// <remarks>
    /// The failure a recursive scan invites: point the output or the temp folder at a subfolder
    /// of the input folder, and the next run would take its own previous output - or a
    /// half-written intermediate segment - back in as a source video.
    /// </remarks>
    [Fact]
    public void A_recursive_scan_skips_the_folders_the_job_writes_into() {
        using var workspace = new TempWorkspace();
        workspace.WriteVideo("Scene.mp4");
        workspace.WriteVideo(@"Merged\MergedScript.mp4");
        workspace.WriteVideo(@"TempTS\0001.mp4");
        workspace.WriteScript(@"Merged\MergedScript.funscript", ScriptBuilder.Basic((0, 0)));

        var scan = new InputScan(
            Recursive: true, ExcludedFolders: [workspace.Path("Merged"), workspace.Path("TempTS")]);

        Assert.Equal(
            ["Scene.mp4"],
            MediaFileScanner.FindVideos(workspace.Root, scan: scan).Select(Path.GetFileName));

        Assert.Empty(MediaFileScanner.FindFunscripts(workspace.Root, scan));
    }

    /// <remarks>
    /// Writing the output into the input folder itself is a choice about where files land, not
    /// a request for an empty scan - so only a folder strictly inside the root excludes anything.
    /// </remarks>
    [Fact]
    public void The_scanned_folder_is_never_excluded_from_its_own_scan() {
        using var workspace = new TempWorkspace();
        workspace.WriteVideo("Scene.mp4");

        var scan = new InputScan(Recursive: true, ExcludedFolders: [workspace.Root]);

        Assert.Equal(
            ["Scene.mp4"],
            MediaFileScanner.FindVideos(workspace.Root, scan: scan).Select(Path.GetFileName));
    }

    /// <remarks>
    /// A sibling axis file shares its scene's name only up to the axis - "Scene.twist" is not
    /// "Scene" - so a scene and its axes must not be reported as an ambiguous pair.
    /// </remarks>
    [Fact]
    public void Only_a_name_carried_by_more_than_one_file_is_reported_as_ambiguous() {
        string[] videos = [@"C:\in\A\Scene.mp4", @"C:\in\B\Scene.mp4", @"C:\in\Solo.mp4"];
        string[] scripts = [@"C:\in\Scene.funscript", @"C:\in\Scene.twist.funscript"];

        Assert.Equal(["Scene"], MediaFileScanner.DuplicateBaseNames(videos));
        Assert.Empty(MediaFileScanner.DuplicateBaseNames(scripts));

        string? warning = MediaFileScanner.AmbiguousNameWarning(videos, scripts);

        Assert.Contains("Scene", warning);
        Assert.DoesNotContain("Solo", warning);
        Assert.Null(MediaFileScanner.AmbiguousNameWarning(scripts));
    }
}
