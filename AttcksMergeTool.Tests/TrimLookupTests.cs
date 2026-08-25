using AttcksMergeTool.Models;
using AttcksMergeTool.Services;

namespace AttcksMergeTool.Tests;

public class TrimLookupTests
{
    /// <remarks>
    /// The two mergers reach the same setting from different directions - the script pass has
    /// only a scene name, the video pass has a full path - so both have to resolve to the
    /// same entry. They used to match on different keys and could disagree.
    /// </remarks>
    [Fact]
    public void A_scene_name_and_a_file_path_find_the_same_entry() {
        var lookup = new TrimLookup([Settings(@"C:\in\Scene.mp4", 1, 5)]);

        Assert.Same(lookup.For("Scene"), lookup.ForFile(@"C:\in\Scene.mp4"));
        Assert.NotNull(lookup.For("Scene"));
    }

    [Fact]
    public void Lookup_ignores_case() {
        var lookup = new TrimLookup([Settings(@"C:\in\Scene.mp4", 1, 5)]);

        Assert.NotNull(lookup.For("SCENE"));
    }

    [Fact]
    public void An_unknown_scene_has_no_settings_and_no_trim() {
        var lookup = new TrimLookup([Settings(@"C:\in\Scene.mp4", 1, 5)]);

        Assert.Null(lookup.For("Missing"));
        Assert.Equal(TrimWindow.None, lookup.WindowFor("Missing"));
    }

    [Fact]
    public void Trim_settings_become_a_millisecond_window() {
        var lookup = new TrimLookup([Settings(@"C:\in\Scene.mp4", 2, 8)]);

        Assert.Equal(new TrimWindow(2000, 8000), lookup.WindowFor("Scene"));
    }

    [Fact]
    public void Settings_with_trimming_switched_off_produce_no_window() {
        var settings = Settings(@"C:\in\Scene.mp4", 2, 8);
        settings.UseTrim = false;

        Assert.Equal(TrimWindow.None, new TrimLookup([settings]).WindowFor("Scene"));
    }

    /// <remarks>
    /// Two videos sharing a base name are one scene as far as the script merge is concerned,
    /// and it can only honour one trim - so the choice is made once, here, rather than
    /// differently in each merger.
    /// </remarks>
    [Fact]
    public void The_first_of_two_videos_sharing_a_base_name_wins() {
        var lookup = new TrimLookup([
            Settings(@"C:\in\Scene.mp4", 1, 5),
            Settings(@"C:\in\Scene.mkv", 20, 50)
        ]);

        Assert.Equal(new TrimWindow(1000, 5000), lookup.WindowFor("Scene"));
    }

    [Fact]
    public void The_empty_lookup_finds_nothing() {
        Assert.Null(TrimLookup.Empty.For("Scene"));
        Assert.Equal(TrimWindow.None, TrimLookup.Empty.WindowFor("Scene"));
    }

    private static VideoSegmentSettings Settings(string path, double start, double end) =>
        new() { FilePath = path, StartTime = start, EndTime = end, UseTrim = true };
}
