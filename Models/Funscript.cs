using System.Text.Json.Serialization;

namespace AttcksMergeTool.Models;

/// <summary>
/// Root of a .funscript document. Property names are pinned by
/// <see cref="JsonPropertyNameAttribute"/> and must not drift &mdash; they are the
/// on-disk contract shared with every other funscript tool.
/// </summary>
/// <remarks>
/// Collections are nullable on purpose: a source file may omit them entirely, and
/// the merge logic distinguishes "absent" from "empty".
/// </remarks>
public class Funscript
{
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0";
    [JsonPropertyName("inverted")] public bool Inverted { get; set; }
    [JsonPropertyName("range")] public int Range { get; set; } = 100;
    [JsonPropertyName("metadata")] public FunscriptMetadata? Metadata { get; set; }
    [JsonPropertyName("actions")] public List<ActionPoint>? Actions { get; set; }
    [JsonPropertyName("bookmarks")] public List<Bookmark>? Bookmarks { get; set; }
    [JsonPropertyName("axes")] public List<FunscriptAxis>? Axes { get; set; }
}
