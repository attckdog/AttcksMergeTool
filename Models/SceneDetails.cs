namespace AttcksMergeTool.Models;

/// <summary>
/// What the video list shows about one input file beyond its name: how long the video runs,
/// whether a funscript was found for it and how many axes that scene's scripts contribute.
/// </summary>
/// <remarks>
/// Display only, and deliberately not part of <see cref="VideoSegmentSettings"/>: a job clones
/// the settings and carries them onto worker threads, and none of this is anything a job needs.
/// A row with no details read yet has no entry at all rather than a blank one, so "not scanned
/// yet" and "scanned, nothing found" stay distinguishable.
/// </remarks>
/// <param name="DurationMs">
/// Length of the video, or <c>null</c> when ffprobe could not read it - see
/// <see cref="Services.IMediaProbe.GetDurationMsAsync"/>.
/// </param>
/// <param name="HasScript">Whether the scene has a funscript of any kind, main or per-axis.</param>
/// <param name="AxisCount">How many distinct axes those scripts would contribute to a merge.</param>
public sealed record SceneDetails(int? DurationMs, bool HasScript, int AxisCount);
