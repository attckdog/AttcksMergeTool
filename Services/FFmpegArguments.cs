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
        // Options that must precede -i (seeking, hardware decode).
        var inputArgs = new List<string>();
        var encoderArgs = new List<string>();
        var bitstreamFilterArgs = new List<string>();

        if (trim is { UseTrim: true }) {
            inputArgs.AddRange(["-ss", trim.StartTime.ToString(CultureInfo.InvariantCulture)]);
            if (trim.EndTime > trim.StartTime) {
                inputArgs.AddRange(["-to", trim.EndTime.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        // The quality numbers and presets are user-configurable; AppSettings.Normalize is what
        // keeps them inside the range the encoders accept.
        string av1Quality = Number(options.Av1Quality);
        string h264Quality = Number(options.H264Quality);

        if (options.UseAv1) {
            if (options.UseNvenc) {
                inputArgs.AddRange(["-hwaccel", "cuda"]);
                encoderArgs.AddRange([
                    "-c:v", "av1_nvenc", "-rc", "vbr", "-cq", av1Quality, "-preset", options.NvencPreset
                ]);
            } else {
                encoderArgs.AddRange([
                    "-c:v", "libsvtav1", "-crf", av1Quality, "-preset", options.Av1SoftwarePreset
                ]);
            }
        } else {
            // MPEG-TS segments need Annex B framing to survive stream-copy concatenation.
            bitstreamFilterArgs.AddRange(["-bsf:v", "h264_mp4toannexb"]);

            if (options.UseNvenc) {
                inputArgs.AddRange(["-hwaccel", "cuda"]);
                encoderArgs.AddRange([
                    "-c:v", "h264_nvenc", "-rc", "vbr", "-cq", h264Quality, "-preset", options.NvencPreset
                ]);
            } else {
                encoderArgs.AddRange([
                    "-c:v", "libx264", "-crf", h264Quality, "-preset", options.X264Preset
                ]);
            }
        }

        // Letterbox rather than crop, so every segment shares one frame size.
        string videoFilter =
            $"scale={options.TargetResolution}:force_original_aspect_ratio=decrease," +
            $"pad={options.TargetResolution}:(ow-iw)/2:(oh-ih)/2";

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        args.AddRange(inputArgs);
        args.AddRange(["-i", inputPath]);
        args.AddRange(encoderArgs);
        args.AddRange([
            "-vf", videoFilter,
            "-r", Number(options.TargetFps),
            // Normalize audio too - mismatched sample rates or channel counts break concat.
            "-c:a", "aac",
            "-b:a", options.AudioBitrate,
            "-ac", Number(options.AudioChannels),
            "-ar", Number(options.AudioSampleRate),
            "-af", "aresample=async=1"
        ]);
        args.AddRange(bitstreamFilterArgs);
        args.AddRange(["-f", TempSegmentFormat(options.UseAv1), "-muxdelay", "0", "-y", segmentPath]);

        return args;
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
