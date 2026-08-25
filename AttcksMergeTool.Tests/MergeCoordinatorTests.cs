using System.Text.Json;

using AttcksMergeTool.Models;
using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

public class MergeCoordinatorTests
{
    [Fact]
    public async Task A_missing_encoder_stops_the_job_before_anything_happens() {
        using var workspace = new TempWorkspace();
        var logger = new FakeJobLogger();
        var runner = new FakeProcessRunner();
        runner.MissingCommands.Add("ffmpeg");

        bool ran = await Run(workspace, nameof(A_missing_encoder_stops_the_job_before_anything_happens), logger, runner);

        Assert.False(ran);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task An_empty_input_folder_is_reported_rather_than_called_a_success() {
        using var workspace = new TempWorkspace();
        var logger = new FakeJobLogger();

        bool ran = await Run(workspace, nameof(An_empty_input_folder_is_reported_rather_than_called_a_success), logger);

        Assert.False(ran);
        Assert.True(logger.WarnedAbout("Nothing to merge"));
    }

    /// <remarks>
    /// It keeps its place on the timeline and plays unscripted, which is what keeps everything
    /// after it in sync - but the user still needs to know a scene of theirs has no script.
    /// </remarks>
    [Fact]
    public async Task An_unpaired_video_is_reported_before_the_merge_starts() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteVideo("A.mp4");
        workspace.WriteVideo("B.mp4");

        var logger = new FakeJobLogger();
        var probe = new FakeMediaProbe { DefaultDurationMs = 2000 };

        Assert.True(await Run(workspace, nameof(An_unpaired_video_is_reported_before_the_merge_starts), logger, probe: probe));

        Assert.True(logger.WarnedAbout("No funscript found for video 'B'"));
        Assert.True(logger.WarnedAbout("plays unscripted"));
    }

    /// <remarks>
    /// Reported with what including it would have cost, because that number is the difference
    /// between a warning the user can act on and one they cannot.
    /// </remarks>
    [Fact]
    public async Task An_unpaired_script_is_reported_with_what_skipping_it_saved() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteScript("B.funscript", ScriptBuilder.Basic((0, 0), (1400, 100)));
        workspace.WriteVideo("A.mp4");

        var logger = new FakeJobLogger();
        var probe = new FakeMediaProbe { DefaultDurationMs = 2000 };

        Assert.True(await Run(workspace, nameof(An_unpaired_script_is_reported_with_what_skipping_it_saved), logger, probe: probe));

        Assert.True(logger.WarnedAbout("No video found for funscript 'B'"));
        Assert.True(logger.WarnedAbout("1400ms ahead of the video"));
    }

    /// <remarks>
    /// The script has to be merged before the encode so a failed encode still leaves one, which
    /// means its offsets start out as the source videos' durations. This is the correction that
    /// stops the difference between a source and its segment accumulating across the merge.
    /// </remarks>
    [Fact]
    public async Task The_merged_script_is_retimed_onto_the_measured_segments() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteScript("B.funscript", ScriptBuilder.Basic((0, 0), (500, 50)));
        workspace.WriteVideo("A.mp4");
        workspace.WriteVideo("B.mp4");

        // The sources say 2000/5000; the segments they encode to are 48ms and 120ms longer.
        var probe = new FakeMediaProbe()
            .WithDuration("A.mp4", 2000).WithDuration("B.mp4", 5000)
            .WithDuration("0001.mkv", 2048).WithDuration("0002.mkv", 5120);

        var logger = new FakeJobLogger();
        MergeOptions options = workspace.Options(nameof(The_merged_script_is_retimed_onto_the_measured_segments));

        Assert.True(await Run(workspace, options, logger, probe: probe));

        Funscript merged = ReadScript(options);

        Assert.Equal([0, 2048], merged.Bookmarks!.Select(bookmark => bookmark.Time));
        Assert.Equal([0, 1000, 2048, 2548], merged.Actions!.Select(action => action.At));
        Assert.Equal(7168 / 1000, merged.Metadata!.Duration);
    }

    /// <remarks>
    /// A retime that cannot be trusted is not applied at all: the script written by step one is
    /// still correct to the sources, which beats one shifted onto boundaries that may be wrong.
    /// </remarks>
    [Fact]
    public async Task An_unmeasurable_segment_leaves_the_script_on_the_source_timings() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteScript("B.funscript", ScriptBuilder.Basic((0, 0), (500, 50)));
        workspace.WriteVideo("A.mp4");
        workspace.WriteVideo("B.mp4");

        var probe = new FakeMediaProbe()
            .WithDuration("A.mp4", 2000).WithDuration("B.mp4", 5000)
            .WithDuration("0001.mkv", 2048).WithDuration("0002.mkv", null);

        var logger = new FakeJobLogger();
        MergeOptions options = workspace.Options(nameof(An_unmeasurable_segment_leaves_the_script_on_the_source_timings));

        Assert.True(await Run(workspace, options, logger, probe: probe));

        Assert.True(logger.WarnedAbout("Could not retime the script"));
        Assert.Equal([0, 2000], ReadScript(options).Bookmarks!.Select(bookmark => bookmark.Time));
    }

    /// <remarks>
    /// The chapter file is written by one step and consumed by another, so a leftover from an
    /// earlier run would otherwise be picked up and applied to this one.
    /// </remarks>
    [Fact]
    public async Task A_stale_chapter_file_is_cleared_before_the_job_and_after_it() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        File.WriteAllText(workspace.Path("ffmetadata.txt"), ";FFMETADATA1\ntitle=from an older run\n");

        Assert.True(await Run(workspace, nameof(A_stale_chapter_file_is_cleared_before_the_job_and_after_it), new FakeJobLogger()));

        Assert.False(workspace.Exists("ffmetadata.txt"));
    }

    /// <remarks>
    /// The chapter file goes the same way as the temp segments: no run of any outcome leaves
    /// scratch behind, so the next run starts from a clean folder.
    /// </remarks>
    [Fact]
    public async Task A_failed_run_still_clears_its_chapter_file() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteVideo("A.mp4");

        var runner = new FakeProcessRunner {
            Respond = invocation => invocation.Arguments.Contains("concat")
                ? throw new ExternalToolException("ffmpeg", 1, "no such file")
                : string.Empty
        };

        var probe = new FakeMediaProbe { DefaultDurationMs = 2000 };

        await Assert.ThrowsAsync<ExternalToolException>(
            () => Run(workspace, nameof(A_failed_run_still_clears_its_chapter_file), new FakeJobLogger(), runner, probe));

        Assert.False(workspace.Exists("ffmetadata.txt"));
    }

    [Fact]
    public async Task A_script_only_run_writes_no_chapter_file_at_all() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));

        var runner = new FakeProcessRunner();

        Assert.True(await Run(workspace, nameof(A_script_only_run_writes_no_chapter_file_at_all), new FakeJobLogger(), runner));

        // Only the two PATH probes; nothing was encoded and nothing was concatenated.
        Assert.All(runner.Invocations, invocation => Assert.Contains("-version", invocation.Arguments));
        Assert.False(workspace.Exists("ffmetadata.txt"));
    }

    /// <remarks>
    /// The scan decides what exists, the snapshot decides where each one goes. Both halves of
    /// the merge follow the same reordered list, so the scripts move with their videos.
    /// </remarks>
    [Fact]
    public async Task The_videos_are_merged_in_the_order_the_snapshot_lists_them() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteScript("B.funscript", ScriptBuilder.Basic((0, 0), (500, 50)));
        workspace.WriteVideo("A.mp4");
        workspace.WriteVideo("B.mp4");

        var probe = new FakeMediaProbe()
            .WithDuration("A.mp4", 2000).WithDuration("B.mp4", 5000)
            .WithDuration("0001.mkv", 5000).WithDuration("0002.mkv", 2000);

        MergeOptions options = workspace.Options(nameof(The_videos_are_merged_in_the_order_the_snapshot_lists_them));

        // B first, which is the opposite of what the scan alone would produce.
        var runner = new FakeProcessRunner();
        var coordinator = new MergeCoordinator(
            new FakeJobLogger(),
            options,
            [Segment(workspace.Path("B.mp4")), Segment(workspace.Path("A.mp4"))],
            runner,
            probe);

        Assert.True(await coordinator.RunAsync());

        Funscript merged = ReadScript(options);

        Assert.Equal(["B", "A"], merged.Bookmarks!.Select(bookmark => bookmark.Name));
        Assert.Equal([0, 5000], merged.Bookmarks!.Select(bookmark => bookmark.Time));

        // The video walk followed the same list: B is what segment one was encoded from.
        Assert.EndsWith("B.mp4", EncodedSourceOf(runner, "0001.mkv"));
    }

    /// <remarks>
    /// A video added to the folder since the window last refreshed is in the merge - the scan
    /// is what says it exists - but it cannot push a scene the user placed out of its position.
    /// </remarks>
    [Fact]
    public async Task A_video_the_snapshot_does_not_know_about_is_merged_last() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteScript("B.funscript", ScriptBuilder.Basic((0, 0), (500, 50)));
        workspace.WriteVideo("A.mp4");
        workspace.WriteVideo("B.mp4");

        var probe = new FakeMediaProbe { DefaultDurationMs = 2000 };
        MergeOptions options = workspace.Options(nameof(A_video_the_snapshot_does_not_know_about_is_merged_last));

        // Only B was on screen when the job started; A appeared in the folder afterwards.
        var coordinator = new MergeCoordinator(
            new FakeJobLogger(), options, [Segment(workspace.Path("B.mp4"))], new FakeProcessRunner(), probe);

        Assert.True(await coordinator.RunAsync());

        Assert.Equal(["B", "A"], ReadScript(options).Bookmarks!.Select(bookmark => bookmark.Name));
    }

    private static VideoSegmentSettings Segment(string path) => new() { FilePath = path };

    /// <summary>
    /// The video that <paramref name="segmentFile"/> was encoded from. Segments are numbered by
    /// concat position, so this reads the merge order back out without depending on the order
    /// the parallel encodes happened to run in.
    /// </summary>
    private static string EncodedSourceOf(FakeProcessRunner runner, string segmentFile) =>
        runner.Invocations
            .First(invocation => invocation.Arguments.Any(
                argument => argument.EndsWith(segmentFile, StringComparison.Ordinal)))
            .Arguments
            .SkipWhile(argument => argument != "-i")
            .Skip(1)
            .First();

    /// <remarks>
    /// Off the timeline means off the encode too: the two stages walk one plan, so a video the
    /// script merge never saw must not turn up in the video the merged script describes.
    /// </remarks>
    [Fact]
    public async Task A_video_with_no_script_is_not_encoded_when_skipping_is_on() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteVideo("A.mp4");
        workspace.WriteVideo("B.mp4");

        var logger = new FakeJobLogger();
        var runner = new FakeProcessRunner();
        var probe = new FakeMediaProbe { DefaultDurationMs = 2000 };

        MergeOptions options = workspace.Options(
            nameof(A_video_with_no_script_is_not_encoded_when_skipping_is_on), skipUnscriptedVideos: true);

        Assert.True(await Run(workspace, options, logger, runner, probe));

        Assert.True(logger.WarnedAbout("Skipping video 'B'"));
        Assert.False(logger.WarnedAbout("plays unscripted"));

        Assert.DoesNotContain(
            runner.Invocations,
            invocation => invocation.Arguments.Any(argument => argument.EndsWith("B.mp4", StringComparison.Ordinal)));

        Assert.Contains(
            runner.Invocations,
            invocation => invocation.Arguments.Any(argument => argument.EndsWith("A.mp4", StringComparison.Ordinal)));
    }

    /// <remarks>
    /// The merged script has to describe the video that was actually built, so a skipped video
    /// must not leave a chapter or a marker behind either.
    /// </remarks>
    [Fact]
    public async Task A_skipped_video_contributes_no_marker_to_the_merged_script() {
        using var workspace = new TempWorkspace();
        workspace.WriteScript("A.funscript", ScriptBuilder.Basic((0, 0), (1000, 100)));
        workspace.WriteVideo("A.mp4");
        workspace.WriteVideo("B.mp4");

        var probe = new FakeMediaProbe { DefaultDurationMs = 2000 };
        MergeOptions options = workspace.Options(
            nameof(A_skipped_video_contributes_no_marker_to_the_merged_script), skipUnscriptedVideos: true);

        Assert.True(await Run(workspace, options, new FakeJobLogger(), probe: probe));

        Assert.Equal(["A"], ReadScript(options).Bookmarks!.Select(bookmark => bookmark.Name));
    }

    /// <remarks>
    /// Skipping everything is not a merge that succeeded with no output; the reasons are logged
    /// first, then the run stops rather than writing an empty video over a real one.
    /// </remarks>
    [Fact]
    public async Task A_run_where_every_video_is_skipped_stops_instead_of_producing_nothing() {
        using var workspace = new TempWorkspace();
        workspace.WriteVideo("A.mp4");

        var logger = new FakeJobLogger();
        MergeOptions options = workspace.Options(
            nameof(A_run_where_every_video_is_skipped_stops_instead_of_producing_nothing),
            skipUnscriptedVideos: true);

        Assert.False(await Run(workspace, options, logger, probe: new FakeMediaProbe { DefaultDurationMs = 2000 }));

        Assert.True(logger.WarnedAbout("Skipping video 'A'"));
        Assert.True(logger.WarnedAbout("Nothing left to merge"));
        Assert.False(File.Exists(options.OutputScriptPath));
    }

    private static Task<bool> Run(
        TempWorkspace workspace,
        string outputName,
        FakeJobLogger logger,
        FakeProcessRunner? runner = null,
        FakeMediaProbe? probe = null) =>
        Run(workspace, workspace.Options(outputName), logger, runner, probe);

    private static Task<bool> Run(
        TempWorkspace workspace,
        MergeOptions options,
        FakeJobLogger logger,
        FakeProcessRunner? runner = null,
        FakeMediaProbe? probe = null) {
        var coordinator = new MergeCoordinator(
            logger,
            options,
            [],
            runner ?? new FakeProcessRunner(),
            probe ?? new FakeMediaProbe());

        return coordinator.RunAsync();
    }

    private static Funscript ReadScript(MergeOptions options) =>
        JsonSerializer.Deserialize<Funscript>(
            File.ReadAllText(options.OutputScriptPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}
