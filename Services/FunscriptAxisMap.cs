namespace AttcksMergeTool.Services;

/// <summary>
/// Translates the friendly axis names used in filenames and embedded axis ids
/// ("surge", "twist", ...) into the canonical funscript axis codes.
/// </summary>
public static class FunscriptAxisMap
{
    /// <summary>Axis id for the primary stroke track, which lives at the document root.</summary>
    public const string RootAxisId = "L0";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase) {
        { "surge", "L1" },
        { "sway", "L2" },
        { "twist", "R0" },
        { "roll", "R1" },
        { "pitch", "R2" }
    };

    /// <summary>
    /// Maps <paramref name="name"/> to its axis code, or returns <paramref name="fallback"/>
    /// unchanged when the name is not a known alias (custom axes pass through as-is).
    /// </summary>
    public static string Resolve(string? name, string? fallback) =>
        name is not null && Map.TryGetValue(name, out string? mapped)
            ? mapped
            : fallback ?? name ?? string.Empty;
}
