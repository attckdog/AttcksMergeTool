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
    /// <param name="hasAudio">
    /// False for a source with no audio stream. Its segment gets a silent track instead, since
    /// the concat needs every segment to carry the same streams.
    /// </param>
    /// <param name="voice">
    /// Voice tracks from <see cref="BuildVoiceTrack"/> to mix over the scene's own audio, or
    /// <c>null</c> to leave it as it is.
    /// </param>
    public static List<string> BuildEncode(
        string inputPath,
        string segmentPath,
        VideoSegmentSettings? trim,
        MergeOptions options,
        bool hasAudio = true,
        VoiceMix? voice = null) {
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

        // Straight after the source: ffmpeg binds options to the file that follows them, so a
        // voice input placed after the encoder arguments would take them as its own.
        foreach (string track in voice?.TrackPaths ?? []) args.AddRange(["-i", track]);

        args.AddRange(encoder.VideoArgs);

        // The concat offsets each segment by its longest stream, so a source whose audio runs
        // short of its video leaves a hole in the merged audio track. Players close that hole
        // by playing the next scene's audio early, and the error compounds scene after scene.
        // Padding with silence from the first frame on, then cutting at the video's end, makes
        // every segment's audio exactly as long as its video. -shortest has to come before the
        // output path, like every output option.
        //
        // Both chains share one -filter_complex graph rather than going in as -vf and -af.
        // ffmpeg 8 deadlocks on aresample=async + apad in a separate audio graph whenever the
        // source's audio has timestamp gaps (looped clips have one at every seam): the encode
        // stops partway through at 0% CPU and never exits. The same filters in one graph run
        // to completion.
        //
        // A source with no audio gets silence generated in the same graph; it is endless, so
        // -shortest cuts it at the video's end just like the padding.
        string audioChain = hasAudio
            ? "[0:a]aresample=async=1:first_pts=0,apad"
            : Silence(options);

        audioChain = voice is { TrackPaths.Count: > 0 }
            ? MixVoice(audioChain, voice)
            : audioChain + "[a]";

        args.AddRange([
            "-filter_complex",
            $"[0:v]{videoFilter}[v];{audioChain}",
            "-map", "[v]",
            "-map", "[a]",
            "-shortest"
        ]);
        args.AddRange(OutputArgs(encoder, segmentPath, options, audioFilter: null));

        return args;
    }

    /// <summary>
    /// Mixes the voice tracks over <paramref name="baseChain"/>, the scene's own padded audio.
    /// </summary>
    /// <remarks>
    /// The base is endless - padded or generated - so mixing for as long as it lasts leaves
    /// -shortest to cut the result at the video's end exactly as it does without voices. The
    /// tracks hold silence wherever no clip plays, so nothing is normalized: a voice must not
    /// get quieter just because it shares the mix. The limiter only catches the peaks where a
    /// loud clip lands on loud scene audio, and has its own auto-levelling switched off so it
    /// never lifts anything, and its lookahead compensated so the audio stays on its frames.
    /// </remarks>
    private static string MixVoice(string baseChain, VoiceMix voice) {
        var graph = new List<string> { $"{baseChain},volume={Volume(voice.OriginalVolumePercent)}[base]" };
        string inputs = "[base]";

        for (int index = 0; index < voice.TrackPaths.Count; index++) {
            graph.Add($"[{index + 1}:a]volume={Volume(voice.VoiceVolumePercent)}[voice{index}]");
            inputs += $"[voice{index}]";
        }

        graph.Add(
            $"{inputs}amix=inputs={Number(voice.TrackPaths.Count + 1)}:duration=first:normalize=0,"
            + "alimiter=limit=0.97:level=0:latency=1[a]");

        return string.Join(';', graph);
    }

    /// <summary>
    /// How many clips one voice track mixes at most. Each is an input on the command line,
    /// which Windows caps at 32k characters; voice packs nest deep, and their paths are long.
    /// </summary>
    public const int MaxClipsPerVoiceTrack = 32;

    /// <summary>
    /// Renders <paramref name="placements"/> to one WAV track that starts at the scene's first
    /// frame, each clip delayed to its place and silence everywhere else.
    /// </summary>
    /// <remarks>
    /// Rendered ahead of the scene's encode rather than inside it so the encode takes a fixed
    /// handful of inputs however many clips a long scene ends up with. Every clip is brought
    /// to the output's layout first, since a pack mixes mono and stereo, 44.1 and 48 kHz.
    /// </remarks>
    public static List<string> BuildVoiceTrack(
        IReadOnlyList<VoicePlacement> placements,
        string outputPath,
        MergeOptions options) {
        if (placements.Count is 0 or > MaxClipsPerVoiceTrack) {
            throw new ArgumentOutOfRangeException(
                nameof(placements), placements.Count, $"A voice track takes 1 to {MaxClipsPerVoiceTrack} clips.");
        }

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };

        foreach (VoicePlacement placement in placements) args.AddRange(["-i", placement.Path]);

        string format =
            $"aformat=sample_fmts=fltp:sample_rates={Number(options.AudioSampleRate)}:"
            + $"channel_layouts={ChannelLayout(options.AudioChannels)}";

        var graph = new List<string>();
        string inputs = string.Empty;

        for (int index = 0; index < placements.Count; index++) {
            graph.Add(
                $"[{index}:a]asetpts=PTS-STARTPTS,{format},"
                + $"adelay=delays={Number(placements[index].StartMs)}:all=1[clip{index}]");
            inputs += $"[clip{index}]";
        }

        // Clips never overlap, so there is nothing to normalize: each plays at its own level.
        graph.Add(placements.Count == 1
            ? $"{inputs}anull[out]"
            : $"{inputs}amix=inputs={Number(placements.Count)}:duration=longest:normalize=0[out]");

        args.AddRange([
            "-filter_complex", string.Join(';', graph),
            "-map", "[out]",
            "-c:a", "pcm_s16le",
            "-ac", Number(options.AudioChannels),
            "-ar", Number(options.AudioSampleRate),
            "-f", "wav",
            "-y", outputPath
        ]);

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
            "-i", Silence(options)
        ]);

        // Both sources are infinite, so the length has to be imposed on the output. -t rather
        // than -shortest, which would have nothing to be shorter than.
        args.AddRange(["-t", seconds]);

        args.AddRange(encoder.VideoArgs);
        args.AddRange(OutputArgs(encoder, segmentPath, options, "aresample=async=1"));

        return args;
    }

    /// <summary>Endless silence in the output's audio layout.</summary>
    private static string Silence(MergeOptions options) =>
        $"anullsrc=channel_layout={Number(options.AudioChannels)}c:"
        + $"sample_rate={Number(options.AudioSampleRate)}";

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
        string? audioFilter) {
        var args = new List<string> {
            "-r", Number(options.TargetFps),
            "-c:a", "aac",
            "-b:a", options.AudioBitrate,
            "-ac", Number(options.AudioChannels),
            "-ar", Number(options.AudioSampleRate)
        };

        // Null when the caller already filtered the audio inside a -filter_complex graph.
        if (audioFilter is not null) args.AddRange(["-af", audioFilter]);

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

    /// <summary>A volume filter gain for a percentage, so 60 becomes 0.6.</summary>
    private static string Volume(int percent) => (percent / 100.0).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// The named layout for a channel count. A bare count ("2c") has no channel order, which
    /// would not negotiate against the named layouts the decoders hand out.
    /// </summary>
    private static string ChannelLayout(int channels) => channels switch {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{Number(channels)}c"
    };
}
