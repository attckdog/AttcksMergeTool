namespace AttcksMergeTool.Models;

/// <summary>
/// One scene's funscripts: the main document plus the per-axis
/// <c>{scene}.{axis}.funscript</c> files that belong to it.
/// </summary>
/// <remarks>
/// Scenes are identified up front by <see cref="Services.SceneScriptIndex"/> rather than
/// discovered while merging, so a sibling can never be mistaken for a scene of its own.
/// <see cref="MainScriptPath"/> is null for a scene that has siblings and a video but no
/// main document; its axes still merge at the right offset.
/// </remarks>
public sealed record SceneScripts(string Name, string? MainScriptPath, IReadOnlyList<string> SiblingScriptPaths)
{
    /// <summary>
    /// Sort key. The scenes must be walked in the same order the videos are concatenated in,
    /// and the video list is ordered by filename - so ordering on the notional script filename
    /// keeps the two walks in step even for a scene whose main script is missing.
    /// </summary>
    public string SortKey => Name + Services.MediaFileScanner.FunscriptExtension;
}
