using AttcksMergeTool.Models;
using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

/// <summary>
/// Covers which funscripts are scenes and which are the per-axis siblings of one. This used
/// to be decided implicitly by sort order, which is why an axis name sorting before the
/// literal "funscript" turned a sibling into a scene of its own.
/// </summary>
public class SceneScriptIndexTests
{
    [Fact]
    public void A_script_with_no_siblings_is_a_scene() {
        List<SceneScripts> scenes = Build(["Scene.funscript"], ["Scene.mp4"]);

        SceneScripts scene = Assert.Single(scenes);
        Assert.Equal("Scene", scene.Name);
        Assert.EndsWith("Scene.funscript", scene.MainScriptPath);
        Assert.Empty(scene.SiblingScriptPaths);
    }

    [Theory]
    [InlineData("twist")]
    [InlineData("surge")]
    public void A_known_axis_sibling_folds_into_its_scene(string axis) {
        List<SceneScripts> scenes = Build([$"Scene.{axis}.funscript", "Scene.funscript"], ["Scene.mp4"]);

        SceneScripts scene = Assert.Single(scenes);
        Assert.Equal("Scene", scene.Name);
        Assert.EndsWith($"Scene.{axis}.funscript", Assert.Single(scene.SiblingScriptPaths));
    }

    /// <remarks>
    /// The regression this class exists for. "alpha" sorts before "funscript", so walking the
    /// folder in name order reached the sibling first and merged it as a scene: its keyframes
    /// landed on the root axis, it emitted a bookmark and a chapter of its own, and it pushed
    /// every later scene along the timeline.
    /// </remarks>
    [Fact]
    public void An_axis_whose_name_sorts_before_funscript_is_still_a_sibling() {
        List<SceneScripts> scenes = Build(["Scene.alpha.funscript", "Scene.funscript"], ["Scene.mp4"]);

        SceneScripts scene = Assert.Single(scenes);
        Assert.Equal("Scene", scene.Name);
        Assert.EndsWith("Scene.alpha.funscript", Assert.Single(scene.SiblingScriptPaths));
    }

    [Fact]
    public void A_sibling_whose_scene_has_a_video_but_no_main_script_still_merges() {
        List<SceneScripts> scenes = Build(["Scene.alpha.funscript"], ["Scene.mp4"]);

        SceneScripts scene = Assert.Single(scenes);
        Assert.Equal("Scene", scene.Name);
        Assert.Null(scene.MainScriptPath);
        Assert.Single(scene.SiblingScriptPaths);
    }

    /// <remarks>
    /// A scene is allowed dots in its name. Owning a video is what settles the ambiguity:
    /// "My.Video.mp4" makes "My.Video" a scene rather than the "Video" axis of "My".
    /// </remarks>
    [Fact]
    public void A_dotted_name_with_its_own_video_is_a_scene_not_an_axis() {
        List<SceneScripts> scenes = Build(
            ["My.funscript", "My.Video.funscript"],
            ["My.mp4", "My.Video.mp4"]);

        // Ordered by file name, so the dotted one comes first - the same order the videos
        // themselves list in, which is the whole point of the sort key.
        Assert.Equal(["My.Video", "My"], scenes.Select(scene => scene.Name));
        Assert.All(scenes, scene => Assert.Empty(scene.SiblingScriptPaths));
        Assert.All(scenes, scene => Assert.NotNull(scene.MainScriptPath));
    }

    [Fact]
    public void A_dotted_name_with_no_video_of_its_own_is_an_axis() {
        List<SceneScripts> scenes = Build(["My.funscript", "My.Video.funscript"], ["My.mp4"]);

        SceneScripts scene = Assert.Single(scenes);
        Assert.Equal("My", scene.Name);
        Assert.Single(scene.SiblingScriptPaths);
    }

    [Fact]
    public void A_script_with_no_video_at_all_is_still_a_scene() {
        List<SceneScripts> scenes = Build(["Orphan.funscript"], []);

        Assert.Equal("Orphan", Assert.Single(scenes).Name);
    }

    /// <remarks>
    /// Scene order has to match the order the videos are concatenated in, and the video list
    /// is ordered by file name - so the scenes are ordered by their notional script file name,
    /// not by the bare scene name, which sorts differently when one name prefixes another.
    /// </remarks>
    [Fact]
    public void Scenes_come_out_in_the_same_order_as_the_videos() {
        // "A B" before "A" because a space sorts below the extension separator - the point
        // being that scene order follows the file name, exactly as the video listing does.
        string[] videos = ["A.mp4", "B.mp4", "A B.mp4"];
        string[] scripts = ["A.funscript", "B.funscript", "A B.funscript"];

        List<SceneScripts> scenes = Build(scripts, videos);

        Assert.Equal(
            videos.Order(StringComparer.Ordinal).Select(Path.GetFileNameWithoutExtension),
            scenes.Select(scene => scene.Name));
    }

    [Fact]
    public void Siblings_of_one_scene_come_out_in_a_stable_order() {
        List<SceneScripts> scenes = Build(
            ["Scene.twist.funscript", "Scene.surge.funscript", "Scene.funscript"],
            ["Scene.mp4"]);

        Assert.Equal(
            ["Scene.surge.funscript", "Scene.twist.funscript"],
            Assert.Single(scenes).SiblingScriptPaths.Select(Path.GetFileName));
    }

    [Fact]
    public void The_axis_alias_is_whatever_sits_between_the_scene_name_and_the_extension() {
        var scene = new SceneScripts("Scene", null, []);

        Assert.Equal("twist", SceneScriptIndex.AxisAliasOf(scene, @"C:\in\Scene.Twist.funscript"));
        Assert.Equal("l1", SceneScriptIndex.AxisAliasOf(scene, @"C:\in\Scene.L1.funscript"));
    }

    [Fact]
    public void Classification_survives_a_real_folder() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("Scene.funscript", ScriptBuilder.Basic((0, 0)));
        workspace.WriteScript("Scene.alpha.funscript", ScriptBuilder.Basic((0, 0)));
        workspace.WriteVideo("Scene.mp4");

        SceneScripts scene = Assert.Single(SceneScriptIndex.Build(workspace.Root));

        Assert.Equal("Scene", scene.Name);
        Assert.Single(scene.SiblingScriptPaths);
    }

    private static List<SceneScripts> Build(IEnumerable<string> scripts, IEnumerable<string> videos) =>
        SceneScriptIndex.Build(
            scripts.Select(name => Path.Combine(@"C:\in", name)),
            videos.Select(name => Path.Combine(@"C:\in", name)));
}
