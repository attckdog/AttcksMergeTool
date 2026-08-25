using System.Text.Json.Serialization;

namespace AttcksMergeTool.Models;

/// <summary>A named marker at a millisecond offset; one is emitted per merged scene.</summary>
public class Bookmark
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("time")] public int Time { get; set; }
}
