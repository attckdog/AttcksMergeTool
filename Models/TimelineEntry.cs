namespace AttcksMergeTool.Models;

/// <summary>
/// One position on the merged timeline, and everything that fills it.
/// </summary>
/// <remarks>
/// The merged script and the merged video used to be two independent walks over two
/// independently built lists - funscripts for one, videos for the other - which is why an
/// unpaired file on either side silently desynced them. A plan of these entries is now built
/// once and both walks follow it, so the two timelines cannot describe different scenes.
/// <para>
/// <see cref="VideoPath"/> is null only on a script-only run, where there is no video to
/// stay in sync with. <see cref="Scripts"/> is null for a video that has no funscript: it
/// still occupies its full length so that everything after it stays aligned.
/// </para>
/// </remarks>
public sealed record TimelineEntry(string Name, SceneScripts? Scripts, string? VideoPath);
