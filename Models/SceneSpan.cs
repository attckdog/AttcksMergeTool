namespace AttcksMergeTool.Models;

/// <summary>
/// Where one scene ended up on the merged script's timeline, as the script merge planned it.
/// </summary>
/// <remarks>
/// The plan comes from probing the <em>source</em> videos, which is not quite what the encode
/// produces. These spans are what <see cref="Services.ScriptRetimer"/> moves onto the measured
/// segment lengths once the encode has run.
/// </remarks>
/// <param name="StartMs">
/// Where the scene's own content starts - after any <paramref name="LeadInMs"/>, not before it.
/// This is what its bookmark and its chapter both mark.
/// </param>
/// <param name="LeadInMs">
/// Black inserted immediately before this scene so the axes have time to reach the position it
/// opens at. Zero for the first scene and whenever nothing had to move. It is the one channel
/// by which the script merge tells the video merge how much filler to generate, and the
/// retimer which keyframes sit in it.
/// </param>
public sealed record SceneSpan(string Name, int StartMs, int DurationMs, int LeadInMs = 0)
{
    public int EndMs => StartMs + DurationMs;

    /// <summary>Where this scene's lead-in begins, which is where the previous one ended.</summary>
    public int LeadInStartMs => StartMs - LeadInMs;
}
