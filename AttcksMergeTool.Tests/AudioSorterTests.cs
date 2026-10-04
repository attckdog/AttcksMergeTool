using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

public class AudioSorterTests
{
    [Theory]
    [InlineData(@"Female\728Kaya [Kaya]\Moaning\open mouth\3-high intensity\Processed\open mouth high 1.mp3", "Female/Moaning/3-High")]
    [InlineData(@"Female\MagicalMysticVA\Mini Moan Pack\Moans [Mouth Closed] Low Intensity (Fast).mp3", "Female/Muffled Moaning/1-Low")]
    [InlineData(@"Female\AdalineBeMine\Moaning\Regular\Slow\Slow Moans 1.mp3", "Female/Moaning/1-Low")]
    [InlineData(@"Female\VelvetSheetzVA\VelvetSheetzVA Voicepack\Voice (High)\Moans - Short\Moans Fast\Velvet-High-01.mp3", "Female/Moaning/3-High")]
    [InlineData(@"Female\Chiyo1000nights\moaning\orgasm\oho\low-fast\Processed\orgasm-oho_low-fast01.mp3", "Female/Orgasm/4-Extreme")]
    [InlineData(@"Female\HellicaVA\Oral\BJ1-Fast.mp3", "Female/Oral/3-High")]
    [InlineData(@"Male\JeremyColeNSFW\Breaths\Singles\Rough\RoughBreaths-009.mp3", "Male/Breathing/3-High")]
    [InlineData(@"Male\pixelcarnagee\Male Voice Pack\Oral\Kisses\B_MKiss-01.mp3", "Male/Kissing")]
    [InlineData(@"Male\Munt_works\Orc\Current Version\Massive\OrcV5_Munt_Roar (3).mp3", "Creature/Orc/Growls & Roars")]
    [InlineData(@"Male\AluryVA\Femboy\Climax\Femboy_Climax_Alury.mp3", "Femboy/Orgasm")]
    public void Clips_land_by_voice_category_and_intensity(string path, string expected) =>
        Assert.Equal(expected, AudioClassifier.TargetFolder(path));

    /// <remarks>
    /// A mood word such as "Dominant" in a file name is not speech, but anything under a
    /// dirty-talk folder is, whatever the individual line says.
    /// </remarks>
    [Theory]
    [InlineData(@"Female\Lurkydip\Lurkydip Voicepack\Moans\Dominant Moans 1.mp3", "Female/Moaning")]
    [InlineData(@"Female\AdalineBeMine\Dirty Talk\Short And Sweet\Sexy\I'm cumming\I'm cumming 1.mp3", "Female/Dialogue")]
    public void Dialogue_comes_from_dialogue_folders_not_mood_words(string path, string expected) =>
        Assert.Equal(expected, AudioClassifier.TargetFolder(path));

    /// <remarks>Pitch describes the voice; reading "High Pitch" as high intensity would misfile half a pack.</remarks>
    [Fact]
    public void Pitch_is_not_read_as_intensity() =>
        Assert.Equal("Female/Moaning",
            AudioClassifier.TargetFolder(@"Female\Hoshinomeririri\Moans - High Pitch\Moans A-1 OM.mp3"));

    [Theory]
    [InlineData("5Sultry_SDT", "Female/Oral/1-Low")]
    [InlineData("13Sultry_FM", "Female/Moaning/3-High")]
    [InlineData("14Sultry_MO", "Female/Orgasm/2-Medium")]
    public void LewdHeart_letter_codes_are_decoded(string stem, string expected) =>
        Assert.Equal(expected, AudioClassifier.TargetFolder($@"Female\LHeartVoiceOver [LewdHeart]\Sultry Voice Pack\{stem}.mp3"));

    [Fact]
    public void Raw_takes_with_a_processed_version_are_skipped() {
        using var workspace = new TempWorkspace();
        Write(workspace, @"Female\Kaya\Moaning\soft 1.mp3", "raw");
        Write(workspace, @"Female\Kaya\Moaning\Processed\soft 1.mp3", "clean");

        AudioSortPlan plan = AudioSorter.Plan(workspace.Root);

        AudioSortEntry raw = Assert.Single(plan.Entries, entry => entry.Source == "Female/Kaya/Moaning/soft 1.mp3");
        Assert.Null(raw.Target);
        Assert.Equal(1, plan.SortedCount);
    }

    [Fact]
    public void Identical_files_are_sorted_once() {
        using var workspace = new TempWorkspace();
        Write(workspace, @"Female\Miko\moan.mp3", "same bytes");
        Write(workspace, @"Male\Miko\moan.mp3", "same bytes");

        AudioSortPlan plan = AudioSorter.Plan(workspace.Root);

        Assert.Equal(1, plan.SortedCount);
        Assert.StartsWith("identical to Female/Miko/moan.mp3", plan.Entries.Single(entry => entry.Target is null).SkipReason);
    }

    /// <remarks>The performer stays in the file name, so a sorted clip can always be credited.</remarks>
    [Fact]
    public void Sorting_links_clips_under_the_performers_name_and_leaves_the_pack_alone() {
        using var workspace = new TempWorkspace();
        Write(workspace, @"Pack\Female\Kaya\Moans\soft moan.mp3", "kaya");
        Write(workspace, @"Pack\Female\Vel\Moans\soft moan.mp3", "vel");
        Write(workspace, @"Pack\Terms.pdf", "terms");

        AudioSortPlan plan = AudioSorter.Plan(workspace.Path("Pack"));
        AudioSortResult result = AudioSorter.Apply(plan, workspace.Path("Sorted"));

        Assert.Equal(2, result.Linked + result.Copied);
        Assert.Equal("kaya", File.ReadAllText(workspace.Path(@"Sorted\Female\Moaning\1-Low\Kaya - soft moan.mp3")));
        Assert.Equal("vel", File.ReadAllText(workspace.Path(@"Sorted\Female\Moaning\1-Low\Vel - soft moan.mp3")));
        Assert.True(File.Exists(workspace.Path(@"Sorted\Terms.pdf")));
        Assert.True(File.Exists(workspace.Path(@"Sorted\" + AudioSorter.ManifestFileName)));
        Assert.True(File.Exists(workspace.Path(@"Pack\Female\Kaya\Moans\soft moan.mp3")));
    }

    [Fact]
    public void Sorting_again_only_adds_what_is_missing() {
        using var workspace = new TempWorkspace();
        Write(workspace, @"Pack\Female\Kaya\Moans\soft moan.mp3", "kaya");
        AudioSorter.Apply(AudioSorter.Plan(workspace.Path("Pack")), workspace.Path("Sorted"));

        Write(workspace, @"Pack\Female\Kaya\Moans\hard moan.mp3", "new");
        AudioSortResult again = AudioSorter.Apply(AudioSorter.Plan(workspace.Path("Pack")), workspace.Path("Sorted"));

        Assert.Equal(1, again.AlreadyThere);
        Assert.Equal(1, again.Linked + again.Copied);
    }

    /// <remarks>
    /// The default puts the sorted folder inside the audio library, which may itself be what is
    /// being sorted. Its own clips must not be sorted a second time.
    /// </remarks>
    [Fact]
    public void A_sorted_folder_inside_the_pack_is_left_out_of_the_scan() {
        using var workspace = new TempWorkspace();
        Write(workspace, @"Female\Kaya\Moans\soft moan.mp3", "kaya");
        AudioSorter.Apply(AudioSorter.Plan(workspace.Root, workspace.Path("Sorted")), workspace.Path("Sorted"));

        AudioSortPlan again = AudioSorter.Plan(workspace.Root, workspace.Path("Sorted"));

        Assert.Single(again.Entries);
    }

    private static void Write(TempWorkspace workspace, string relative, string content) {
        string path = workspace.Path(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
