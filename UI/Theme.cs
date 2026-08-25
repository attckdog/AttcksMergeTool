using AttcksMergeTool.Services;

namespace AttcksMergeTool.UI;

/// <summary>
/// The dark palette and fonts for the app, named once so the raw
/// <see cref="Color.FromArgb(int, int, int)"/> triplets stop being scattered through
/// the layout code.
/// </summary>
internal static class Theme
{
    public static readonly Color Window = Color.FromArgb(30, 30, 30);
    public static readonly Color Toolbar = Color.FromArgb(45, 45, 48);
    public static readonly Color SidePanel = Color.FromArgb(35, 35, 40);

    /// <summary>Background for content areas that should read as recessed.</summary>
    public static readonly Color Well = Color.FromArgb(20, 20, 20);

    public static readonly Color Text = Color.White;
    public static readonly Color PrimaryAction = Color.SteelBlue;
    public static readonly Color SecondaryAction = Color.DimGray;
    public static readonly Color ConfirmAction = Color.SeaGreen;

    /// <summary>Actions that stop or discard work, kept visually distinct from the rest.</summary>
    public static readonly Color DestructiveAction = Color.Firebrick;

    /// <summary>Background for an input the user can type into, against the dark window.</summary>
    public static readonly Color Field = Color.FromArgb(45, 45, 48);

    /// <summary>
    /// Text for a value that is missing and that the user probably wants to do something
    /// about. Lighter than <see cref="DestructiveAction"/>, which is a button fill and reads
    /// as mud at text size against the dark background.
    /// </summary>
    public static readonly Color MissingValue = Color.FromArgb(235, 95, 95);

    /// <summary>De-emphasised text, for the hints under a setting.</summary>
    public static readonly Color MutedText = Color.FromArgb(160, 160, 160);

    /// <summary>The log font at its default size.</summary>
    public static Font LogFont { get; } = LogFontOfSize(DefaultLogFontSize);

    public const float DefaultLogFontSize = 10F;

    /// <summary>
    /// The log font at <paramref name="points"/>. Callers own what they get back and must
    /// dispose it, which is why <see cref="LogFont"/> stays separate as the shared default.
    /// </summary>
    public static Font LogFontOfSize(float points) => new("Consolas", points);

    /// <summary>Maps a service-layer log level onto its display colour.</summary>
    public static Color ForLogLevel(LogLevel level) => level switch {
        LogLevel.Success => Color.LimeGreen,
        LogLevel.Warning => Color.Yellow,
        LogLevel.Error => Color.Red,
        LogLevel.Heading => Color.Cyan,
        _ => Color.LightGray
    };
}
