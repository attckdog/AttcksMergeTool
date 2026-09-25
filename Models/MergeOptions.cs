using AttcksMergeTool.Services;

namespace AttcksMergeTool.Models;

/// <summary>
/// Everything a merge run needs to know. Gathered from the UI once when the job
/// starts, then treated as read-only by the services so nothing reaches back into
/// the form from a worker thread.
/// </summary>
/// <remarks>
/// The user-facing, mutable half of this lives in <see cref="AppSettings"/>;
/// <see cref="FromSettings"/> is the single place the two are mapped. Every default here
/// still matches what the code did before any of it was configurable.
/// </remarks>
public sealed class MergeOptions
{
    public const string DefaultOutputName = "MergedScript";
    public const string DefaultInputFolder = "Input";
    public const string DefaultTempFolder = "TempTS";
    public const string DefaultConcatListFile = "filelist.txt";
    public const string DefaultChapterMetadataFile = "ffmetadata.txt";

    private readonly string _inputFolder = ResolvePath(DefaultInputFolder);
    private readonly string _tempFolder = ResolvePath(DefaultTempFolder);
    private readonly string _concatListFile = ResolvePath(DefaultConcatListFile);
    private readonly string _chapterMetadataFile = ResolvePath(DefaultChapterMetadataFile);
    private readonly string _outputFolder = AppContext.BaseDirectory;
    private readonly string _ffmpegPath = AppSettings.DefaultFfmpegPath;
    private readonly string _ffprobePath = AppSettings.DefaultFfprobePath;

    /// <summary>Base filename for the merged outputs, without extension.</summary>
    public string OutputName { get; init; } = DefaultOutputName;

    public bool UseNvenc { get; init; } = true;
    public bool UseAv1 { get; init; } = true;

    // All five are stored resolved, so nothing downstream has to care whether the value it
    // was handed was relative. See ResolvePath for why the working directory is not used.
    public string InputFolder {
        get => _inputFolder;
        init => _inputFolder = ResolvePath(value);
    }

    public string TempFolder {
        get => _tempFolder;
        init => _tempFolder = ResolvePath(value);
    }

    public string ConcatListFile {
        get => _concatListFile;
        init => _concatListFile = ResolvePath(value);
    }

    public string ChapterMetadataFile {
        get => _chapterMetadataFile;
        init => _chapterMetadataFile = ResolvePath(value);
    }

    /// <summary>Where the merged video and script are written. Blank means beside the executable.</summary>
    public string OutputFolder {
        get => _outputFolder;
        init => _outputFolder = string.IsNullOrWhiteSpace(value) ? AppContext.BaseDirectory : ResolvePath(value);
    }

    /// <summary>The ffmpeg executable to launch, and likewise <see cref="FfprobePath"/>.</summary>
    /// <remarks>
    /// Resolved only when it looks like a path. A bare "ffmpeg" has to stay bare, because that
    /// is what makes <see cref="System.Diagnostics.ProcessStartInfo"/> search PATH for it -
    /// resolving it would pin the lookup to the app folder, where there is no ffmpeg.
    /// </remarks>
    public string FfmpegPath {
        get => _ffmpegPath;
        init => _ffmpegPath = ResolveExecutable(value, AppSettings.DefaultFfmpegPath);
    }

    public string FfprobePath {
        get => _ffprobePath;
        init => _ffprobePath = ResolveExecutable(value, AppSettings.DefaultFfprobePath);
    }

    /// <summary>How many ffmpeg encodes run at once.</summary>
    public int MaxParallelEncodes { get; init; } = 3;

    /// <summary>ffmpeg scale/pad target, in "width:height" form.</summary>
    public string TargetResolution { get; init; } = AppSettings.DefaultTargetResolution;

    public int TargetFps { get; init; } = 60;

    /// <summary>Constant-quality value for the AV1 encoders; lower is better quality.</summary>
    public int Av1Quality { get; init; } = 30;

    /// <summary>Constant-quality value for the H.264 encoders; lower is better quality.</summary>
    public int H264Quality { get; init; } = 23;

    /// <summary>NVENC preset, shared by both hardware encoders.</summary>
    public string NvencPreset { get; init; } = "p4";

    /// <summary>libsvtav1 preset.</summary>
    public string Av1SoftwarePreset { get; init; } = "8";

    /// <summary>libx264 preset.</summary>
    public string X264Preset { get; init; } = "fast";

    public string AudioBitrate { get; init; } = "192k";
    public int AudioChannels { get; init; } = 2;
    public int AudioSampleRate { get; init; } = 48000;

    /// <summary>
    /// Leave a video out of the merge when no funscript shares its name, rather than keeping
    /// it on the timeline as an unscripted stretch.
    /// </summary>
    /// <remarks>
    /// Off here, where the default is what the code did before the option existed, and on in
    /// <see cref="AppSettings.SkipVideosWithoutScripts"/>, which is what the window starts from.
    /// </remarks>
    public bool SkipVideosWithoutScripts { get; init; }

    /// <summary>Which file extensions count as input videos.</summary>
    public IReadOnlyList<string> VideoExtensions { get; init; } = MediaFileScanner.VideoExtensions;

    /// <summary>
    /// Scan every folder under <see cref="InputFolder"/>, not just its top level.
    /// </summary>
    /// <remarks>
    /// Off here, where the default is what the code did before the option existed. Use
    /// <see cref="InputScan"/> rather than this flag to do any scanning: it also carries the
    /// folders that have to stay out of a recursive walk.
    /// </remarks>
    public bool IncludeInputSubfolders { get; init; }

    /// <summary>
    /// How this job walks its input folder, with the folders it writes into kept out of the
    /// walk - both are resolved paths by now, so a temp or output folder sitting inside the
    /// input tree is recognised as one and skipped.
    /// </summary>
    public InputScan InputScan => new(IncludeInputSubfolders, [TempFolder, OutputFolder]);

    /// <summary>
    /// Insert a stretch of black between scenes, long enough for every axis to reach the
    /// position the next scene opens at without exceeding <see cref="MaxAxisSpeed"/>.
    /// </summary>
    /// <remarks>
    /// On here as well as in <see cref="AppSettings"/>, unlike the rest of this file: a merge
    /// that joins scenes with no gap at all is what the smoothing exists to stop, so it is not
    /// a default worth preserving. <see cref="Services.TransitionGap"/> sizes each one.
    /// </remarks>
    public bool InsertTransitionGaps { get; init; } = true;

    /// <summary>
    /// Ceiling on axis travel across a scene seam, in position units per second. It is what
    /// sizes the black gap: the slower the limit, the longer the gap has to be.
    /// </summary>
    public int MaxAxisSpeed { get; init; } = 100;

    public string OutputScriptPath => Path.Combine(OutputFolder, OutputName + ".funscript");
    public string OutputVideoPath => Path.Combine(OutputFolder, OutputName + ".mp4");

    /// <summary>
    /// The frozen job snapshot for <paramref name="settings"/>. The one place the user-facing
    /// settings and the job's view of them are mapped.
    /// </summary>
    public static MergeOptions FromSettings(AppSettings settings) => new() {
        OutputName = NormalizeOutputName(settings.OutputName),
        InputFolder = settings.InputFolder,
        TempFolder = settings.TempFolder,
        OutputFolder = settings.OutputFolder,
        ConcatListFile = settings.ConcatListFile,
        ChapterMetadataFile = settings.ChapterMetadataFile,
        FfmpegPath = settings.FfmpegPath,
        FfprobePath = settings.FfprobePath,
        UseNvenc = settings.UseNvenc,
        UseAv1 = settings.UseAv1,
        MaxParallelEncodes = settings.MaxParallelEncodes,
        TargetResolution = settings.TargetResolution,
        TargetFps = settings.TargetFps,
        Av1Quality = settings.Av1Quality,
        H264Quality = settings.H264Quality,
        NvencPreset = settings.NvencPreset,
        Av1SoftwarePreset = settings.Av1SoftwarePreset,
        X264Preset = settings.X264Preset,
        AudioBitrate = settings.AudioBitrate,
        AudioChannels = settings.AudioChannels,
        AudioSampleRate = settings.AudioSampleRate,
        VideoExtensions = [.. settings.VideoExtensions],
        IncludeInputSubfolders = settings.IncludeInputSubfolders,
        InsertTransitionGaps = settings.InsertTransitionGaps,
        MaxAxisSpeed = settings.MaxAxisSpeed,
        SkipVideosWithoutScripts = settings.SkipVideosWithoutScripts
    };

    /// <summary>Falls back to <see cref="DefaultOutputName"/> for blank input.</summary>
    public static string NormalizeOutputName(string? rawName) =>
        string.IsNullOrWhiteSpace(rawName) ? DefaultOutputName : rawName.Trim();

    /// <summary>
    /// Makes a relative path absolute against the executable's own folder rather than the
    /// process working directory. Launching from a shortcut with a different "Start in"
    /// would otherwise read and write a completely different set of folders.
    /// </summary>
    public static string ResolvePath(string path) => Path.GetFullPath(path, AppContext.BaseDirectory);

    /// <summary>
    /// Leaves a bare command name alone so PATH is searched, and resolves anything that
    /// carries a directory so a relative tool path still means "next to the app".
    /// </summary>
    private static string ResolveExecutable(string? value, string fallback) {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        string trimmed = value.Trim();

        return trimmed.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) < 0
            ? trimmed
            : ResolvePath(trimmed);
    }
}
