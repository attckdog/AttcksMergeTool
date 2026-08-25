using AttcksMergeTool.Models;

namespace AttcksMergeTool.Tests;

/// <summary>
/// Covers the seam between the settings the user edits and the frozen snapshot a job reads.
/// </summary>
public class MergeOptionsTests
{
    [Fact]
    public void Every_setting_reaches_the_job_snapshot() {
        var settings = new AppSettings {
            OutputName = "  Merged  ",
            UseNvenc = false,
            UseAv1 = false,
            MaxParallelEncodes = 5,
            TargetResolution = "3840:2160",
            TargetFps = 30,
            Av1Quality = 22,
            H264Quality = 18,
            NvencPreset = "p7",
            Av1SoftwarePreset = "4",
            X264Preset = "slow",
            AudioBitrate = "320k",
            AudioChannels = 6,
            AudioSampleRate = 44100,
            TransitionMs = 250,
            VideoExtensions = [".mp4", ".mkv"]
        };

        MergeOptions options = MergeOptions.FromSettings(settings);

        Assert.Equal("Merged", options.OutputName);
        Assert.False(options.UseNvenc);
        Assert.False(options.UseAv1);
        Assert.Equal(5, options.MaxParallelEncodes);
        Assert.Equal("3840:2160", options.TargetResolution);
        Assert.Equal(30, options.TargetFps);
        Assert.Equal(22, options.Av1Quality);
        Assert.Equal(18, options.H264Quality);
        Assert.Equal("p7", options.NvencPreset);
        Assert.Equal("4", options.Av1SoftwarePreset);
        Assert.Equal("slow", options.X264Preset);
        Assert.Equal("320k", options.AudioBitrate);
        Assert.Equal(6, options.AudioChannels);
        Assert.Equal(44100, options.AudioSampleRate);
        Assert.Equal(250, options.TransitionMs);
        Assert.Equal([".mp4", ".mkv"], options.VideoExtensions);
    }

    /// <summary>
    /// The defaults are what every existing test and every existing run depends on, so they
    /// have to survive a trip through settings that were never touched.
    /// </summary>
    [Fact]
    public void Untouched_settings_produce_the_same_snapshot_as_the_defaults() {
        MergeOptions fromSettings = MergeOptions.FromSettings(new AppSettings());
        var direct = new MergeOptions();

        Assert.Equal(direct.InputFolder, fromSettings.InputFolder);
        Assert.Equal(direct.TempFolder, fromSettings.TempFolder);
        Assert.Equal(direct.OutputFolder, fromSettings.OutputFolder);
        Assert.Equal(direct.ConcatListFile, fromSettings.ConcatListFile);
        Assert.Equal(direct.ChapterMetadataFile, fromSettings.ChapterMetadataFile);
        Assert.Equal(direct.OutputVideoPath, fromSettings.OutputVideoPath);
        Assert.Equal(direct.FfmpegPath, fromSettings.FfmpegPath);
        Assert.Equal(direct.TargetResolution, fromSettings.TargetResolution);
        Assert.Equal(direct.Av1Quality, fromSettings.Av1Quality);
        Assert.Equal(direct.AudioBitrate, fromSettings.AudioBitrate);
    }

    /// <summary>
    /// A bare command name has to stay bare. Resolving it would pin the lookup to the app
    /// folder, where there is no ffmpeg, and break every machine that relies on PATH.
    /// </summary>
    [Fact]
    public void A_bare_tool_name_is_left_alone_so_PATH_is_searched() {
        var options = new MergeOptions { FfmpegPath = "ffmpeg", FfprobePath = " ffprobe " };

        Assert.Equal("ffmpeg", options.FfmpegPath);
        Assert.Equal("ffprobe", options.FfprobePath);
    }

    [Fact]
    public void An_absolute_tool_path_is_kept_as_given() {
        var options = new MergeOptions { FfmpegPath = @"C:\tools\ffmpeg\bin\ffmpeg.exe" };

        Assert.Equal(@"C:\tools\ffmpeg\bin\ffmpeg.exe", options.FfmpegPath);
    }

    /// <summary>A tool shipped beside the app is found whatever the working directory is.</summary>
    [Fact]
    public void A_relative_tool_path_resolves_against_the_application_folder() {
        var options = new MergeOptions { FfmpegPath = @"tools\ffmpeg.exe" };

        Assert.Equal(MergeOptions.ResolvePath(@"tools\ffmpeg.exe"), options.FfmpegPath);
        Assert.True(Path.IsPathRooted(options.FfmpegPath));
    }

    [Fact]
    public void A_blank_tool_path_falls_back_to_the_bare_name() {
        var options = new MergeOptions { FfmpegPath = "   ", FfprobePath = "" };

        Assert.Equal("ffmpeg", options.FfmpegPath);
        Assert.Equal("ffprobe", options.FfprobePath);
    }

    [Fact]
    public void The_output_folder_decides_where_both_outputs_land() {
        var options = new MergeOptions { OutputName = "Merged", OutputFolder = @"D:\Merged" };

        Assert.Equal(@"D:\Merged\Merged.mp4", options.OutputVideoPath);
        Assert.Equal(@"D:\Merged\Merged.funscript", options.OutputScriptPath);
    }

    /// <summary>Blank is the documented way to ask for "beside the application".</summary>
    [Fact]
    public void A_blank_output_folder_means_beside_the_application() {
        var options = new MergeOptions { OutputName = "Merged", OutputFolder = "  " };

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Merged.mp4"), options.OutputVideoPath);
    }
}
