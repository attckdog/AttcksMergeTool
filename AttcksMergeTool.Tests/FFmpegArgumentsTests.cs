using AttcksMergeTool.Models;
using AttcksMergeTool.Services;

namespace AttcksMergeTool.Tests;

public class FFmpegArgumentsTests
{
    private const string Input = @"C:\in put\Scene One.mp4";
    private const string Segment = @"C:\temp dir\0001.mkv";
    private const string Output = @"C:\out put\Merged Script.mp4";
    private const string ConcatList = @"C:\out put\file list.txt";
    private const string ChapterFile = @"C:\out put\ffmetadata.txt";

    public static TheoryData<bool, bool> EncoderMatrix => new() {
        { true, true }, { true, false }, { false, true }, { false, false }
    };

    /// <remarks>
    /// ffmpeg binds every option to the file that follows it, so anything after the output
    /// path belongs to an output that never arrives and is silently ignored. This is the
    /// regression guard for that class of bug.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void The_encode_command_ends_at_its_output(bool useAv1, bool useNvenc) {
        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, Trim(1, 5), Options(useAv1, useNvenc));

        Assert.Equal(Segment, args[^1]);
        Assert.Single(args, argument => argument == Segment);
    }

    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void The_concat_command_ends_at_its_output(bool useAv1, bool useNvenc) {
        List<string> args = FFmpegArguments.BuildConcat(ConcatList, Output, ChapterFile, Options(useAv1, useNvenc));

        Assert.Equal(Output, args[^1]);
    }

    /// <remarks>
    /// Arguments go through <c>ProcessStartInfo.ArgumentList</c>, which escapes them. Quoting
    /// here as well would make the quotes part of the path.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void No_argument_is_hand_quoted(bool useAv1, bool useNvenc) {
        MergeOptions options = Options(useAv1, useNvenc);

        List<string> args = [
            .. FFmpegArguments.BuildEncode(Input, Segment, Trim(1, 5), options),
            .. FFmpegArguments.BuildConcat(ConcatList, Output, ChapterFile, options)
        ];

        Assert.All(args, argument => Assert.False(argument.StartsWith('"') || argument.EndsWith('"')));
    }

    [Fact]
    public void Paths_containing_spaces_are_passed_through_untouched() {
        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, trim: null, Options(true, true));

        Assert.Contains(Input, args);
        Assert.Contains(Segment, args);
    }

    [Fact]
    public void A_trim_seeks_before_the_input_so_ffmpeg_can_skip_rather_than_decode() {
        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, Trim(2.5, 7.5), Options(true, false));

        int seek = args.IndexOf("-ss");
        int input = args.IndexOf("-i");

        Assert.InRange(seek, 0, input - 1);
        Assert.Equal("2.5", args[seek + 1]);
    }

    [Fact]
    public void An_end_past_the_start_becomes_a_to_bound() {
        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, Trim(2, 8), Options(true, false));

        Assert.Contains("-to", args);
        Assert.Equal("8", args[args.IndexOf("-to") + 1]);
    }

    [Fact]
    public void An_end_that_is_not_past_the_start_is_left_off_entirely() {
        Assert.DoesNotContain("-to", FFmpegArguments.BuildEncode(Input, Segment, Trim(5, 0), Options(true, false)));
        Assert.DoesNotContain("-to", FFmpegArguments.BuildEncode(Input, Segment, Trim(5, 2), Options(true, false)));
    }

    [Fact]
    public void A_trim_that_is_switched_off_contributes_nothing() {
        var settings = new VideoSegmentSettings { FilePath = Input, StartTime = 3, EndTime = 9, UseTrim = false };

        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, settings, Options(true, false));

        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-to", args);
    }

    [Fact]
    public void Trim_timestamps_are_written_with_an_invariant_decimal_point() {
        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, Trim(1.25, 3.5), Options(true, false));

        Assert.Equal("1.25", args[args.IndexOf("-ss") + 1]);
    }

    /// <remarks>
    /// The MPEG-TS path needs the bitstream filter to turn ADTS audio back into ASC on the way
    /// into mp4; the AV1 path is already in MKV and does not.
    /// </remarks>
    [Fact]
    public void The_adts_filter_is_applied_on_the_h264_path_only_and_before_the_output() {
        List<string> h264 = FFmpegArguments.BuildConcat(ConcatList, Output, ChapterFile, Options(false, true));
        List<string> av1 = FFmpegArguments.BuildConcat(ConcatList, Output, ChapterFile, Options(true, true));

        Assert.Contains("aac_adtstoasc", h264);
        Assert.InRange(h264.IndexOf("-bsf:a"), 0, h264.Count - 2);
        Assert.DoesNotContain("aac_adtstoasc", av1);
    }

    [Fact]
    public void Chapters_are_mapped_in_only_when_a_metadata_file_exists() {
        List<string> with = FFmpegArguments.BuildConcat(ConcatList, Output, ChapterFile, Options(true, true));
        List<string> without = FFmpegArguments.BuildConcat(ConcatList, Output, null, Options(true, true));

        Assert.Contains("-map_metadata", with);
        Assert.Contains(ChapterFile, with);
        Assert.DoesNotContain("-map_metadata", without);
    }

    [Fact]
    public void The_segment_container_matches_the_codec() {
        Assert.Equal(".mkv", FFmpegArguments.TempSegmentExtension(useAv1: true));
        Assert.Equal(".ts", FFmpegArguments.TempSegmentExtension(useAv1: false));
    }

    /// <summary>
    /// The quality, preset and audio values are configurable, so the encode command has to be
    /// built from them rather than from the constants they used to be.
    /// </summary>
    [Theory]
    [InlineData(true, true, "av1_nvenc", "-cq", "18", "p7")]
    [InlineData(true, false, "libsvtav1", "-crf", "18", "4")]
    [InlineData(false, true, "h264_nvenc", "-cq", "12", "p7")]
    [InlineData(false, false, "libx264", "-crf", "12", "slow")]
    public void The_configured_quality_and_preset_reach_the_encode(
        bool useAv1,
        bool useNvenc,
        string encoder,
        string qualityFlag,
        string quality,
        string preset) {
        var options = new MergeOptions {
            UseAv1 = useAv1,
            UseNvenc = useNvenc,
            Av1Quality = 18,
            H264Quality = 12,
            NvencPreset = "p7",
            Av1SoftwarePreset = "4",
            X264Preset = "slow"
        };

        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, trim: null, options);

        Assert.Equal(encoder, args[args.IndexOf("-c:v") + 1]);
        Assert.Equal(quality, args[args.IndexOf(qualityFlag) + 1]);
        Assert.Equal(preset, args[args.IndexOf("-preset") + 1]);
    }

    /// <remarks>
    /// Only one preset reaches the command line. A build that emitted both would hand ffmpeg
    /// a preset its selected encoder does not recognise.
    /// </remarks>
    [Fact]
    public void Only_the_selected_encoders_preset_is_used() {
        var options = new MergeOptions {
            UseAv1 = true,
            UseNvenc = true,
            NvencPreset = "p7",
            Av1SoftwarePreset = "4",
            X264Preset = "slow"
        };

        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, trim: null, options);

        Assert.Single(args, argument => argument == "-preset");
        Assert.DoesNotContain("slow", args);
    }

    [Fact]
    public void The_configured_audio_and_frame_rate_reach_the_encode() {
        var options = new MergeOptions {
            TargetFps = 30,
            TargetResolution = "3840:2160",
            AudioBitrate = "320k",
            AudioChannels = 6,
            AudioSampleRate = 44100
        };

        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, trim: null, options);

        Assert.Equal("30", args[args.IndexOf("-r") + 1]);
        Assert.Equal("320k", args[args.IndexOf("-b:a") + 1]);
        Assert.Equal("6", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("44100", args[args.IndexOf("-ar") + 1]);
        Assert.Contains(args, argument => argument.Contains("scale=3840:2160", StringComparison.Ordinal));
    }

    private static MergeOptions Options(bool useAv1, bool useNvenc) =>
        new() { UseAv1 = useAv1, UseNvenc = useNvenc };

    private static VideoSegmentSettings Trim(double start, double end) =>
        new() { FilePath = Input, StartTime = start, EndTime = end, UseTrim = true };
}
