using AttcksMergeTool.Models;
using AttcksMergeTool.Services;

namespace AttcksMergeTool.Tests;

public class VoicePlannerTests
{
    private static readonly VoiceInjection Gaps2To8 = new() { Folders = ["x"], MinGapSeconds = 2, MaxGapSeconds = 8 };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Clips_never_overlap_and_are_separated_by_a_gap_in_range(int seed) {
        IReadOnlyList<VoicePlacement> placements = await Plan(Pool(20, 3000), 120_000, Gaps2To8, seed);

        Assert.NotEmpty(placements);
        Assert.InRange(placements[0].StartMs, 2000, 8000);

        for (int index = 1; index < placements.Count; index++) {
            Assert.InRange(placements[index].StartMs - placements[index - 1].EndMs, 2000, 8000);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task No_clip_runs_past_the_end_of_the_scene(int seed) {
        IReadOnlyList<VoicePlacement> placements = await Plan(Pool(10, 7000), 30_000, Gaps2To8, seed);

        Assert.All(placements, placement => Assert.True(placement.EndMs <= 30_000));
    }

    [Fact]
    public async Task Nothing_repeats_until_the_whole_pool_has_played() {
        var settings = new VoiceInjection { Folders = ["x"], MinGapSeconds = 0, MaxGapSeconds = 0 };

        IReadOnlyList<VoicePlacement> placements = await Plan(Pool(5, 1000), 10_000, settings, seed: 7);

        Assert.Equal(5, placements.Take(5).Select(placement => placement.Path).Distinct().Count());
        Assert.Equal(5, placements.Skip(5).Take(5).Select(placement => placement.Path).Distinct().Count());
        Assert.NotEqual(placements[4].Path, placements[5].Path);
    }

    [Fact]
    public async Task An_empty_pool_places_nothing() {
        Assert.Empty(await Plan(new Dictionary<string, int?>(), 60_000, Gaps2To8, seed: 1));
    }

    [Fact]
    public async Task Clips_that_cannot_be_measured_are_skipped() {
        var pool = new Dictionary<string, int?> { ["broken.mp3"] = null, ["good.mp3"] = 1000 };

        IReadOnlyList<VoicePlacement> placements = await Plan(pool, 60_000, Gaps2To8, seed: 1);

        Assert.NotEmpty(placements);
        Assert.All(placements, placement => Assert.Equal("good.mp3", placement.Path));
    }

    [Fact]
    public async Task A_pool_of_clips_longer_than_the_scene_places_nothing() {
        Assert.Empty(await Plan(Pool(3, 90_000), 30_000, Gaps2To8, seed: 1));
    }

    private static Dictionary<string, int?> Pool(int count, int durationMs) =>
        Enumerable.Range(1, count).ToDictionary(index => $"clip{index}.mp3", _ => (int?)durationMs);

    private static Task<IReadOnlyList<VoicePlacement>> Plan(
        Dictionary<string, int?> pool, int windowMs, VoiceInjection settings, int seed) =>
        VoicePlanner.PlanAsync(
            [.. pool.Keys],
            windowMs,
            settings,
            new Random(seed),
            (path, _) => Task.FromResult(pool[path]));
}
