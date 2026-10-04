namespace AttcksMergeTool.Models;

/// <summary>
/// Per-video voice injection: random clips drawn from folders of the audio library and mixed
/// over the video's own audio, separated by random gaps, until the scene ends.
/// </summary>
public sealed class VoiceInjection
{
    public const int DefaultVoiceVolumePercent = 100;
    public const int DefaultOriginalVolumePercent = 100;
    public const double DefaultMinGapSeconds = 2;
    public const double DefaultMaxGapSeconds = 8;

    /// <summary>Folders relative to the audio library root. Each one includes everything below it.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Loudness of the injected clips, as a percentage of their own level.</summary>
    public int VoiceVolumePercent { get; set; } = DefaultVoiceVolumePercent;

    /// <summary>Loudness of the video's own audio underneath them.</summary>
    public int OriginalVolumePercent { get; set; } = DefaultOriginalVolumePercent;

    /// <summary>Shortest silence between two clips.</summary>
    public double MinGapSeconds { get; set; } = DefaultMinGapSeconds;

    /// <summary>Longest silence between two clips.</summary>
    public double MaxGapSeconds { get; set; } = DefaultMaxGapSeconds;

    /// <summary>Whether there is anything to inject. No folders means the video is encoded as-is.</summary>
    public bool IsActive => Folders.Count > 0;

    public VoiceInjection Clone() => new() {
        Folders = [.. Folders],
        VoiceVolumePercent = VoiceVolumePercent,
        OriginalVolumePercent = OriginalVolumePercent,
        MinGapSeconds = MinGapSeconds,
        MaxGapSeconds = MaxGapSeconds
    };
}
