using System.Text.Json.Serialization;

namespace AttcksMergeTool.Models;

/// <summary>
/// One auxiliary motion axis (L1, R0, ...) and its keyframes. See
/// <see cref="Services.FunscriptAxisMap"/> for the friendly-name to axis-id mapping.
/// </summary>
public class FunscriptAxis
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("actions")] public List<ActionPoint>? Actions { get; set; }
}
