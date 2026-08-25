using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Finds the trim configured for a scene, keyed by base name.
/// </summary>
/// <remarks>
/// One rule, one place. The two mergers used to look the same setting up two different ways -
/// the video pass matched on the full file path, the script pass on the base name - so they
/// could disagree about which video a trim belonged to. Both were also a linear scan inside a
/// loop, the video one inside the parallel encode body.
/// </remarks>
public sealed class TrimLookup
{
    private readonly Dictionary<string, VideoSegmentSettings> _bySceneName;

    public TrimLookup(IEnumerable<VideoSegmentSettings> settings) {
        _bySceneName = new Dictionary<string, VideoSegmentSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (VideoSegmentSettings entry in settings) {
            // First wins: two videos sharing a base name across extensions are one scene, and
            // the scene merge can only honour one of them.
            _bySceneName.TryAdd(Path.GetFileNameWithoutExtension(entry.FilePath), entry);
        }
    }

    /// <summary>A lookup with nothing configured, for merges that carry no trims.</summary>
    public static TrimLookup Empty { get; } = new([]);

    /// <summary>The settings for <paramref name="sceneName"/>, or <c>null</c> when it has none.</summary>
    public VideoSegmentSettings? For(string sceneName) => _bySceneName.GetValueOrDefault(sceneName);

    /// <summary>The settings for the file at <paramref name="path"/>, or <c>null</c>.</summary>
    public VideoSegmentSettings? ForFile(string path) => For(Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// The trim window for <paramref name="sceneName"/>, or <see cref="TrimWindow.None"/>
    /// when it has no settings or trimming is switched off for it.
    /// </summary>
    public TrimWindow WindowFor(string sceneName) =>
        For(sceneName) is { UseTrim: true } settings
            ? TrimWindow.FromSeconds(settings.StartTime, settings.EndTime)
            : TrimWindow.None;
}
