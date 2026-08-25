using System.Diagnostics;
using AttcksMergeTool.Models;
using AttcksMergeTool.Services;

namespace AttcksMergeTool.UI;

/// <summary>
/// The application window. Owns the input file list and the trim settings, then hands
/// a snapshot of them to <see cref="MergeCoordinator"/> when a job starts. All merge
/// logic lives in the service layer; this class only wires controls to it.
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly List<VideoSegmentSettings> _videoSettings = [];
    private readonly IJobLogger _logger;

    /// <summary>
    /// What the list shows about each file beyond its name, keyed by path. Filled in by a
    /// background scan rather than during the refresh: reading a duration means launching
    /// ffprobe, and counting axes means parsing every funscript in the folder.
    /// </summary>
    private readonly Dictionary<string, SceneDetails> _details = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cancels the background scan above. A refresh starts a new one, and the old one is
    /// stopped rather than left to write rows describing files that are no longer listed.
    /// </summary>
    private CancellationTokenSource? _detailScan;

    /// <summary>
    /// The persisted settings, and the only mutable copy of them. Replaced wholesale when the
    /// options dialog returns, so nothing can hold a stale half-edited reference.
    /// </summary>
    private AppSettings _settings;

    /// <summary>
    /// The log font, when it is one we built rather than <see cref="Theme.LogFont"/>. Held so
    /// it can be disposed; disposing the shared default would break every later window.
    /// </summary>
    private Font? _logFont;

    /// <summary>Cancels the running job. Non-null only while a job is in flight.</summary>
    /// <remarks>
    /// Doubles as the "is a job running" flag, which is why it is nulled in the same
    /// <c>finally</c> that re-enables the toolbar.
    /// </remarks>
    private CancellationTokenSource? _jobCancellation;

    /// <summary>
    /// Set when the window was closed mid-job, so the close can be replayed once the job
    /// has unwound instead of tearing the form down underneath it.
    /// </summary>
    private bool _closePending;

    public MainForm() {
        // First: everything below either reads the settings or scans the folder they name.
        _settings = SettingsStore.Default.Load();

        BuildUi();
        _logger = new RichTextBoxLogger(_txtLog);

        ApplySettingsToUi();

        if (_settings.RefreshInputOnLaunch) LoadInputFiles();
    }

    /// <summary>
    /// Resolved against the executable's folder rather than the working directory, so the
    /// browser and the job always agree on which folder is the input folder.
    /// </summary>
    private string InputFolder => MergeOptions.ResolvePath(_settings.InputFolder);

    /// <summary>
    /// Where the merged video and script are written. Blank means beside the executable,
    /// the same reading <see cref="MergeOptions.OutputFolder"/> gives it.
    /// </summary>
    private string OutputFolder => string.IsNullOrWhiteSpace(_settings.OutputFolder)
        ? AppContext.BaseDirectory
        : MergeOptions.ResolvePath(_settings.OutputFolder);

    /// <summary>
    /// Shows a folder in the file browser, creating it first if it is not there yet.
    /// </summary>
    /// <remarks>
    /// Created rather than reported as missing: both folders are ones the job would create
    /// on its own anyway, and an empty window is a clearer answer to "where do my files go"
    /// than a button that appears to do nothing.
    /// </remarks>
    /// <param name="description">How the folder is named in the log, for a failure.</param>
    private void OpenFolder(string path, string description) {
        try {
            if (!Directory.Exists(path)) {
                Directory.CreateDirectory(path);
                _logger.Log($"{description} '{path}' did not exist yet. Created it.", LogLevel.Warning);
            }

            // Shell execute, because the path is a folder rather than something to run - it is
            // the shell that knows to hand it to the file browser.
            using Process? browser = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        } catch (Exception ex) when (ex is IOException
                                         or UnauthorizedAccessException
                                         or ArgumentException
                                         or NotSupportedException
                                         or System.ComponentModel.Win32Exception) {
            _logger.Log($"Could not open {description.ToLowerInvariant()} '{path}': {ex.Message}", LogLevel.Error);
        }
    }

    // --- Input file list ---

    /// <summary>
    /// Rescans the input folder, keeping the trim settings of files that are still there
    /// and dropping the ones that have gone.
    /// </summary>
    private void LoadInputFiles() {
        if (!Directory.Exists(InputFolder)) {
            // Silently leaving the list empty reads as "no videos found" rather than "there is
            // nowhere to look"; the folder itself is created when a job starts.
            _logger.Log($"Input folder '{InputFolder}' does not exist yet.", LogLevel.Warning);
            return;
        }

        List<string> videoFiles = MediaFileScanner.FindVideos(InputFolder, _settings.VideoExtensions);

        _videoSettings.RemoveAll(s => !videoFiles.Contains(s.FilePath));

        foreach (string file in videoFiles) {
            if (!_videoSettings.Any(s => s.FilePath == file)) {
                _videoSettings.Add(new VideoSegmentSettings { FilePath = file });
            }
        }

        _details.Keys.Where(path => !videoFiles.Contains(path)).ToList().ForEach(path => _details.Remove(path));

        RefreshVideoList();
        StartDetailScan();
    }

    /// <param name="selectIndex">
    /// The row to leave selected, or null to keep the one that is selected now. Passed by the
    /// reorder buttons, which move the selected video to a row it was not on before.
    /// </param>
    private void RefreshVideoList(int? selectIndex = null) {
        int selectedIndex = selectIndex ?? SelectedVideoIndex;

        _lstVideos.BeginUpdate();
        _lstVideos.Items.Clear();

        foreach (VideoSegmentSettings settings in _videoSettings) {
            _lstVideos.Items.Add(BuildRow(settings));
        }

        _lstVideos.EndUpdate();

        // Now that the rows are in. Filling the list is what makes its vertical scrollbar
        // appear, which narrows the client area without resizing the control - so a name
        // column sized at the last resize would overhang by the scrollbar's width and put a
        // horizontal scrollbar under the list.
        ResizeNameColumn();

        if (selectedIndex >= 0 && selectedIndex < _lstVideos.Items.Count) {
            _lstVideos.Items[selectedIndex].Selected = true;
            _lstVideos.Items[selectedIndex].EnsureVisible();
        }
    }

    /// <summary>The selected row, or -1 when nothing is selected.</summary>
    private int SelectedVideoIndex => _lstVideos.SelectedIndices.Count > 0 ? _lstVideos.SelectedIndices[0] : -1;

    /// <summary>
    /// The settings behind the selected row, or null when nothing is selected. Taken from the
    /// row's tag rather than by index, so it cannot drift from what the row displays.
    /// </summary>
    private VideoSegmentSettings? SelectedVideo =>
        SelectedVideoIndex >= 0 ? _lstVideos.Items[SelectedVideoIndex].Tag as VideoSegmentSettings : null;

    // --- Row contents ---

    /// <summary>Cell text for a detail the background scan has not reached yet.</summary>
    private const string PendingCell = "...";

    /// <summary>Cell text for a detail that could not be read - no ffprobe on PATH, say.</summary>
    private const string UnknownCell = "?";

    /// <summary>Marks a length as the trimmed length rather than the file's own.</summary>
    private const string TrimMarker = "✂ ";

    /// <summary>
    /// A list row for <paramref name="settings"/>, filled in from whatever details have been
    /// read so far. The settings object rides along as the row's tag - it, not the row index,
    /// is what the trim panel and the reorder buttons work from.
    /// </summary>
    private ListViewItem BuildRow(VideoSegmentSettings settings) {
        // Per-subitem colours, so one unread cell can be greyed without greying the row.
        var item = new ListViewItem(settings.FileName) { Tag = settings, UseItemStyleForSubItems = false };

        item.SubItems.Add(string.Empty);
        item.SubItems.Add(string.Empty);
        item.SubItems.Add(string.Empty);

        UpdateRow(item, settings);

        return item;
    }

    /// <summary>Rewrites every cell of <paramref name="item"/> from the details read so far.</summary>
    private void UpdateRow(ListViewItem item, VideoSegmentSettings settings) {
        SetCell(item, NameColumn, settings.FileName, Theme.Text);

        if (_details.GetValueOrDefault(settings.FilePath) is not { } details) {
            // Not read yet. Saying so beats showing a zero, which would look like an answer.
            SetCell(item, LengthColumn, PendingCell, Theme.MutedText);
            SetCell(item, ScriptColumn, PendingCell, Theme.MutedText);
            SetCell(item, AxesColumn, PendingCell, Theme.MutedText);

            return;
        }

        int? lengthMs = EffectiveDurationMs(settings, details.DurationMs);

        SetCell(
            item,
            LengthColumn,
            lengthMs is null ? UnknownCell : LengthTextOf(settings, lengthMs.Value),
            lengthMs is null ? Theme.MutedText : settings.UseTrim ? Theme.ConfirmAction : Theme.Text);

        // Red rather than merely muted: with the skip option on - and it is on by default -
        // a No is the reason that video will not be in the output at all.
        SetCell(
            item,
            ScriptColumn,
            details.HasScript ? "Yes" : "No",
            details.HasScript ? Theme.Text : Theme.MissingValue);

        SetCell(
            item,
            AxesColumn,
            details.AxisCount > 0 ? details.AxisCount.ToString() : "-",
            details.AxisCount > 0 ? Theme.Text : Theme.MutedText);
    }

    private static void SetCell(ListViewItem item, int column, string text, Color foreground) {
        item.SubItems[column].Text = text;
        item.SubItems[column].ForeColor = foreground;
    }

    /// <summary>
    /// The length cell, marked when what it shows is what a trim leaves rather than the whole
    /// file - the number alone would otherwise look like the file had been measured wrong.
    /// </summary>
    private static string LengthTextOf(VideoSegmentSettings settings, int lengthMs) =>
        (settings.UseTrim ? TrimMarker : string.Empty) + FormatDuration(lengthMs);

    /// <summary>h:mm:ss for an hour or more, m:ss below that.</summary>
    private static string FormatDuration(int milliseconds) {
        TimeSpan span = TimeSpan.FromMilliseconds(milliseconds);

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>
    /// How much of the file the merge would actually use: its own duration, or what the
    /// configured trim leaves of it. Null when neither is known.
    /// </summary>
    private static int? EffectiveDurationMs(VideoSegmentSettings settings, int? durationMs) {
        if (!settings.UseTrim) return durationMs;

        TrimWindow trim = TrimWindow.FromSeconds(settings.StartTime, settings.EndTime);

        // An end of zero means "run to the end of the source", which is only a length once
        // the source's own duration is known.
        int endMs = trim.EndMs > trim.StartMs ? trim.EndMs : durationMs ?? 0;

        if (durationMs is { } total) endMs = Math.Min(endMs, total);

        if (endMs > trim.StartMs) return endMs - trim.StartMs;

        return durationMs is null ? null : 0;
    }

    // --- Background detail scan ---

    /// <summary>
    /// Starts reading the durations and axis counts the list shows, filling each row in as
    /// its answer arrives.
    /// </summary>
    /// <remarks>
    /// Off the UI thread and cancellable, because it launches an ffprobe per video and parses
    /// every funscript in the folder; doing that inline would freeze the window for as long as
    /// it takes. It is also purely cosmetic - a merge reads all of it again for itself - so a
    /// scan that is cancelled or fails costs nothing but the columns it did not fill in.
    /// </remarks>
    private void StartDetailScan() {
        CancelDetailScan();

        if (_videoSettings.Count == 0) return;

        // Built once for the whole scan rather than per row: classifying the scenes means
        // looking at every funscript in the folder next to every other one.
        Dictionary<string, SceneScripts> scenes = SceneScriptIndex
            .Build(InputFolder, _settings.VideoExtensions)
            .ToDictionary(scene => scene.Name, StringComparer.OrdinalIgnoreCase);

        var probe = new FFprobe(ProcessRunner.Default, MergeOptions.FromSettings(_settings).FfprobePath);

        _detailScan = new CancellationTokenSource();

        // Deliberately not awaited - the rows fill themselves in as it goes.
        _ = ScanDetailsAsync(scenes, probe, _detailScan.Token);
    }

    private async Task ScanDetailsAsync(
        Dictionary<string, SceneScripts> scenes,
        IMediaProbe probe,
        CancellationToken cancellationToken) {
        // Snapshotted: the list can be reordered while this runs, so a row is found again by
        // path when its details land rather than by the position it held at the start.
        string[] paths = [.. _videoSettings.Select(settings => settings.FilePath)];

        try {
            foreach (string path in paths) {
                SceneScripts? scene = scenes.GetValueOrDefault(Path.GetFileNameWithoutExtension(path));

                // Task.Run so the JSON parsing runs on the pool; the continuation comes back to
                // the UI thread, which is what makes touching the row below safe.
                SceneDetails details = await Task.Run(
                    () => SceneDetailsReader.ReadAsync(path, scene, probe, cancellationToken),
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                ApplyDetails(path, details);
            }
        } catch (OperationCanceledException) {
            // A newer scan replaced this one, or the window is closing.
        } catch (Exception exception) {
            _logger.Log($"Could not read the video details: {exception.Message}", LogLevel.Warning);
        }
    }

    /// <summary>Records what was read and rewrites just that row.</summary>
    private void ApplyDetails(string filePath, SceneDetails details) {
        _details[filePath] = details;

        if (IsDisposed) return;

        foreach (ListViewItem item in _lstVideos.Items) {
            if (item.Tag is not VideoSegmentSettings settings
                || !string.Equals(settings.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            UpdateRow(item, settings);
            break;
        }
    }

    private void CancelDetailScan() {
        _detailScan?.Cancel();
        _detailScan?.Dispose();
        _detailScan = null;
    }

    // --- Merge order ---

    /// <summary>
    /// The list's order is the merge order: the videos are concatenated top to bottom and the
    /// funscripts follow their videos, so moving a row here moves the whole scene - video,
    /// script and chapter - to that position in the output.
    /// </summary>
    private void MoveSelectedVideo(int offset) {
        int index = SelectedVideoIndex;
        int target = index + offset;

        // Silently ignored at the ends rather than enabling the buttons per selection: the
        // click is a no-op either way, and buttons that keep still are easier to aim at.
        if (index < 0 || target < 0 || target >= _videoSettings.Count) return;

        VideoSegmentSettings moved = _videoSettings[index];
        _videoSettings.RemoveAt(index);
        _videoSettings.Insert(target, moved);

        RefreshVideoList(target);
    }

    private void BtnShuffleOrder_Click(object? sender, EventArgs e) {
        VideoSegmentSettings[] shuffled = [.. _videoSettings];
        Random.Shared.Shuffle(shuffled);

        ApplyOrder(shuffled);
        _logger.Log($"Merge order shuffled ({_videoSettings.Count} videos).");
    }

    /// <summary>
    /// Sorts by filename, which is also the order a fresh scan produces.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, unlike <see cref="MediaFileScanner.FindVideos"/>: a button labelled
    /// Alphabetical that files "Zebra" ahead of "apple" is not doing what it says. The ordinal
    /// tiebreak keeps two names differing only in case in a fixed order rather than whichever
    /// one the sort happened to see first.
    /// </remarks>
    private void BtnSortOrder_Click(object? sender, EventArgs e) {
        ApplyOrder([.. _videoSettings
            .OrderBy(settings => settings.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(settings => settings.FileName, StringComparer.Ordinal)]);

        _logger.Log($"Merge order sorted alphabetically ({_videoSettings.Count} videos).");
    }

    /// <summary>
    /// Adopts a wholesale reordering, keeping the selected video selected wherever it has
    /// landed so the trim panel still describes the file the user was looking at.
    /// </summary>
    private void ApplyOrder(IReadOnlyList<VideoSegmentSettings> ordered) {
        VideoSegmentSettings? selected = SelectedVideo;

        _videoSettings.Clear();
        _videoSettings.AddRange(ordered);

        RefreshVideoList(selected is null ? null : _videoSettings.IndexOf(selected));
    }

    // --- Event handlers ---

    private void LstVideos_SelectedIndexChanged(object? sender, EventArgs e) {
        if (SelectedVideo is not { } settings) return;

        _chkEnableTrim.Checked = settings.UseTrim;
        _numStart.Value = ClampToRange(_numStart, settings.StartTime);
        _numEnd.Value = ClampToRange(_numEnd, settings.EndTime);
    }

    /// <summary>
    /// Pins a stored trim to the control's range. Assigning a value outside it throws
    /// <see cref="ArgumentOutOfRangeException"/>, which would take the whole selection
    /// handler down.
    /// </summary>
    private static decimal ClampToRange(NumericUpDown control, double seconds) {
        if (double.IsNaN(seconds)) return control.Minimum;

        return (decimal)Math.Clamp(seconds, (double)control.Minimum, (double)control.Maximum);
    }

    private void BtnApplyTrim_Click(object? sender, EventArgs e) {
        if (SelectedVideo is not { } settings) return;

        settings.UseTrim = _chkEnableTrim.Checked;
        settings.StartTime = (double)_numStart.Value;
        settings.EndTime = (double)_numEnd.Value;

        RefreshVideoList();
        _logger.Log($"Saved settings for {settings.FileName}");
    }

    private async void BtnStart_Click(object? sender, EventArgs e) {
        MergeOptions options = ReadOptions();

        if (!ConfirmOverwrite(options)) return;

        SetJobRunning(true);
        _txtLog.Clear();

        // Reset the style too: a previous run that reached the concat left it on marquee, and
        // the new run would open animating before it had done anything.
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = 0;

        _jobCancellation = new CancellationTokenSource();

        try {
            // Deep copy, not ToList: the list itself is frozen either way, but the settings
            // objects inside it stay reachable from the UI thread, which can still edit them
            // mid-job through Apply to Selected Video.
            var coordinator = new MergeCoordinator(
                _logger, options, [.. _videoSettings.Select(s => s.Clone())]);

            await coordinator.RunAsync(new Progress<MergeProgress>(ApplyProgress), _jobCancellation.Token);
        } catch (OperationCanceledException) {
            _logger.Log($"{Environment.NewLine}Job cancelled.", LogLevel.Warning);
        } catch (Exception ex) {
            _logger.Log($"{Environment.NewLine}An error occurred: {ex.Message}", LogLevel.Error);
        } finally {
            _jobCancellation.Dispose();
            _jobCancellation = null;

            _progressBar.Visible = false;
            SetJobRunning(false);

            // The job is what was holding the window open - see OnFormClosing.
            if (_closePending) Close();
        }
    }

    private void BtnCancel_Click(object? sender, EventArgs e) => RequestCancellation();

    /// <summary>
    /// Opens the settings dialog and adopts what it returns. Saving here rather than on exit
    /// is what makes the settings survive a crash mid-job.
    /// </summary>
    private void BtnOptions_Click(object? sender, EventArgs e) {
        // The output name lives in the toolbar, so carry the typed one in - otherwise opening
        // Options and pressing OK would silently revert it to whatever was last saved.
        _settings.OutputName = MergeOptions.NormalizeOutputName(_txtOutputName.Text);

        using var dialog = new OptionsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        AppSettings previous = _settings;
        _settings = dialog.Result;

        SaveSettings();
        ApplySettingsToUi();

        // Only rescan when the answer could have changed; a rescan drops the trim settings of
        // any file that is no longer there.
        if (!ScansTheSameFiles(previous, _settings)) {
            LoadInputFiles();
        } else if (!string.Equals(previous.FfprobePath, _settings.FfprobePath, StringComparison.OrdinalIgnoreCase)) {
            // Same files, but a different ffprobe reads their durations - and a corrected path
            // is the usual reason a column full of question marks would be corrected.
            StartDetailScan();
        }
    }

    private static bool ScansTheSameFiles(AppSettings before, AppSettings after) =>
        string.Equals(before.InputFolder, after.InputFolder, StringComparison.OrdinalIgnoreCase)
        && before.VideoExtensions.SequenceEqual(after.VideoExtensions);

    /// <summary>
    /// Confirms a run that would replace outputs already on disk. Both paths are checked, so
    /// the prompt can say exactly what is at stake.
    /// </summary>
    private bool ConfirmOverwrite(MergeOptions options) {
        if (!_settings.WarnBeforeOverwrite) return true;

        List<string> existing = new[] { options.OutputVideoPath, options.OutputScriptPath }
            .Where(File.Exists)
            .ToList();

        if (existing.Count == 0) return true;

        return MessageBox.Show(
            this,
            $"This run will overwrite:{Environment.NewLine}{Environment.NewLine}"
            + $"{string.Join(Environment.NewLine, existing)}{Environment.NewLine}{Environment.NewLine}"
            + "Continue?",
            "Overwrite existing output?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    /// <summary>
    /// Signals the running job to stop. Cancellation propagates through the token the
    /// coordinator was started with, which kills the in-flight ffmpeg process tree; the
    /// job is not finished until <see cref="BtnStart_Click"/> reaches its <c>finally</c>.
    /// </summary>
    private void RequestCancellation() {
        if (_jobCancellation is null || _jobCancellation.IsCancellationRequested) return;

        // Disabled here rather than in SetJobRunning: the button has done its work, but the
        // job stays in flight until the current step unwinds, and a second click is a no-op.
        _btnCancel.Enabled = false;
        _logger.Log($"{Environment.NewLine}Cancelling - stopping the current step...", LogLevel.Warning);

        _jobCancellation.Cancel();
    }

    /// <summary>
    /// Closing mid-job would dispose the log the worker threads are still writing to, so
    /// the close is deferred: cancel first, then let the job's completion path close us.
    /// Settings are saved here rather than after the close, while the window is still alive
    /// to be measured and the log is still there to report a failure.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (_jobCancellation is not null) {
            e.Cancel = true;
            _closePending = true;
            RequestCancellation();
        }

        base.OnFormClosing(e);

        // Only once the close is really going ahead. The deferred path above comes back
        // through here when the job has unwound, and a handler may have cancelled too.
        if (e.Cancel) return;

        CancelDetailScan();

        if (_settings.RememberWindowBounds) CaptureWindowBounds();

        // Kept whether or not the window bounds are: the panel width is a layout preference in
        // its own right, and one the user set by dragging rather than by opening a dialog.
        if (_split.Panel2.Width > 0) _settings.SidePanelWidth = _split.Panel2.Width;

        _settings.OutputName = MergeOptions.NormalizeOutputName(_txtOutputName.Text);
        SaveSettings();
    }

    // --- Job plumbing ---

    /// <summary>
    /// Freezes the settings into the snapshot the job reads, taking the output name from the
    /// toolbar because that is the one control still editable outside the options dialog.
    /// </summary>
    private MergeOptions ReadOptions() {
        _settings.OutputName = MergeOptions.NormalizeOutputName(_txtOutputName.Text);

        return MergeOptions.FromSettings(_settings);
    }

    private void SetJobRunning(bool running) {
        _btnStart.Enabled = !running;
        _btnCancel.Enabled = running;
        _btnRefresh.Enabled = !running;

        // The running job holds its own copy of the order, so rearranging the list mid-job
        // would not change the output - only mislead about what is being merged.
        _btnMoveUp.Enabled = !running;
        _btnMoveDown.Enabled = !running;
        _btnShuffleOrder.Enabled = !running;
        _btnSortOrder.Enabled = !running;

        // Editing paths mid-job would leave the running snapshot pointing at the old ones,
        // and the log reporting folders the job is not using.
        _btnOptions.Enabled = !running;
    }

    // --- Settings plumbing ---

    /// <summary>Pushes the settings that have a visible effect into the controls.</summary>
    private void ApplySettingsToUi() {
        _txtOutputName.Text = _settings.OutputName;

        if (Math.Abs(_txtLog.Font.SizeInPoints - _settings.LogFontSize) < 0.01F) return;

        Font replacement = Theme.LogFontOfSize(_settings.LogFontSize);
        _txtLog.Font = replacement;

        // Only ever the one we made ourselves - Theme.LogFont is shared and must outlive us.
        _logFont?.Dispose();
        _logFont = replacement;
    }

    /// <summary>
    /// Writes the settings out, reporting a failure to the log rather than throwing. Losing a
    /// preference is an annoyance; taking the window down over it would not be.
    /// </summary>
    private void SaveSettings() {
        if (SettingsStore.Default.TrySave(_settings, out string? error)) return;

        _logger.Log(
            $"Could not save settings to {SettingsStore.Default.FilePath}: {error}",
            LogLevel.Warning);
    }

    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);

        RestoreWindowBounds();
        ApplySidePanelWidth();
    }

    /// <summary>
    /// Restores the side panel to the width the splitter was last left at, clamped to what
    /// the window can actually give it.
    /// </summary>
    /// <remarks>
    /// After the window bounds, never before: the distance is measured against the container
    /// width, so applying it to the size the form was built with would put the splitter
    /// somewhere else entirely once the restored bounds took effect.
    /// </remarks>
    private void ApplySidePanelWidth() {
        int available = _split.Width - _split.SplitterWidth;

        // Too narrow to honour both minimums. Leaving the splitter where it is beats throwing
        // on a distance the container would reject.
        if (available < _split.Panel1MinSize + _split.Panel2MinSize) return;

        int width = Math.Clamp(
            _settings.SidePanelWidth, _split.Panel2MinSize, available - _split.Panel1MinSize);

        _split.SplitterDistance = available - width;
    }

    private void RestoreWindowBounds() {
        if (!_settings.RememberWindowBounds || _settings.WindowWidth <= 0 || _settings.WindowHeight <= 0) {
            return;
        }

        var bounds = new Rectangle(
            _settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);

        // A position saved on a monitor that is no longer attached would put the window
        // somewhere the user cannot drag it back from, so only the size survives that case.
        if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))) {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        } else {
            Size = bounds.Size;
        }

        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>
    /// Records the window geometry to restore next launch.
    /// </summary>
    /// <remarks>
    /// <see cref="Control.Bounds"/> while the window is normal, and
    /// <see cref="Form.RestoreBounds"/> only when it is not. Neither alone is right:
    /// <c>Bounds</c> while maximized is the whole screen, which would pin the window to the
    /// monitor's edges next launch, but <c>RestoreBounds</c> is only tracked once the window
    /// has changed state, and before that it still reports the size this form was built with
    /// rather than whatever the user dragged it to.
    /// </remarks>
    private void CaptureWindowBounds() {
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

        _settings.WindowX = bounds.X;
        _settings.WindowY = bounds.Y;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
    }


    /// <summary>
    /// Applies a progress tick. Invoked through <see cref="Progress{T}"/>, so this always
    /// runs on the UI thread even though the reports come from worker threads.
    /// </summary>
    private void ApplyProgress(MergeProgress progress) {
        _progressBar.Style = progress.IsIndeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        _progressBar.Visible = true;

        if (progress.IsIndeterminate) return;

        _progressBar.Maximum = Math.Max(progress.Total, 1);
        _progressBar.Value = Math.Min(progress.Completed, _progressBar.Maximum);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            // ToolTip is a component, not a child control, so the form will not collect it.
            _toolTip.Dispose();
            _detailScan?.Dispose();
            _jobCancellation?.Dispose();
            _logFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}
