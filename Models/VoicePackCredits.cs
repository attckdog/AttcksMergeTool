namespace AttcksMergeTool.Models;

/// <summary>One performer or contributor to credit, by the X (Twitter) handle the pack asks for.</summary>
/// <param name="Name">How the pack names them, when that differs from the handle.</param>
public sealed record VoiceCredit(string Handle, string? Name = null)
{
    public string Url => $"https://x.com/{Handle}";

    /// <summary>"@handle", or "@handle (Name)" when the pack gives them another name.</summary>
    public string Display => Name is null ? $"@{Handle}" : $"@{Handle} ({Name})";
}

/// <summary>
/// Everyone the bundled OpenNSFW voice pack asks to be credited. Its terms want performers
/// credited by X handle and the work always described as a voice pack; CREDITS.md carries the
/// same list for people reading the files rather than the app.
/// </summary>
public static class VoicePackCredits
{
    public const string PackName = "OpenNSFW Voice Pack";
    public const string PackHandle = "OpenNSFWSP";
    public const string PackUrl = "https://x.com/OpenNSFWSP";
    public const string PackSiteUrl = "https://opennsfw.carrd.co/";
    public const string PackLicenseUrl = "https://creativecommons.org/licenses/by/4.0/";

    public static IReadOnlyList<VoiceCredit> Female { get; } = [
        new("728kaya", "Kaya"),
        new("AdalineBeMine"),
        new("ChisaEnigmaVA"),
        new("chiyo1000nights"),
        new("geministarsign1"),
        new("HellicaVA"),
        new("Hoshinomeririri", "星野めりか"),
        new("LecheryAmoreVA"),
        new("lheartvoiceover", "LewdHeart"),
        new("LillithMrngstrr"),
        new("Lurkdip", "Lurkydip"),
        new("MagicalMysticVA"),
        new("MIKO_DESH"),
        new("Misuzugon"),
        new("NariAudios", "Nariko Nabi"),
        new("NyaughtyMeow"),
        new("Qrixlvoice"),
        new("SwitchyValAudio"),
        new("VelvetSheetzVA"),
        new("venusdevelours"),
        new("VoxAfterHours", "Vox After Hours")
    ];

    public static IReadOnlyList<VoiceCredit> Male { get; } = [
        new("AluryVA"),
        new("AnnussyMorussy"),
        new("FuriousRedDemon"),
        new("hitalva", "HitalVA"),
        new("JeremyColeNSFW"),
        new("KGF_Kiko"),
        new("Lewddmon"),
        new("LewdKitzu"),
        new("MIKO_DESH"),
        new("Munt_works", "Munt_Works"),
        new("Otoha_Suzune"),
        new("pixelcarnagee"),
        new("RayTracingVA_18", "RayTracingVA"),
        new("SoftKazu_"),
        new("ThunderingKVA")
    ];

    /// <summary>Sound editing and compiling, named in the Hoshinomeririri pack's information sheet.</summary>
    public static IReadOnlyList<VoiceCredit> Editors { get; } = [
        new("LeHornySFX3D"),
        new("evilzorak", "EvilZorak")
    ];
}
