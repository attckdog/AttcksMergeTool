namespace AttcksMergeTool.Models;

/// <summary>
/// Pre-rendered voice tracks to mix over one scene's own audio, and how loud each side plays.
/// </summary>
/// <param name="TrackPaths">
/// Full-length tracks starting at the scene's first frame, each holding some of the placed clips.
/// </param>
public sealed record VoiceMix(IReadOnlyList<string> TrackPaths, int VoiceVolumePercent, int OriginalVolumePercent);
