using AttcksMergeTool.Models;
using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

/// <summary>
/// The plan is the one place that decides what lands where, so these are the guards on the
/// two outputs describing the same scenes.
/// </summary>
public class TimelinePlanTests
{
    [Fact]
    public void The_videos_define_the_timeline_in_concat_order() {
        TimelinePlan plan = TimelinePlan.Build(
            [Scene("B"), Scene("A")],
            [@"C:\in\A.mp4", @"C:\in\B.mkv"]);

        Assert.Equal(["A", "B"], plan.Entries.Select(entry => entry.Name));
        Assert.All(plan.Entries, entry => Assert.NotNull(entry.Scripts));
        Assert.Empty(plan.SkippedScenes);
        Assert.Empty(plan.UnscriptedVideos);
    }

    [Fact]
    public void A_video_with_no_script_keeps_its_place_with_no_scripts() {
        TimelinePlan plan = TimelinePlan.Build([Scene("A")], [@"C:\in\A.mp4", @"C:\in\B.mp4"]);

        Assert.Equal(["A", "B"], plan.Entries.Select(entry => entry.Name));
        Assert.Null(plan.Entries[1].Scripts);
        Assert.Equal(["B"], plan.UnscriptedVideos.Select(entry => entry.Name));
        Assert.Empty(plan.SkippedScenes);
    }

    [Fact]
    public void A_script_with_no_video_is_left_off_the_timeline() {
        TimelinePlan plan = TimelinePlan.Build([Scene("A"), Scene("B")], [@"C:\in\A.mp4"]);

        Assert.Equal(["A"], plan.Entries.Select(entry => entry.Name));
        Assert.Equal(["B"], plan.SkippedScenes.Select(scene => scene.Name));
    }

    /// <remarks>
    /// There is no video to stay in sync with, so nothing is dropped and every scene keeps its
    /// own order.
    /// </remarks>
    [Fact]
    public void A_script_only_run_plans_every_scene() {
        TimelinePlan plan = TimelinePlan.Build([Scene("A"), Scene("B")], []);

        Assert.Equal(["A", "B"], plan.Entries.Select(entry => entry.Name));
        Assert.All(plan.Entries, entry => Assert.Null(entry.VideoPath));
        Assert.Empty(plan.SkippedScenes);
    }

    /// <remarks>
    /// Windows filenames are case-insensitive, so a scene and its video that differ only in
    /// case are the same scene and must not be reported as two unpaired files.
    /// </remarks>
    [Fact]
    public void Pairing_ignores_case() {
        TimelinePlan plan = TimelinePlan.Build([Scene("scene")], [@"C:\in\SCENE.MKV"]);

        Assert.NotNull(Assert.Single(plan.Entries).Scripts);
        Assert.Empty(plan.SkippedScenes);
    }

    /// <remarks>
    /// The window turns this on by default: a video nothing scripts is usually in the folder by
    /// accident, and merging it puts a stretch of dead timeline in the middle of the output.
    /// </remarks>
    [Fact]
    public void An_unscripted_video_is_left_out_when_the_job_asks_for_it() {
        TimelinePlan plan = TimelinePlan.Build(
            [Scene("A")], [@"C:\in\A.mp4", @"C:\in\B.mp4"], skipUnscriptedVideos: true);

        Assert.Equal(["A"], plan.Entries.Select(entry => entry.Name));
        Assert.Equal(["B"], plan.SkippedVideos.Select(entry => entry.Name));

        // It is off the timeline, so it is not one of the videos playing unscripted on it.
        Assert.Empty(plan.UnscriptedVideos);
    }

    [Fact]
    public void Skipping_leaves_the_scripted_videos_in_concat_order() {
        TimelinePlan plan = TimelinePlan.Build(
            [Scene("A"), Scene("C")],
            [@"C:\in\A.mp4", @"C:\in\B.mp4", @"C:\in\C.mp4"],
            skipUnscriptedVideos: true);

        Assert.Equal(["A", "C"], plan.Entries.Select(entry => entry.Name));
    }

    /// <remarks>
    /// A sibling axis file is a funscript of that scene like any other, so a video with only
    /// those - no main document - is scripted and stays.
    /// </remarks>
    [Fact]
    public void A_video_with_only_axis_scripts_is_not_skipped() {
        TimelinePlan plan = TimelinePlan.Build(
            [new SceneScripts("A", null, [@"C:\in\A.twist.funscript"])],
            [@"C:\in\A.mp4"],
            skipUnscriptedVideos: true);

        Assert.Equal(["A"], plan.Entries.Select(entry => entry.Name));
        Assert.Empty(plan.SkippedVideos);
    }

    [Fact]
    public void Nothing_is_skipped_unless_the_job_asks() {
        TimelinePlan plan = TimelinePlan.Build([Scene("A")], [@"C:\in\A.mp4", @"C:\in\B.mp4"]);

        Assert.Equal(["A", "B"], plan.Entries.Select(entry => entry.Name));
        Assert.Empty(plan.SkippedVideos);
    }

    [Fact]
    public async Task A_skipped_video_is_named_with_the_reason_and_the_way_back() {
        var logger = new FakeJobLogger();

        await TimelinePlan
            .Build([Scene("A")], [@"C:\in\A.mp4", @"C:\in\B.mp4"], skipUnscriptedVideos: true)
            .ReportAsync(logger);

        Assert.True(logger.WarnedAbout("Skipping video 'B'"));
        Assert.True(logger.WarnedAbout("no funscript of that name"));
        Assert.True(logger.WarnedAbout("Skip videos with no funscript"));

        // The other message would be a lie about a video that is not on the timeline at all.
        Assert.False(logger.WarnedAbout("plays unscripted"));
    }

    [Fact]
    public async Task Every_unpaired_file_is_named_and_told_what_happens_to_it() {
        using var workspace = new TempWorkspace();
        string scriptPath = workspace.WriteScript("B.funscript", ScriptBuilder.Basic((0, 0), (1400, 100)));

        var logger = new FakeJobLogger();

        await TimelinePlan
            .Build([new SceneScripts("B", scriptPath, [])], [@"C:\in\A.mp4"])
            .ReportAsync(logger);

        Assert.True(logger.WarnedAbout("No funscript found for video 'A'"));
        Assert.True(logger.WarnedAbout("plays unscripted"));
        Assert.True(logger.WarnedAbout("No video found for funscript 'B'"));
        Assert.True(logger.WarnedAbout("1400ms ahead of the video"));
    }

    [Fact]
    public async Task A_plan_where_everything_paired_up_says_nothing() {
        var logger = new FakeJobLogger();

        await TimelinePlan.Build([Scene("A")], [@"C:\in\A.mp4"]).ReportAsync(logger);

        Assert.Empty(logger.Warnings);
    }

    private static SceneScripts Scene(string name) =>
        new(name, $@"C:\in\{name}.funscript", []);
}
