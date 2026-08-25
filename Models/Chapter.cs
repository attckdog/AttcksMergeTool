namespace AttcksMergeTool.Models;

/// <summary>
/// One navigable span of the merged video, written out as an FFMETADATA <c>[CHAPTER]</c>.
/// </summary>
/// <remarks>
/// Spans are explicit rather than derived from a list of start markers plus a terminator,
/// which is what lets the boundaries come from the measured segments instead of the script.
/// </remarks>
public sealed record Chapter(string Name, int StartMs, int EndMs);
