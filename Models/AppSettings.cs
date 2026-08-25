using System.Text.RegularExpressions;

using AttcksMergeTool.Services;

namespace AttcksMergeTool.Models;

/// <summary>
/// Everything the user can configure, as it is stored in <c>settings.json</c>. Mutable and
/// JSON round-trippable, which is what separates it from <see cref="MergeOptions"/> - that
/// one is the frozen, already-resolved snapshot a running job reads.
/// </summary>
/// <remarks>
/// Every default here is the value the code used before any of this was configurable, so a
/// missing or empty settings file reproduces the old behaviour exactly.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>Accepted form for <see cref="TargetResolution"/>, as ffmpeg's scale filter wants it.</summary>
    private static readonly Regex ResolutionPattern = new(@"^\d{1,5}:\d{1,5}$", RegexOptions.Compiled);

    public const string DefaultTargetResolution = "1920:1080";

    /// <summary>Starting width of the side panel, and the width it returns to if the stored one is unusable.</summary>
    public const int DefaultSidePanelWidth = 340;

    /// <summary>
    /// Narrowest the side panel may be dragged. Below this the trim inputs and the reorder
    /// buttons start clipping, so the splitter stops here rather than letting the panel
    /// be collapsed into something that cannot be used.
    /// </summary>
    public const int MinSidePanelWidth = 260;

    public const string DefaultFfmpegPath = "ffmpeg";
    public const string DefaultFfprobePath = "ffprobe";

    // --- Paths ---

    public string InputFolder { get; set; } = MergeOptions.DefaultInputFolder;
    public string TempFolder { get; set; } = MergeOptions.DefaultTempFolder;

    /// <summary>Where the merged video and script land. Empty means "beside the executable".</summary>
    public string OutputFolder { get; set; } = string.Empty;

    public string ConcatListFile { get; set; } = MergeOptions.DefaultConcatListFile;
    public string ChapterMetadataFile { get; set; } = MergeOptions.DefaultChapterMetadataFile;

    // --- External tools ---

    /// <summary>
    /// The ffmpeg executable. A bare name is looked up on PATH; anything containing a directory
    /// separator is taken as a path. Same for <see cref="FfprobePath"/>.
    /// </summary>
    public string FfmpegPath { get; set; } = DefaultFfmpegPath;

    public string FfprobePath { get; set; } = DefaultFfprobePath;

    // --- Encoding ---

    public bool UseNvenc { get; set; } = true;
    public bool UseAv1 { get; set; } = true;

    /// <summary>ffmpeg scale/pad target, in "width:height" form.</summary>
    public string TargetResolution { get; set; } = DefaultTargetResolution;

    public int TargetFps { get; set; } = 60;
    public int MaxParallelEncodes { get; set; } = 3;

    /// <summary>Constant-quality value for the AV1 encoders. Lower is better quality.</summary>
    public int Av1Quality { get; set; } = 30;

    /// <summary>Constant-quality value for the H.264 encoders. Lower is better quality.</summary>
    public int H264Quality { get; set; } = 23;

    /// <summary>NVENC preset, shared by both hardware encoders: p1 (fastest) to p7 (best).</summary>
    public string NvencPreset { get; set; } = "p4";

    /// <summary>libsvtav1 preset, 0 (slowest) to 13 (fastest).</summary>
    public string Av1SoftwarePreset { get; set; } = "8";

    /// <summary>libx264 preset, "ultrafast" to "veryslow".</summary>
    public string X264Preset { get; set; } = "fast";

    public string AudioBitrate { get; set; } = "192k";
    public int AudioChannels { get; set; } = 2;
    public int AudioSampleRate { get; set; } = 48000;

    // --- Merge and script ---

    /// <summary>Base filename for the merged outputs, without extension.</summary>
    public string OutputName { get; set; } = MergeOptions.DefaultOutputName;

    /// <summary>
    /// Window at the head of each scene over which keyframes are collapsed, so the device
    /// eases from the previous scene's final position instead of snapping.
    /// </summary>
    public int TransitionMs { get; set; } = 500;

    /// <summary>
    /// Leave a video out of the merge when no funscript shares its name.
    /// </summary>
    /// <remarks>
    /// On by default, unlike the rest of this file: merging a video nothing scripts was the
    /// old behaviour, but it puts a stretch of dead timeline in the middle of the output, and
    /// a folder that has one usually has it by accident.
    /// </remarks>
    public bool SkipVideosWithoutScripts { get; set; } = true;

    /// <summary>Which file extensions are treated as input videos.</summary>
    public List<string> VideoExtensions { get; set; } = [.. MediaFileScanner.VideoExtensions];

    // --- Application ---

    public bool RememberWindowBounds { get; set; } = true;

    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Width of the side panel, as the splitter was last left. Stored rather than the splitter
    /// distance itself, which only means anything alongside the window width it was measured at.
    /// </summary>
    public int SidePanelWidth { get; set; } = DefaultSidePanelWidth;

    /// <summary>Point size of the job log's monospaced font.</summary>
    public float LogFontSize { get; set; } = 10F;

    public bool RefreshInputOnLaunch { get; set; } = true;

    /// <summary>Ask before a run overwrites an existing merged video or script.</summary>
    public bool WarnBeforeOverwrite { get; set; } = true;

    /// <summary>
    /// A detached copy, so the options dialog can edit freely and Cancel can really cancel.
    /// </summary>
    public AppSettings Clone() {
        AppSettings copy = (AppSettings)MemberwiseClone();
        copy.VideoExtensions = [.. VideoExtensions];

        return copy;
    }

    /// <summary>
    /// Repairs anything out of range or unusable, in place. Run after every load and again on
    /// OK, so neither a hand-edited settings file nor a stray dialog value can reach ffmpeg.
    /// </summary>
    public void Normalize() {
        var defaults = new AppSettings();

        InputFolder = Fallback(InputFolder, defaults.InputFolder);
        TempFolder = Fallback(TempFolder, defaults.TempFolder);
        OutputFolder = (OutputFolder ?? string.Empty).Trim();
        ConcatListFile = Fallback(ConcatListFile, defaults.ConcatListFile);
        ChapterMetadataFile = Fallback(ChapterMetadataFile, defaults.ChapterMetadataFile);

        FfmpegPath = Fallback(FfmpegPath, defaults.FfmpegPath);
        FfprobePath = Fallback(FfprobePath, defaults.FfprobePath);

        if (!ResolutionPattern.IsMatch(Fallback(TargetResolution, string.Empty))) {
            TargetResolution = defaults.TargetResolution;
        }

        TargetFps = Math.Clamp(TargetFps, 1, 240);
        MaxParallelEncodes = Math.Clamp(MaxParallelEncodes, 1, 16);
        Av1Quality = Math.Clamp(Av1Quality, 0, 63);
        H264Quality = Math.Clamp(H264Quality, 0, 63);

        NvencPreset = Fallback(NvencPreset, defaults.NvencPreset);
        Av1SoftwarePreset = Fallback(Av1SoftwarePreset, defaults.Av1SoftwarePreset);
        X264Preset = Fallback(X264Preset, defaults.X264Preset);

        AudioBitrate = Fallback(AudioBitrate, defaults.AudioBitrate);
        AudioChannels = Math.Clamp(AudioChannels, 1, 8);
        AudioSampleRate = Math.Clamp(AudioSampleRate, 8000, 192000);

        OutputName = MergeOptions.NormalizeOutputName(OutputName);
        TransitionMs = Math.Clamp(TransitionMs, 0, 10000);

        VideoExtensions = NormalizeExtensions(VideoExtensions) is { Count: > 0 } extensions
            ? extensions
            : defaults.VideoExtensions;

        LogFontSize = Math.Clamp(LogFontSize, 6F, 24F);

        // Only the floor is enforced here; the ceiling depends on how wide the window turned
        // out to be, so the layout clamps against that when it applies the width.
        if (SidePanelWidth < MinSidePanelWidth) SidePanelWidth = defaults.SidePanelWidth;
    }

    /// <summary>
    /// Lowercased, dot-prefixed and de-duplicated, because that is the shape
    /// <see cref="MediaFileScanner.IsVideoFile"/> compares against.
    /// </summary>
    private static List<string> NormalizeExtensions(IEnumerable<string>? extensions) {
        if (extensions is null) return [];

        var seen = new List<string>();

        foreach (string raw in extensions) {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string extension = raw.Trim().ToLowerInvariant();
            if (!extension.StartsWith('.')) extension = "." + extension;

            if (extension.Length > 1 && !seen.Contains(extension)) seen.Add(extension);
        }

        return seen;
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
