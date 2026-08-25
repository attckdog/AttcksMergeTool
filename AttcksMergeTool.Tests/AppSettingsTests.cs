using AttcksMergeTool.Models;

namespace AttcksMergeTool.Tests;

/// <summary>
/// Covers <see cref="AppSettings.Normalize"/>, which is the only thing standing between a
/// hand-edited settings file and an ffmpeg command line built from nonsense.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Out_of_range_numbers_are_pulled_back_into_range() {
        var settings = new AppSettings {
            MaxParallelEncodes = 999,
            TargetFps = 0,
            Av1Quality = -5,
            H264Quality = 500,
            TransitionMs = -1,
            AudioChannels = 64,
            LogFontSize = 400F
        };

        settings.Normalize();

        Assert.Equal(16, settings.MaxParallelEncodes);
        Assert.Equal(1, settings.TargetFps);
        Assert.Equal(0, settings.Av1Quality);
        Assert.Equal(63, settings.H264Quality);
        Assert.Equal(0, settings.TransitionMs);
        Assert.Equal(8, settings.AudioChannels);
        Assert.Equal(24F, settings.LogFontSize);
    }

    /// <remarks>
    /// Only the floor is checked here: the ceiling depends on the window the panel is sitting
    /// in, so the layout clamps against that when it applies the width.
    /// </remarks>
    /// <remarks>
    /// The one default in this file that is not what the code did before it was configurable.
    /// A video nothing scripts is usually in the folder by accident, so it is left out unless
    /// the user says otherwise.
    /// </remarks>
    [Fact]
    public void Videos_with_no_funscript_are_skipped_by_default() {
        var settings = new AppSettings();

        settings.Normalize();

        Assert.True(settings.SkipVideosWithoutScripts);
    }

    [Fact]
    public void A_side_panel_narrower_than_the_minimum_falls_back_to_the_default() {
        var settings = new AppSettings { SidePanelWidth = 10 };

        settings.Normalize();

        Assert.Equal(AppSettings.DefaultSidePanelWidth, settings.SidePanelWidth);
    }

    [Fact]
    public void A_usable_side_panel_width_is_kept() {
        var settings = new AppSettings { SidePanelWidth = 480 };

        settings.Normalize();

        Assert.Equal(480, settings.SidePanelWidth);
    }

    [Fact]
    public void Blanked_out_strings_fall_back_to_their_defaults() {
        var settings = new AppSettings {
            InputFolder = "   ",
            TempFolder = "",
            ConcatListFile = " ",
            FfmpegPath = "",
            FfprobePath = "  ",
            AudioBitrate = "",
            X264Preset = " ",
            OutputName = "  "
        };

        settings.Normalize();

        var defaults = new AppSettings();

        Assert.Equal(defaults.InputFolder, settings.InputFolder);
        Assert.Equal(defaults.TempFolder, settings.TempFolder);
        Assert.Equal(defaults.ConcatListFile, settings.ConcatListFile);
        Assert.Equal("ffmpeg", settings.FfmpegPath);
        Assert.Equal("ffprobe", settings.FfprobePath);
        Assert.Equal("192k", settings.AudioBitrate);
        Assert.Equal("fast", settings.X264Preset);
        Assert.Equal(MergeOptions.DefaultOutputName, settings.OutputName);
    }

    /// <summary>
    /// A blank output folder is the documented way to say "beside the application", so it is
    /// the one string that must survive being empty.
    /// </summary>
    [Fact]
    public void A_blank_output_folder_stays_blank() {
        var settings = new AppSettings { OutputFolder = "  " };

        settings.Normalize();

        Assert.Equal(string.Empty, settings.OutputFolder);
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("1920x1080")]
    [InlineData("1920:")]
    [InlineData("")]
    public void An_unusable_target_resolution_reverts_to_the_default(string resolution) {
        var settings = new AppSettings { TargetResolution = resolution };

        settings.Normalize();

        Assert.Equal(AppSettings.DefaultTargetResolution, settings.TargetResolution);
    }

    [Fact]
    public void A_well_formed_target_resolution_is_left_alone() {
        var settings = new AppSettings { TargetResolution = "3840:2160" };

        settings.Normalize();

        Assert.Equal("3840:2160", settings.TargetResolution);
    }

    /// <summary>
    /// The scanner compares against lowercase, dot-prefixed extensions, so anything a user
    /// might reasonably type has to be reshaped into that before it reaches the comparison.
    /// </summary>
    [Fact]
    public void Video_extensions_are_lowercased_dot_prefixed_and_deduplicated() {
        var settings = new AppSettings { VideoExtensions = ["MP4", ".MkV", " mp4 ", "", "  "] };

        settings.Normalize();

        Assert.Equal([".mp4", ".mkv"], settings.VideoExtensions);
    }

    [Fact]
    public void An_empty_video_extension_list_reverts_to_the_defaults() {
        var settings = new AppSettings { VideoExtensions = [] };

        settings.Normalize();

        Assert.Equal(new AppSettings().VideoExtensions, settings.VideoExtensions);
    }

    /// <summary>
    /// The options dialog edits a clone so that Cancel can discard it; that only works if the
    /// list inside is copied too.
    /// </summary>
    [Fact]
    public void A_clone_does_not_share_its_extension_list() {
        var settings = new AppSettings();

        AppSettings clone = settings.Clone();
        clone.VideoExtensions.Add(".xyz");

        Assert.DoesNotContain(".xyz", settings.VideoExtensions);
    }
}
