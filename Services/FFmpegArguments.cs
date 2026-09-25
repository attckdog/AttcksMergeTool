using System.Globalization;

using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Builds the ffmpeg command lines for the two video phases: per-segment transcode
/// and final concatenation. Kept separate from <see cref="VideoMerger"/> so the
/// encoder matrix can be read (and changed) without wading through job orchestration.
/// </summary>
/// <remarks>
/// Every element is one argument. <see cref="ProcessRunner.RunAsync"/> passes them through
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, so paths must not be
/// quoted here - the runtime escapes them, and hand-quoting would make the quotes literal.
/// </remarks>
public static class FFmpegArguments
{
    /// <summary>Container the intermediate per-video segments are written to.</summary>
    public static string TempSegmentExtension(bool useAv1) => useAv1 ? ".mkv" : ".ts";

    private static string TempSegmentFormat(bool useAv1) => useAv1 ? "matroska" : "mpegts";

    /// <summary>Transcodes one source video into a normalized segment ready for concat.</summary>
    public static List<string> BuildEncode(
        string inputPath,
        string segmentPath,
        VideoSegmentSettings? trim,
        MergeOptions options) {
        Encoder encoder = EncoderFor(options);

        // Options that must precede -i (seeking, hardware decode).
        var inputArgs = new List<string>();

        if (trim is { UseTrim: true }) {
            inputArgs.AddRange(["-ss", trim.StartTime.ToString(CultureInfo.InvariantCulture)]);
            if (trim.EndTime > trim.StartTime) {
                inputArgs.AddRange(["-to", trim.EndTime.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        inputArgs.AddRange(encoder.HardwareDecodeArgs);

        // Letterbox rather than crop, so every segment shares one frame size.
        string videoFilter =
            $"scale={options.TargetResolution}:force_original_aspect_ratio=decrease," +
            $"pad={options.TargetResolution}:(ow-iw)/2:(oh-ih)/2";

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        args.AddRange(inputArgs);
        args.AddRange(["-i", inputPath]);
        args.AddRange(encoder.VideoArgs);
        args.AddRange(["-vf", videoFilter]);

        // The concat offsets each segment by its longest stream, so a source whose audio runs
        // short of its video leaves a hole in the merged audio track. Players close that hole
        // by playing the next scene's audio early, and the error compounds scene after scene.
        // Padding with silence from the first frame on, then cutting at the video's end, makes
        // every segment's audio exactly as long as its video. -shortest has to come before the
        // output path, like every output option.
        args.Add("-shortest");
        args.AddRange(OutputArgs(encoder, segmentPath, options, "aresample=async=1:first_pts=0,apad"));

        return args;
    }

    /// <summary>
    /// Generates <paramref name="durationMs"/> of black with silent audio, as a segment
    /// interchangeable with the ones <see cref="BuildEncode"/> produces.
    /// </summary>
    /// <remarks>
    /// This is the transition between two scenes: the time the device needs to travel from
    /// where one ended to where the next opens, which the merged script fills with a single
    /// keyframe per axis. It has to come out byte-compatible with the real segments - same
    /// encoder, frame rate, frame size, audio layout and container - because the concat that
    /// joins them all is a stream copy and would refuse a segment that differed.
    /// <para>
    /// The one thing deliberately left off is <c>-hwaccel</c>. It is a decode hint, and there
    /// is nothing here to decode; the frames come from lavfi. The encoder itself is still
    /// whichever one the options select, hardware included.
    /// </para>
    /// </remarks>
    public static List<string> BuildBlackSegment(string segmentPath, int durationMs, MergeOptions options) {
        Encoder encoder = EncoderFor(options);

        // color= wants WxH, where scale= wants W:H. One stored form, two spellings.
        string frameSize = options.TargetResolution.Replace(':', 'x');
        string seconds = (durationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };

        args.AddRange([
            "-f", "lavfi",
            "-i", $"color=c=black:s={frameSize}:r={Number(options.TargetFps)}",
            "-f", "lavfi",
            "-i", $"anullsrc=channel_layout={Number(options.AudioChannels)}c:"
                + $"sample_rate={Number(options.AudioSampleRate)}"
        ]);

        // Both sources are infinite, so the length has to be imposed on the output. -t rather
        // than -shortest, which would have nothing to be shorter than.
        args.AddRange(["-t", seconds]);

        args.AddRange(encoder.VideoArgs);
        args.AddRange(OutputArgs(encoder, segmentPath, options, "aresample=async=1"));

        return args;
    }

    /// <summary>
    /// Everything after the video filter, which every segment shares: frame rate, normalized
    /// audio, the bitstream filter the container needs, and the container itself.
    /// </summary>
    /// <remarks>
    /// Mismatched sample rates or channel counts break concat, so the audio is normalized even
    /// where it carries nothing.
    /// </remarks>
    private static List<string> OutputArgs(
        Encoder encoder,
        string segmentPath,
        MergeOptions options,
        string audioFilter) {
        var args = new List<string> {
            "-r", Number(options.TargetFps),
            "-c:a", "aac",
            "-b:a", options.AudioBitrate,
            "-ac", Number(options.AudioChannels),
            "-ar", Number(options.AudioSampleRate),
            "-af", audioFilter
        };

        args.AddRange(encoder.BitstreamFilterArgs);
        args.AddRange(["-f", TempSegmentFormat(options.UseAv1), "-muxdelay", "0", "-y", segmentPath]);

        return args;
    }

    /// <summary>
    /// The encoder the options select, and the arguments that travel with it.
    /// </summary>
    /// <param name="HardwareDecodeArgs">
    /// Pre-input options. Only meaningful for a real file - a generated source has nothing to
    /// decode - so this is the one part <see cref="BuildBlackSegment"/> leaves out.
    /// </param>
    private readonly record struct Encoder(
        IReadOnlyList<string> HardwareDecodeArgs,
        IReadOnlyList<string> VideoArgs,
        IReadOnlyList<string> BitstreamFilterArgs);

    /// <summary>
    /// The AV1/H.264 x hardware/software matrix, in one place so both segment kinds are
    /// guaranteed to be encoded the same way.
    /// </summary>
    private static Encoder EncoderFor(MergeOptions options) {
        // The quality numbers and presets are user-configurable; AppSettings.Normalize is what
        // keeps them inside the range the encoders accept.
        string av1Quality = Number(options.Av1Quality);
        string h264Quality = Number(options.H264Quality);

        if (options.UseAv1) {
            return options.UseNvenc
                ? new Encoder(
                    ["-hwaccel", "cuda"],
                    ["-c:v", "av1_nvenc", "-rc", "vbr", "-cq", av1Quality, "-preset", options.NvencPreset],
                    [])
                : new Encoder(
                    [],
                    ["-c:v", "libsvtav1", "-crf", av1Quality, "-preset", options.Av1SoftwarePreset],
                    []);
        }

        // MPEG-TS segments need Annex B framing to survive stream-copy concatenation.
        string[] annexB = ["-bsf:v", "h264_mp4toannexb"];

        return options.UseNvenc
            ? new Encoder(
                ["-hwaccel", "cuda"],
                ["-c:v", "h264_nvenc", "-rc", "vbr", "-cq", h264Quality, "-preset", options.NvencPreset],
                annexB)
            : new Encoder(
                [],
                ["-c:v", "libx264", "-crf", h264Quality, "-preset", options.X264Preset],
                annexB);
    }

    /// <summary>
    /// Stream-copies the segments listed in <paramref name="concatListPath"/> into the
    /// final video, attaching chapter markers when a metadata file is available.
    /// </summary>
    public static List<string> BuildConcat(
        string concatListPath,
        string outputVideoPath,
        string? chapterMetadataPath,
        MergeOptions options) {
        var args = new List<string> { "-hide_banner", "-f", "concat", "-safe", "0", "-i", concatListPath };

        if (chapterMetadataPath is not null) {
            args.AddRange(["-i", chapterMetadataPath, "-map_metadata", "1"]);
        }

        args.AddRange(["-c", "copy", "-movflags", "+faststart"]);

        // ADTS-to-ASC only applies to the MPEG-TS path; AV1 segments are already in MKV.
        // This has to precede the output: ffmpeg binds each option to the file that follows
        // it, so anything trailing the output path is read as belonging to a further output
        // that never arrives, and is ignored with only a warning.
        if (!options.UseAv1) {
            args.AddRange(["-bsf:a", "aac_adtstoasc"]);
        }

        args.AddRange(["-y", outputVideoPath]);

        return args;
    }

    /// <summary>
    /// Invariant formatting for every numeric argument. A locale using a different digit set
    /// or separator would otherwise produce a command line ffmpeg cannot parse.
    /// </summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
