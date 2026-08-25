using System.Text.Json.Serialization;

namespace AttcksMergeTool.Models;

/// <summary>A single position keyframe: <c>Pos</c> (0-100) at <c>At</c> milliseconds.</summary>
public class ActionPoint
{
    [JsonPropertyName("at")] public int At { get; set; }
    [JsonPropertyName("pos")] public int Pos { get; set; }
}
