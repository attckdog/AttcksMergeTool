namespace AttcksMergeTool.Models;

/// <summary>
/// Where one scene ended up on the merged script's timeline, as the script merge planned it.
/// </summary>
/// <remarks>
/// The plan comes from probing the <em>source</em> videos, which is not quite what the encode
/// produces. These spans are what <see cref="Services.ScriptRetimer"/> moves onto the measured
/// segment lengths once the encode has run.
/// </remarks>
public sealed record SceneSpan(string Name, int StartMs, int DurationMs)
{
    public int EndMs => StartMs + DurationMs;
}
