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

    /// <remarks>
    /// A segment whose audio is shorter than its video leaves a hole in the concatenated
    /// audio, which players close by playing later audio early - drift that grows with every
    /// scene. Padding and then cutting at the shortest stream keeps each segment's two
    /// streams the same length.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void The_encode_pads_audio_to_the_video_length(bool useAv1, bool useNvenc) {
        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, Trim(1, 5), Options(useAv1, useNvenc));

        int filter = args.IndexOf("-filter_complex");
        Assert.Contains("apad", args[filter + 1]);
        Assert.Contains("first_pts=0", args[filter + 1]);
        Assert.InRange(args.IndexOf("-shortest"), 0, args.IndexOf(Segment) - 1);
    }

    /// <remarks>
    /// ffmpeg 8 hangs forever on aresample=async + apad in a standalone -af graph when the
    /// source's audio has timestamp gaps. The same filters inside one -filter_complex graph
    /// finish, so the encode must never fall back to separate -vf/-af chains.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void The_encode_filters_audio_and_video_in_one_graph(bool useAv1, bool useNvenc) {
        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, Trim(1, 5), Options(useAv1, useNvenc));

        Assert.DoesNotContain("-af", args);
        Assert.DoesNotContain("-vf", args);

        string graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.StartsWith("[0:v]scale=", graph);
        Assert.Contains("[0:a]aresample=", graph);
        Assert.Contains("[v]", args);
        Assert.Contains("[a]", args);
    }

    /// <remarks>
    /// The concat stream-copies, so a segment without audio would break it. A mute source gets
    /// generated silence in the output's layout, cut at the video's end like padded audio.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void A_source_without_audio_gets_a_silent_track(bool useAv1, bool useNvenc) {
        List<string> args = FFmpegArguments.BuildEncode(
            Input, Segment, Trim(1, 5), Options(useAv1, useNvenc), hasAudio: false);

        string graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.DoesNotContain("[0:a]", graph);
        Assert.Contains("anullsrc=channel_layout=2c:sample_rate=48000[a]", graph);
        Assert.Contains("[a]", args);
        Assert.InRange(args.IndexOf("-shortest"), 0, args.IndexOf(Segment) - 1);
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

    /// <remarks>
    /// The generated black is stream-copied into the output alongside the real segments, so
    /// anything about it that differs - encoder, container, frame rate, audio layout - would
    /// make the concat refuse it or produce a broken file.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void A_black_segment_is_encoded_exactly_like_a_real_one(bool useAv1, bool useNvenc) {
        MergeOptions options = Options(useAv1, useNvenc);

        List<string> encode = FFmpegArguments.BuildEncode(Input, Segment, trim: null, options);
        List<string> black = FFmpegArguments.BuildBlackSegment(Segment, 800, options);

        Assert.Equal(encode[encode.IndexOf("-c:v") + 1], black[black.IndexOf("-c:v") + 1]);
        Assert.Equal(encode[encode.IndexOf("-r") + 1], black[black.IndexOf("-r") + 1]);
        Assert.Equal(encode[encode.IndexOf("-f") + 1], black[black.LastIndexOf("-f") + 1]);
        Assert.Equal(encode.Contains("h264_mp4toannexb"), black.Contains("h264_mp4toannexb"));
        Assert.Equal(encode[encode.IndexOf("-c:a") + 1], black[black.IndexOf("-c:a") + 1]);
    }

    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void The_black_segment_command_ends_at_its_output(bool useAv1, bool useNvenc) {
        List<string> args = FFmpegArguments.BuildBlackSegment(Segment, 800, Options(useAv1, useNvenc));

        Assert.Equal(Segment, args[^1]);
        Assert.Single(args, argument => argument == Segment);
    }

    /// <remarks>
    /// Both lavfi sources run forever, so the length has to be imposed on the output or ffmpeg
    /// never stops.
    /// </remarks>
    [Fact]
    public void The_black_segment_is_generated_from_lavfi_and_bounded_by_its_duration() {
        List<string> args = FFmpegArguments.BuildBlackSegment(Segment, 800, Options(true, true));

        Assert.Equal(2, args.Count(argument => argument == "lavfi"));
        Assert.Contains(args, argument => argument.StartsWith("color=c=black", StringComparison.Ordinal));
        Assert.Contains(args, argument => argument.StartsWith("anullsrc=", StringComparison.Ordinal));
        Assert.Equal("0.8", args[args.IndexOf("-t") + 1]);
    }

    /// <remarks>
    /// scale= wants W:H and color= wants WxH. One stored setting, two spellings - and handing
    /// color= the colon form makes ffmpeg reject the whole filter.
    /// </remarks>
    [Fact]
    public void The_black_frame_size_is_written_the_way_the_color_source_wants_it() {
        var options = new MergeOptions { TargetResolution = "3840:2160", TargetFps = 30 };

        List<string> args = FFmpegArguments.BuildBlackSegment(Segment, 500, options);

        Assert.Contains("color=c=black:s=3840x2160:r=30", args);
        Assert.DoesNotContain(args, argument => argument.Contains("s=3840:2160", StringComparison.Ordinal));
    }

    [Fact]
    public void The_configured_audio_layout_reaches_the_black_segment() {
        var options = new MergeOptions { AudioChannels = 6, AudioSampleRate = 44100, AudioBitrate = "320k" };

        List<string> args = FFmpegArguments.BuildBlackSegment(Segment, 500, options);

        Assert.Contains("anullsrc=channel_layout=6c:sample_rate=44100", args);
        Assert.Equal("6", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("44100", args[args.IndexOf("-ar") + 1]);
        Assert.Equal("320k", args[args.IndexOf("-b:a") + 1]);
    }

    /// <remarks>
    /// -hwaccel is a decode hint and there is nothing here to decode; the frames come from
    /// lavfi. The encoder itself is still whichever one the options select.
    /// </remarks>
    [Fact]
    public void A_black_segment_asks_for_no_hardware_decode() {
        List<string> args = FFmpegArguments.BuildBlackSegment(Segment, 800, Options(true, true));

        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal("av1_nvenc", args[args.IndexOf("-c:v") + 1]);
    }

    [Fact]
    public void A_black_segment_hand_quotes_nothing_either() {
        List<string> args = FFmpegArguments.BuildBlackSegment(Segment, 800, Options(true, true));

        Assert.All(args, argument => Assert.False(argument.StartsWith('"') || argument.EndsWith('"')));
    }

    /// <remarks>
    /// A locale using a comma for the decimal point would otherwise produce a duration ffmpeg
    /// cannot parse.
    /// </remarks>
    [Fact]
    public void The_black_duration_is_written_with_an_invariant_decimal_point() {
        List<string> args = FFmpegArguments.BuildBlackSegment(Segment, 1250, Options(true, true));

        Assert.Equal("1.25", args[args.IndexOf("-t") + 1]);
    }

    [Fact]
    public void An_encode_without_voice_is_unchanged_by_the_voice_parameter() {
        MergeOptions options = Options(useAv1: true, useNvenc: true);

        Assert.Equal(
            FFmpegArguments.BuildEncode(Input, Segment, Trim(1, 5), options),
            FFmpegArguments.BuildEncode(Input, Segment, Trim(1, 5), options, voice: new VoiceMix([], 100, 100)));
    }

    /// <remarks>
    /// ffmpeg binds options to the file after them. A voice input placed after the encoder
    /// arguments would take the video codec as its own decoder and the encode would fail.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EncoderMatrix))]
    public void Voice_tracks_are_inputs_straight_after_the_source(bool useAv1, bool useNvenc) {
        var voice = new VoiceMix([@"C:\temp dir\0001_voice1.wav", @"C:\temp dir\0001_voice2.wav"], 100, 60);

        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, Trim(1, 5), Options(useAv1, useNvenc), voice: voice);

        int source = args.IndexOf(Input);
        Assert.Equal("-i", args[source + 1]);
        Assert.Equal(voice.TrackPaths[0], args[source + 2]);
        Assert.Equal("-i", args[source + 3]);
        Assert.Equal(voice.TrackPaths[1], args[source + 4]);
        Assert.Equal("-c:v", args[source + 5]);
    }

    [Fact]
    public void Voice_tracks_are_mixed_over_the_scene_audio_at_their_volumes() {
        var voice = new VoiceMix([@"C:\v1.wav", @"C:\v2.wav"], 150, 60);

        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, null, Options(true, true), voice: voice);
        string graph = args[args.IndexOf("-filter_complex") + 1];

        Assert.Contains("[0:a]aresample=async=1:first_pts=0,apad,volume=0.6[base]", graph);
        Assert.Contains("[1:a]volume=1.5[voice0]", graph);
        Assert.Contains("[2:a]volume=1.5[voice1]", graph);
        Assert.Contains("[base][voice0][voice1]amix=inputs=3:duration=first:normalize=0", graph);
        Assert.EndsWith("[a]", graph);
        Assert.Contains("-shortest", args);
    }

    [Fact]
    public void Voice_is_mixed_over_silence_for_a_source_without_audio() {
        var voice = new VoiceMix([@"C:\v1.wav"], 100, 100);

        List<string> args = FFmpegArguments.BuildEncode(Input, Segment, null, Options(true, true), hasAudio: false, voice: voice);
        string graph = args[args.IndexOf("-filter_complex") + 1];

        Assert.DoesNotContain("[0:a]", graph);
        Assert.Contains("anullsrc", graph);
        Assert.Contains("amix=inputs=2", graph);
    }

    [Fact]
    public void A_voice_track_delays_each_clip_to_its_place() {
        VoicePlacement[] placements = [new(@"C:\a b\one.mp3", 2500, 1000), new(@"C:\a b\two.mp3", 9000, 2000)];

        List<string> args = FFmpegArguments.BuildVoiceTrack(placements, @"C:\temp\voice.wav", Options(true, true));
        string graph = args[args.IndexOf("-filter_complex") + 1];

        Assert.Equal(placements[0].Path, args[args.IndexOf("-i") + 1]);
        Assert.Contains("[0:a]asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,adelay=delays=2500:all=1[clip0]", graph);
        Assert.Contains("adelay=delays=9000:all=1[clip1]", graph);
        Assert.Contains("[clip0][clip1]amix=inputs=2:duration=longest:normalize=0[out]", graph);
        Assert.Equal(@"C:\temp\voice.wav", args[^1]);
    }

    [Fact]
    public void A_voice_track_takes_a_bounded_number_of_clips() {
        VoicePlacement[] tooMany = [.. Enumerable.Range(0, FFmpegArguments.MaxClipsPerVoiceTrack + 1)
            .Select(index => new VoicePlacement($"{index}.mp3", index * 1000, 500))];

        Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegArguments.BuildVoiceTrack(tooMany, "v.wav", Options(true, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FFmpegArguments.BuildVoiceTrack([], "v.wav", Options(true, true)));
    }

    private static MergeOptions Options(bool useAv1, bool useNvenc) =>
        new() { UseAv1 = useAv1, UseNvenc = useNvenc };

    private static VideoSegmentSettings Trim(double start, double end) =>
        new() { FilePath = Input, StartTime = start, EndTime = end, UseTrim = true };
}
