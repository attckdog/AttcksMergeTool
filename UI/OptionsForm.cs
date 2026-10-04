using System.Diagnostics;
using AttcksMergeTool.Models;
using AttcksMergeTool.Services;

namespace AttcksMergeTool.UI;

/// <summary>
/// The settings dialog. Edits a copy of the caller's <see cref="AppSettings"/> and exposes it
/// as <see cref="Result"/> once OK is pressed, so Cancel really does cancel and the caller
/// owns the decision to persist.
/// </summary>
public sealed partial class OptionsForm : Form
{
    /// <summary>Position of the Encoding page in the category rail, for jumping to a bad value.</summary>
    private const int EncodingPageIndex = 1;

    private readonly AppSettings _settings;

    /// <summary>Set while a pack is being sorted, which the dialog must not close under.</summary>
    private bool _sorting;

    public OptionsForm(AppSettings settings) {
        _settings = settings.Clone();

        BuildUi();
        LoadFrom(_settings);
        SuggestSortFolders(_settings.AudioFolder);

        FormClosing += (_, e) => {
            if (!_sorting) return;

            e.Cancel = true;
            SetSortStatus("Wait for the sort to finish before closing Options.", Theme.ForLogLevel(LogLevel.Warning));
        };
    }

    /// <summary>
    /// The edited settings. Only meaningful once <see cref="Form.ShowDialog()"/> returned
    /// <see cref="DialogResult.OK"/>; on Cancel this is the untouched copy.
    /// </summary>
    public AppSettings Result => _settings;

    // --- Event handlers ---

    private void Categories_SelectedIndexChanged(object? sender, EventArgs e) {
        for (int i = 0; i < _pages.Count; i++) {
            _pages[i].Visible = i == _categories.SelectedIndex;
        }
    }

    private void BtnOk_Click(object? sender, EventArgs e) {
        // The numeric and combo inputs cannot hold a bad value, but the free-text ones can, and
        // a bad resolution would not surface until ffmpeg rejected the whole encode.
        if (!TryValidateResolution()) return;

        ApplyTo(_settings);
        _settings.Normalize();

        DialogResult = DialogResult.OK;
        Close();
    }

    private void BtnDefaults_Click(object? sender, EventArgs e) {
        DialogResult confirmation = MessageBox.Show(
            this,
            "Reset every option on every page back to its default?",
            "Restore Defaults",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        // Only the controls are reset; nothing is written until OK, so this stays undoable.
        if (confirmation == DialogResult.Yes) LoadFrom(new AppSettings());
    }

    private bool TryValidateResolution() {
        var candidate = new AppSettings { TargetResolution = _txtTargetResolution.Text };
        candidate.Normalize();

        if (candidate.TargetResolution == _txtTargetResolution.Text.Trim()) return true;

        MessageBox.Show(
            this,
            "The target resolution must be written as width:height, for example 1920:1080.",
            "Options",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        _categories.SelectedIndex = EncodingPageIndex;
        _txtTargetResolution.Focus();
        _txtTargetResolution.SelectAll();

        return false;
    }

    private void BrowseForFolder(TextBox target) {
        using var dialog = new FolderBrowserDialog { InitialDirectory = ExistingFolder(target.Text) };

        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    private void BrowseForExecutable(TextBox target) {
        using var dialog = new OpenFileDialog {
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
    }

    /// <summary>
    /// Runs the tool with <c>-version</c> and reports whether it answered, so a wrong path is
    /// found here rather than a minute into a merge.
    /// </summary>
    private async Task TestToolAsync(TextBox target) {
        // Resolved the same way a job would resolve it, so what is tested is what would run.
        string tool = target == _txtFfmpegPath
            ? new MergeOptions { FfmpegPath = target.Text }.FfmpegPath
            : new MergeOptions { FfprobePath = target.Text }.FfprobePath;

        SetToolStatus($"Checking {tool}...", Theme.MutedText);

        bool exists = await ProcessRunner.Default.CommandExistsAsync(tool);

        SetToolStatus(
            exists ? $"OK - {tool} responded." : $"Could not run {tool}.",
            Theme.ForLogLevel(exists ? LogLevel.Success : LogLevel.Error));
    }

    private void SetToolStatus(string message, Color colour) {
        _lblToolStatus.Text = message;
        _lblToolStatus.ForeColor = colour;
    }

    /// <summary>
    /// Plans the sort, and with <paramref name="apply"/> builds it after a confirmation. The
    /// work runs off the UI thread: planning reads every clip to find duplicates.
    /// </summary>
    private async Task SortAudioAsync(bool apply) {
        string source, destination;

        try {
            source = MergeOptions.ResolvePath(_txtSortSource.Text);
            destination = MergeOptions.ResolvePath(_txtSortDestination.Text);
        } catch (ArgumentException) {
            SetSortStatus("One of the folders is not a valid path.", Theme.ForLogLevel(LogLevel.Error));
            return;
        }

        if (!Directory.Exists(source)) {
            SetSortStatus($"Pack folder not found: {source}", Theme.ForLogLevel(LogLevel.Error));
            return;
        }

        // Inside the pack is fine - the plan leaves the sorted folder out of its scan - but the
        // pack folder itself would mix sorted links in among the originals.
        if (SameFolder(source, destination)) {
            SetSortStatus("The sorted folder must be different from the pack folder.", Theme.ForLogLevel(LogLevel.Error));
            return;
        }

        var progress = new Progress<string>(message => SetSortStatus(message, Theme.MutedText));
        SetSorting(true);

        try {
            AudioSortPlan plan = await Task.Run(() => AudioSorter.Plan(source, destination, progress));
            _txtSortSummary.Text = DescribePlan(plan, destination);

            if (plan.SortedCount == 0) {
                SetSortStatus("No audio clips found in that folder.", Theme.ForLogLevel(LogLevel.Warning));
                return;
            }

            if (!apply) {
                SetSortStatus($"Preview: {plan.SortedCount:N0} clips would be sorted, {plan.SkippedCount:N0} skipped. Nothing was written.",
                    Theme.ForLogLevel(LogLevel.Info));
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                this,
                $"Sort {plan.SortedCount:N0} clips into {destination}?\n\n"
                + $"{plan.SkippedCount:N0} duplicates are left out. The pack itself is not changed.",
                "Sort Pack",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);

            if (confirmation != DialogResult.OK) {
                SetSortStatus("Sort cancelled. Nothing was written.", Theme.MutedText);
                return;
            }

            AudioSortResult result = await Task.Run(() => AudioSorter.Apply(plan, destination, progress));

            string copies = result.Copied > 0 ? $", {result.Copied:N0} copied (another drive)" : string.Empty;
            SetSortStatus(
                $"Done: {result.Linked:N0} linked{copies}, {result.AlreadyThere:N0} already there. "
                + "Use Rescan in the audio folder picker to see them.",
                Theme.ForLogLevel(LogLevel.Success));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            SetSortStatus($"Sorting failed: {ex.Message}", Theme.ForLogLevel(LogLevel.Error));
        } finally {
            SetSorting(false);
        }
    }

    private static string DescribePlan(AudioSortPlan plan, string destination) {
        var text = new System.Text.StringBuilder();

        text.AppendLine($"{plan.Entries.Count:N0} clips: {plan.SortedCount:N0} sorted, {plan.SkippedCount:N0} skipped");
        foreach ((string reason, int count) in plan.SkipCounts) text.AppendLine($"  skipped {count,5:N0}  {reason.TrimEnd()}");

        text.AppendLine().AppendLine($"Into {destination}:");
        foreach ((string folder, int count) in plan.FolderCounts) text.AppendLine($"  {count,5:N0}  {folder.Replace('/', '\\')}");

        return text.ToString();
    }

    private static bool SameFolder(string first, string second) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private void SetSorting(bool sorting) {
        _sorting = sorting;
        _btnSortPreview.Enabled = !sorting;
        _btnSortRun.Enabled = !sorting;
        _btnOk.Enabled = !sorting;
        _btnCancel.Enabled = !sorting;
        UseWaitCursor = sorting;
    }

    private void SetSortStatus(string message, Color colour) {
        _lblSortStatus.Text = message;
        _lblSortStatus.ForeColor = colour;
    }

    /// <summary>
    /// Starts the sorting page on the OpenNSFW pack when it is in the audio library, sorting
    /// into a "Sorted" folder beside it so the result can be picked straight away.
    /// </summary>
    private void SuggestSortFolders(string audioFolder) {
        string library;

        try {
            library = MergeOptions.ResolvePath(audioFolder);
        } catch (ArgumentException) {
            return;
        }

        string pack = Path.Combine(library, "OpenNSFW VA");

        _txtSortSource.Text = Directory.Exists(pack) ? pack : library;
        _txtSortDestination.Text = Path.Combine(library, AudioSorter.DefaultFolderName);
    }

    /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
    private void OpenLink(string url) {
        try {
            // Shell execute, because a URL is not something to run - the shell hands it to
            // whatever browser is registered for it.
            using Process? process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        } catch (Exception ex) when (ex is InvalidOperationException
                                         or System.ComponentModel.Win32Exception) {
            MessageBox.Show(
                this,
                $"Could not open {url}: {ex.Message}",
                "Options",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>The GPL text embedded from the repository's LICENSE file.</summary>
    private static string ReadLicenseText() {
        using Stream? stream = typeof(OptionsForm).Assembly.GetManifestResourceStream(LicenseResourceName);

        if (stream is null) return $"The license text is missing from this build. See {LicenseUrl}";

        using var reader = new StreamReader(stream);

        // A multiline TextBox only breaks on CRLF, and the file may be checked out with LF.
        return reader.ReadToEnd().ReplaceLineEndings("\r\n");
    }

    /// <summary>The version without the "+commit" suffix the SDK appends to it.</summary>
    private static string DisplayVersion() => Application.ProductVersion.Split('+')[0];

    private static string ExistingFolder(string path) {
        try {
            string resolved = MergeOptions.ResolvePath(path);
            return Directory.Exists(resolved) ? resolved : AppContext.BaseDirectory;
        } catch (ArgumentException) {
            // An unusable path is exactly when the browser is most useful, so fall back quietly.
            return AppContext.BaseDirectory;
        }
    }

    // --- Settings to controls and back ---

    private void LoadFrom(AppSettings settings) {
        _txtInputFolder.Text = settings.InputFolder;
        _txtAudioFolder.Text = settings.AudioFolder;
        _chkIncludeSubfolders.Checked = settings.IncludeInputSubfolders;
        _txtTempFolder.Text = settings.TempFolder;
        _txtOutputFolder.Text = settings.OutputFolder;
        _txtConcatListFile.Text = settings.ConcatListFile;
        _txtChapterMetadataFile.Text = settings.ChapterMetadataFile;
        _txtFfmpegPath.Text = settings.FfmpegPath;
        _txtFfprobePath.Text = settings.FfprobePath;

        _chkNvenc.Checked = settings.UseNvenc;
        _chkAv1.Checked = settings.UseAv1;
        _txtTargetResolution.Text = settings.TargetResolution;
        _numTargetFps.Value = Clamp(_numTargetFps, settings.TargetFps);
        _numParallelEncodes.Value = Clamp(_numParallelEncodes, settings.MaxParallelEncodes);
        _numAv1Quality.Value = Clamp(_numAv1Quality, settings.Av1Quality);
        _numH264Quality.Value = Clamp(_numH264Quality, settings.H264Quality);
        Select(_cboNvencPreset, settings.NvencPreset);
        Select(_cboAv1Preset, settings.Av1SoftwarePreset);
        Select(_cboX264Preset, settings.X264Preset);
        _txtAudioBitrate.Text = settings.AudioBitrate;
        _numAudioChannels.Value = Clamp(_numAudioChannels, settings.AudioChannels);
        _numAudioSampleRate.Value = Clamp(_numAudioSampleRate, settings.AudioSampleRate);

        _txtOutputName.Text = settings.OutputName;
        _chkTransitionGaps.Checked = settings.InsertTransitionGaps;
        _numMaxAxisSpeed.Value = Clamp(_numMaxAxisSpeed, settings.MaxAxisSpeed);
        _txtVideoExtensions.Text = string.Join(", ", settings.VideoExtensions);

        _chkRememberBounds.Checked = settings.RememberWindowBounds;
        _chkSkipUnscripted.Checked = settings.SkipVideosWithoutScripts;
        _chkRefreshOnLaunch.Checked = settings.RefreshInputOnLaunch;
        _chkWarnOverwrite.Checked = settings.WarnBeforeOverwrite;
        _numLogFontSize.Value = Clamp(_numLogFontSize, (int)Math.Round(settings.LogFontSize));

        SetToolStatus(string.Empty, Theme.MutedText);
    }

    /// <summary>
    /// Writes the controls back. The window bounds are deliberately not touched - they are
    /// owned by the main window, and this dialog only exposes the switch that governs them.
    /// </summary>
    private void ApplyTo(AppSettings settings) {
        settings.InputFolder = _txtInputFolder.Text;
        settings.AudioFolder = _txtAudioFolder.Text;
        settings.IncludeInputSubfolders = _chkIncludeSubfolders.Checked;
        settings.TempFolder = _txtTempFolder.Text;
        settings.OutputFolder = _txtOutputFolder.Text;
        settings.ConcatListFile = _txtConcatListFile.Text;
        settings.ChapterMetadataFile = _txtChapterMetadataFile.Text;
        settings.FfmpegPath = _txtFfmpegPath.Text;
        settings.FfprobePath = _txtFfprobePath.Text;

        settings.UseNvenc = _chkNvenc.Checked;
        settings.UseAv1 = _chkAv1.Checked;
        settings.TargetResolution = _txtTargetResolution.Text;
        settings.TargetFps = (int)_numTargetFps.Value;
        settings.MaxParallelEncodes = (int)_numParallelEncodes.Value;
        settings.Av1Quality = (int)_numAv1Quality.Value;
        settings.H264Quality = (int)_numH264Quality.Value;
        settings.NvencPreset = Selected(_cboNvencPreset);
        settings.Av1SoftwarePreset = Selected(_cboAv1Preset);
        settings.X264Preset = Selected(_cboX264Preset);
        settings.AudioBitrate = _txtAudioBitrate.Text;
        settings.AudioChannels = (int)_numAudioChannels.Value;
        settings.AudioSampleRate = (int)_numAudioSampleRate.Value;

        settings.OutputName = _txtOutputName.Text;
        settings.InsertTransitionGaps = _chkTransitionGaps.Checked;
        settings.MaxAxisSpeed = (int)_numMaxAxisSpeed.Value;
        settings.VideoExtensions = [.. _txtVideoExtensions.Text.Split(
            [',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        settings.RememberWindowBounds = _chkRememberBounds.Checked;
        settings.SkipVideosWithoutScripts = _chkSkipUnscripted.Checked;
        settings.RefreshInputOnLaunch = _chkRefreshOnLaunch.Checked;
        settings.WarnBeforeOverwrite = _chkWarnOverwrite.Checked;
        settings.LogFontSize = (float)_numLogFontSize.Value;
    }

    /// <summary>
    /// Pins a stored value to the control's range. Assigning outside it throws
    /// <see cref="ArgumentOutOfRangeException"/>, which would take the whole load down.
    /// </summary>
    private static decimal Clamp(NumericUpDown control, int value) =>
        Math.Clamp(value, control.Minimum, control.Maximum);

    /// <summary>
    /// Selects <paramref name="value"/>, adding it first when it is not one of the offered
    /// choices - a hand-edited settings file may name a preset this build does not list, and
    /// silently swapping it for another would be worse than showing it.
    /// </summary>
    private static void Select(ComboBox combo, string value) {
        if (!combo.Items.Contains(value)) combo.Items.Add(value);

        combo.SelectedItem = value;
    }

    private static string Selected(ComboBox combo) => combo.SelectedItem as string ?? string.Empty;

    protected override void Dispose(bool disposing) {
        // ToolTip is a component, not a child control, so the form will not collect it.
        if (disposing) _toolTip.Dispose();

        base.Dispose(disposing);
    }
}
