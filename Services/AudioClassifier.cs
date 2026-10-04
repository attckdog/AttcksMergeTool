using System.Text.RegularExpressions;

namespace AttcksMergeTool.Services;

/// <summary>
/// Works out where a voice clip belongs - voice, category and intensity - from nothing but its
/// path inside a pack. Tuned against the OpenNSFW VA pack, where every performer names their
/// folders and files differently, but any pack laid out as <c>Gender/Performer/...</c> gets a
/// sensible result.
/// </summary>
/// <remarks>
/// The file name is read first and then the folders from the innermost out, because the
/// nearest name is the most specific: a "Kisses" file inside an "Oral" folder is a kiss.
/// </remarks>
public static class AudioClassifier
{
    private static readonly string[] Levels = ["1-Low", "2-Medium", "3-High", "4-Extreme"];

    private static readonly (string Key, string Label)[] Creatures = [
        ("goblin", "Goblin"), ("orc", "Orc"), ("troll", "Troll"), ("werewolf", "Werewolf"),
        ("sangheili", "Alien"), ("sectoid", "Alien")
    ];

    /// <summary>The first category whose pattern matches wins, so order is priority.</summary>
    private static readonly (string Category, Regex Pattern)[] Categories = [
        ("Laughing", Words(@"laugh\w*|giggl\w*|chuckl\w*")),
        ("Kissing", Words(@"kiss\w*|smooch\w*")),
        ("Oral", Words(@"oral|blowjob\w*|bj\d*|dt\d*|deep ?throat\w*|fellatio|suck\w*|lick\w*|cunnilingus|face ?fuck\w*|gluck\w*|gagging|"
                       + @"swallow\w*|cum (in )?(mouth|throat)|cum coming out|taste it|pull ?outs?|cum inside outside|gag")),
        ("Dialogue", Words(@"dialog\w*|dirty talk|talk|voice lines?|lines?|phrases|words|spoken|whispers?|jp|japanese|quotes|"
                           + @"come on|oh yeah|mocking|ohh yess|fuck")),
        ("Post-Orgasm", Words(@"post orgasm|after climax|climax recovery|exhausted")),
        ("Orgasm", Words(@"orgasm\w*|climax\w*|nut|finish|cum")),
        ("Moaning", Words(@"breathy moan\w*")),
        ("Breathing", Words(@"breath|breaths|breathy|breathing|pant|panting|gasp\w*|sighs?|exhales?|inhales?|heaving|exertion")),
        ("Pain & Struggle", Words(@"pain\w*|struggl\w*|resist\w*|strain\w*|reluctant|cry\w*|oww?|hurt")),
        ("Muffled Moaning", Words(@"closed mouth|closed|mouth closed|cm|gagged|hand gagged|clench\w*|gritted|muffled|full mouth|"
                                  + @"choking|fingers in mouth")),
        ("Moaning", Words(@"moan\w*|groan\w*|grunt\w*|whimper\w*|whin\w*|mm+h?|mmm s|oho|tongue out|open mouth|openmouth|"
                          + @"transition\w*|reactions|pound\w*|grind\w*|overstimulat\w*|noises|sounds|heightened|ahh?|uh+|nnh|aah|tongue outh|"
                          + @"pleasure\w*|effort|opened|ooened|noans|intense|realistic")),
        ("Growls & Roars", Words(@"roar\w*|growl\w*|angry|snarl\w*|howl\w*|gr+r+\w*")),
        ("Sound Effects", Words(@"sfx|handjob|fingering|vibrator|womanizer|spitting|sniff\w*|snif|straw"))
    ];

    /// <summary>Folder names that say nothing about what is in them.</summary>
    private static readonly Regex NeutralFolder = new(
        @"^(processed|set \w|sets|\d+|pack \d+|v\d|origin|original|new|old|current version|old version|singles|edited_singles|"
        + @"individual moans|full tracks|tracks|.*voice lines and noises.*|.*voice ?pack.*|.*pack$)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Folders whose whole content is speech, whatever the files inside are called.</summary>
    private static readonly Regex DialogueFolder =
        Words(@"dirty talk|dialog\w*|voice lines|spoken|jp lines|japanese dialogue|common lines|short phrases");

    /// <summary>Pitch describes the voice, not how intense it is, so it is removed before intensity is read.</summary>
    private static readonly Regex Pitch = new(
        @" (high|low|medium|mid|medium low|normal|deep|anime style) (pitch(ed)?|voice|tone) | voice (high|low|medium) | "
        + @"(pitch|male medium|softboy|soft boy|pretty boy) ",
        RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitIntensity =
        new(@" (low|moderate|medium|mid|high|extreme)( to (high|intense|extreme))? intens\w* ", RegexOptions.CultureInvariant);

    private static readonly (int Level, Regex Pattern)[] StrongWords = [
        (4, Words(@"extreme|extra|crazy|harder|fastest|rapid|forceful|oho|enjoyable|intense moans with orgasm")),
        (3, Words(@"intense|hard|rough|heavy|aggressive|agressive|heightened|passionate|shouty|pounded|overstimulated|forceful")),
        (1, Words(@"soft|gentle|quiet|subdued|tame|weak|light|slight|sensual|relaxed|sporadic"))
    ];

    private static readonly (int Level, Regex Pattern)[] SpeedWords = [
        (3, Words("fast|faster")), (2, Words("medium|mid")), (1, Words("slow"))
    ];

    private static readonly (int Level, Regex Pattern)[] WeakWords = [
        (3, Words("guttural")), (2, Words("regular|normal|basic"))
    ];

    /// <summary>LewdKitzu sorts by mood, which maps onto a category and intensity directly.</summary>
    private static readonly Dictionary<string, (string Category, int? Level)> KitzuMoods = new(StringComparer.OrdinalIgnoreCase) {
        ["breathy"] = ("Breathing", null),
        ["cheerful and giggly"] = ("Moaning", 2),
        ["curious and shy"] = ("Moaning", 1),
        ["extreme femboy"] = ("Moaning", 4),
        ["quiet or subdued"] = ("Moaning", 1),
        ["relaxed or casual"] = ("Moaning", 1),
        ["resisting or strained"] = ("Pain & Struggle", null),
        ["serious or cold"] = ("Moaning", 2),
        ["submissive and whimpery"] = ("Moaning", 2)
    };

    /// <summary>Categories where an intensity level would mean little.</summary>
    private static readonly HashSet<string> Unlevelled = ["Laughing", "Dialogue", "Kissing", "Misc", "Long Loops"];

    /// <summary>
    /// The folder a clip belongs in, as <c>Voice[/Creature]/Category[/Intensity]</c> with forward
    /// slashes, for example <c>Female/Moaning/3-High</c>. Clips with no intensity clue stay at
    /// the category level, so choosing a category folder still picks up every level below it.
    /// </summary>
    /// <param name="relativePath">The clip's path inside the pack, with either slash.</param>
    public static string TargetFolder(string relativePath) {
        string[] parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool gendered = parts.Length >= 3 && parts[0] is { } first
                        && (first.Equals("Female", StringComparison.OrdinalIgnoreCase) || first.Equals("Male", StringComparison.OrdinalIgnoreCase));

        // A pack that is not split by gender is treated as one performer per top-level folder.
        string gender = gendered ? parts[0] : "Other";
        string performer = gendered ? parts[1] : parts.Length >= 2 ? parts[0] : string.Empty;
        string[] folders = parts[(gendered ? 2 : Math.Min(1, parts.Length - 1))..^1];
        string stem = Path.GetFileNameWithoutExtension(parts[^1]);

        var folderTexts = new List<string>();

        foreach (string folder in folders.Reverse()) {
            if (NeutralFolder.IsMatch(folder.Trim())) continue;
            // JeremyColeNSFW splits by pitch everywhere except his breaths, where "Medium" is a level.
            if (performer == "JeremyColeNSFW" && folder is "Deep" or "High" or "Medium" && !folders.Contains("Breaths")) continue;

            string text = SplitWords(folder);
            if (performer.StartsWith("VoxAfterHours", StringComparison.Ordinal)) text = Regex.Replace(text, " (high|low|mid) $", " ");

            folderTexts.Add(text);
        }

        string nameText = SplitWords(stem);
        List<string> texts = [nameText, .. folderTexts];

        (string voice, string? creature) = VoiceOf(gender, performer, SplitWords(string.Join(' ', parts)));
        List<string> levelTexts = [.. texts.Select(text => Pitch.Replace(text, " ").Replace(" extra spice ", " "))];

        string category;
        int? level;

        if (performer.StartsWith("LHeartVoiceOver", StringComparison.Ordinal)) {
            (category, level) = LewdHeart(stem);
        } else if (performer == "LewdKitzu" && folders.Length > 0 && KitzuMoods.TryGetValue(folders[0], out var mood)) {
            (category, level) = mood;
            if (folders[0].Equals("extreme femboy", StringComparison.OrdinalIgnoreCase)) voice = "Femboy";
        } else if (folders.Contains("Masterloops")) {
            (category, level) = ("Long Loops", null);
        } else if (folderTexts.Any(DialogueFolder.IsMatch)) {
            string fromName = Classify([nameText]);
            (category, level) = (fromName is "Laughing" or "Kissing" or "Breathing" ? fromName : "Dialogue", null);
        } else {
            category = Classify(texts);
            level = Intensity(levelTexts);

            // The pack-level folder names were skipped as too vague; better than nothing.
            if (category == "Misc") category = Classify([.. folders.Reverse().Select(SplitWords)]);

            if (category == "Moaning" && folders.Any(folder =>
                    folder.Equals("climax", StringComparison.OrdinalIgnoreCase)
                    || folder.Equals("climaxes", StringComparison.OrdinalIgnoreCase)
                    || folder.Equals("orgasm", StringComparison.OrdinalIgnoreCase))) {
                category = "Orgasm";
            }
        }

        if (Unlevelled.Contains(category)) level = null;

        List<string> target = [voice];
        if (creature is not null) target.Add(creature);
        target.Add(category);
        if (level is { } known) target.Add(Levels[known - 1]);

        return string.Join('/', target);
    }

    /// <summary>
    /// Lower-cased, with camelCase, digits and punctuation split into single-spaced words and a
    /// space at each end, so every pattern can match whole words with plain spaces.
    /// </summary>
    internal static string SplitWords(string text) {
        text = Regex.Replace(text, "([a-z])([A-Z])", "$1 $2");
        text = Regex.Replace(text, "([A-Z])([A-Z][a-z])", "$1 $2");
        text = Regex.Replace(text, "(?<=[0-9])(?=[A-Za-z])|(?<=[A-Za-z])(?=[0-9])", " ");
        text = Regex.Replace(text, @"[_\-+.,\[\]()#&~]+", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

        return $" {text} ";
    }

    private static (string Voice, string? Creature) VoiceOf(string gender, string performer, string allText) {
        if (performer is "Munt_works" or "Otoha_Suzune" || allText.Contains(" monster ", StringComparison.Ordinal)) {
            foreach ((string key, string label) in Creatures) {
                if (allText.Contains($" {key}", StringComparison.Ordinal)) return ("Creature", label);
            }

            return ("Creature", performer == "Otoha_Suzune" ? "Goblin" : "Monster");
        }

        if (gender == "Female" && allText.Contains(" male moan ", StringComparison.Ordinal)) return ("Male", null);
        if (gender == "Male" && allText.Contains(" femboy ", StringComparison.Ordinal)) return ("Femboy", null);

        return (gender, null);
    }

    private static string Classify(IEnumerable<string> texts) {
        foreach (string text in texts) {
            foreach ((string category, Regex pattern) in Categories) {
                if (pattern.IsMatch(text)) return category;
            }
        }

        return "Misc";
    }

    /// <summary>
    /// An explicit "high intensity" anywhere wins, then descriptive words, then speed, then the
    /// vaguest words - each tier checked through every name before the next is tried.
    /// </summary>
    private static int? Intensity(IReadOnlyList<string> texts) {
        foreach (string text in texts) {
            Match match = ExplicitIntensity.Match(text);

            if (match.Success) {
                string word = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[1].Value;
                return word switch { "low" => 1, "high" or "intense" => 3, "extreme" => 4, _ => 2 };
            }
        }

        foreach ((int Level, Regex Pattern)[] tier in (IEnumerable<(int, Regex)[]>)[StrongWords, SpeedWords, WeakWords]) {
            foreach (string text in texts) {
                foreach ((int level, Regex pattern) in tier) {
                    if (pattern.IsMatch(text)) return level;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// LewdHeart names files with letter codes such as "5Sultry_SDT": S/M/F/E for slow, medium,
    /// fast and "enjoyable", then B blowjob, DT deepthroat, O orgasm, M or D moaning.
    /// </summary>
    private static (string Category, int? Level) LewdHeart(string stem) {
        string code = Regex.Replace(stem, "^[0-9]+(Sultry_?|Sweet|Tomboy)", "").Replace("mp3", "");
        code = Regex.Replace(code, "Enjoy(able)?", "E");

        string? category = null;
        string rest = code;

        if (code.Contains("DT")) {
            (category, rest) = ("Oral", RemoveFirst(code, "DT"));
        } else {
            foreach ((string letter, string mapped) in (ReadOnlySpan<(string, string)>)[("B", "Oral"), ("O", "Orgasm"), ("D", "Moaning"), ("M", "Moaning")]) {
                if (code.Contains(letter)) {
                    (category, rest) = (mapped, RemoveFirst(code, letter));
                    break;
                }
            }

            if (category is null) return ("Moaning", code.Contains('E') ? 4 : null);
        }

        foreach ((char letter, int level) in (ReadOnlySpan<(char, int)>)[('E', 4), ('F', 3), ('M', 2), ('S', 1)]) {
            if (rest.Contains(letter)) return (category, level);
        }

        return (category, null);
    }

    private static string RemoveFirst(string text, string value) {
        int index = text.IndexOf(value, StringComparison.Ordinal);
        return index < 0 ? text : text.Remove(index, value.Length);
    }

    /// <summary>A pattern of alternatives that must each stand as whole words, space-delimited.</summary>
    private static Regex Words(string alternatives) => new($" ({alternatives}) ", RegexOptions.CultureInvariant);
}
