using System.Text.Json.Serialization;

namespace AttcksMergeTool.Models;

/// <summary>Optional descriptive block written into the merged funscript.</summary>
/// <remarks>
/// Every member is nullable so that unset fields are omitted from the output by
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> rather than written as empty values.
/// </remarks>
public class FunscriptMetadata
{
    [JsonPropertyName("creator")] public string? Creator { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("license")] public string? License { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("performers")] public List<string>? Performers { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}
