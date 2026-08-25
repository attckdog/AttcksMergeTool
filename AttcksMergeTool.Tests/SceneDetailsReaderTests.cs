using AttcksMergeTool.Models;
using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

/// <summary>
/// Covers what the video list reports about a scene. The axis count is the interesting half:
/// it has to describe what a merge would emit, not how many files happen to be lying next to
/// the video, or the column would disagree with the merged script it is meant to preview.
/// </summary>
public class SceneDetailsReaderTests
{
    [Fact]
    public async Task A_plain_script_contributes_the_root_axis() {
        using var workspace = new TempWorkspace();

        workspace.WriteVideo("Scene.mp4");
        workspace.WriteScript("Scene.funscript", ScriptBuilder.Basic((0, 10), (500, 90)));

        Assert.Equal(1, await CountAxesAsync(workspace));
    }

    [Fact]
    public async Task Embedded_axes_and_sibling_files_both_count() {
        using var workspace = new TempWorkspace();

        workspace.WriteVideo("Scene.mp4");
        workspace.WriteScript("Scene.funscript", ScriptBuilder.Basic((0, 10)).WithAxis("L1", (0, 20)));
        workspace.WriteScript("Scene.twist.funscript", ScriptBuilder.Basic((0, 30)));

        // Root, the embedded surge and the twist sibling.
        Assert.Equal(3, await CountAxesAsync(workspace));
    }

    /// <remarks>
    /// The alias and the embedded id are two spellings of one axis - "twist" resolves to R0 -
    /// and the merge folds them onto a single track, so the count has to as well.
    /// </remarks>
    [Fact]
    public async Task An_axis_supplied_twice_counts_once() {
        using var workspace = new TempWorkspace();

        workspace.WriteVideo("Scene.mp4");
        workspace.WriteScript("Scene.funscript", ScriptBuilder.Basic((0, 10)).WithAxis("R0", (0, 20)));
        workspace.WriteScript("Scene.twist.funscript", ScriptBuilder.Basic((0, 30)));

        Assert.Equal(2, await CountAxesAsync(workspace));
    }

    /// <remarks>
    /// Matching <see cref="FunscriptMerger"/>, which only registers an axis it has keyframes
    /// for: an empty sibling file produces no track in the output and so is not an axis here.
    /// </remarks>
    [Fact]
    public async Task An_empty_sibling_contributes_no_axis() {
        using var workspace = new TempWorkspace();

        workspace.WriteVideo("Scene.mp4");
        workspace.WriteScript("Scene.funscript", ScriptBuilder.Basic((0, 10)));
        workspace.WriteScript("Scene.surge.funscript", ScriptBuilder.Basic());

        Assert.Equal(1, await CountAxesAsync(workspace));
    }

    /// <remarks>
    /// This feeds a list, not a merge. A file that is not JSON at all must leave the other
    /// rows alone rather than taking the scan down with it.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_script_is_skipped_rather_than_thrown_over() {
        using var workspace = new TempWorkspace();

        workspace.WriteVideo("Scene.mp4");
        File.WriteAllText(workspace.Path("Scene.funscript"), "this is not a funscript");
        workspace.WriteScript("Scene.twist.funscript", ScriptBuilder.Basic((0, 30)));

        Assert.Equal(1, await CountAxesAsync(workspace));
    }

    [Fact]
    public async Task A_video_with_no_script_reports_none() {
        using var workspace = new TempWorkspace();

        string video = workspace.WriteVideo("Scene.mp4");

        SceneDetails details = await SceneDetailsReader.ReadAsync(
            video, SceneOf(workspace, "Scene"), new FakeMediaProbe().WithDuration("Scene.mp4", 4000));

        Assert.False(details.HasScript);
        Assert.Equal(0, details.AxisCount);
        Assert.Equal(4000, details.DurationMs);
    }

    /// <remarks>
    /// Null rather than zero, so the list can say it does not know instead of claiming the
    /// video is empty - see <see cref="IMediaProbe.GetDurationMsAsync"/>.
    /// </remarks>
    [Fact]
    public async Task A_duration_that_could_not_be_read_stays_null() {
        using var workspace = new TempWorkspace();

        string video = workspace.WriteVideo("Scene.mp4");
        workspace.WriteScript("Scene.funscript", ScriptBuilder.Basic((0, 10)));

        SceneDetails details = await SceneDetailsReader.ReadAsync(
            video, SceneOf(workspace, "Scene"), new FakeMediaProbe());

        Assert.Null(details.DurationMs);
        Assert.True(details.HasScript);
    }

    private static Task<int> CountAxesAsync(TempWorkspace workspace) =>
        SceneDetailsReader.CountAxesAsync(SceneOf(workspace, "Scene")!);

    /// <summary>
    /// The scene as the window classifies it, so these tests see exactly the scene the list
    /// would hand the reader.
    /// </summary>
    private static SceneScripts? SceneOf(TempWorkspace workspace, string name) =>
        SceneScriptIndex.Build(workspace.Root).FirstOrDefault(scene => scene.Name == name);
}
