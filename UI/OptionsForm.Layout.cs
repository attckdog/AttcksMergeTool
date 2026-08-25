using AttcksMergeTool.Models;

namespace AttcksMergeTool.UI;

/// <summary>
/// Control declarations and layout construction for <see cref="OptionsForm"/>. Split from
/// the behaviour half the same way <see cref="MainForm"/> is, and for the same reason.
/// </summary>
public sealed partial class OptionsForm
{
    /// <summary>Width of the caption column, sized for the longest label on any page.</summary>
    private const int CaptionWidth = 215;

    /// <summary>
    /// Width of the input column. Fixed rather than proportional: the grid auto-sizes to fit
    /// its rows, and a percentage column inside an auto-sizing table collapses to whatever its
    /// widest control asked for, which is not the same number on every page.
    /// </summary>
    private const int InputWidth = 340;

    private readonly ListBox _categories = new() {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        ForeColor = Color.White
    };

    /// <summary>Holds every page; exactly one is visible at a time.</summary>
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, Padding = new Padding(15, 5, 5, 5) };

    private readonly List<Panel> _pages = [];

    // --- Paths and tools ---

    private readonly TextBox _txtInputFolder = NewTextBox();
    private readonly TextBox _txtTempFolder = NewTextBox();
    private readonly TextBox _txtOutputFolder = NewTextBox();
    private readonly TextBox _txtConcatListFile = NewTextBox();
    private readonly TextBox _txtChapterMetadataFile = NewTextBox();
    private readonly TextBox _txtFfmpegPath = NewTextBox();
    private readonly TextBox _txtFfprobePath = NewTextBox();

    /// <summary>Where the result of a "Test" click is reported, instead of a message box.</summary>
    /// <remarks>
    /// A row in the grid rather than a docked strip: the page scrolls, and a bottom-docked
    /// child of a scrolling panel stays pinned to the viewport instead of to the content.
    /// </remarks>
    private readonly Label _lblToolStatus = new() {
        AutoSize = true,
        ForeColor = Theme.MutedText,
        Margin = new Padding(0, 4, 0, 4)
    };

    // --- Encoding ---

    private readonly CheckBox _chkNvenc = NewCheckBox("Use NVENC hardware encoding");
    private readonly CheckBox _chkAv1 = NewCheckBox("Use AV1 (smaller files; H.264 is more compatible)");
    private readonly TextBox _txtTargetResolution = NewTextBox();
    private readonly NumericUpDown _numTargetFps = NewNumeric(1, 240);
    private readonly NumericUpDown _numParallelEncodes = NewNumeric(1, 16);
    private readonly NumericUpDown _numAv1Quality = NewNumeric(0, 63);
    private readonly NumericUpDown _numH264Quality = NewNumeric(0, 63);
    private readonly ComboBox _cboNvencPreset = NewCombo(["p1", "p2", "p3", "p4", "p5", "p6", "p7"]);
    private readonly ComboBox _cboAv1Preset = NewCombo([.. Enumerable.Range(0, 14).Select(n => n.ToString())]);
    private readonly ComboBox _cboX264Preset = NewCombo([
        "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"
    ]);
    private readonly TextBox _txtAudioBitrate = NewTextBox();
    private readonly NumericUpDown _numAudioChannels = NewNumeric(1, 8);
    private readonly NumericUpDown _numAudioSampleRate = NewNumeric(8000, 192000, increment: 1000);

    // --- Merge and script ---

    private readonly TextBox _txtOutputName = NewTextBox();
    private readonly NumericUpDown _numTransitionMs = NewNumeric(0, 10000, increment: 50);
    private readonly TextBox _txtVideoExtensions = NewTextBox();

    private readonly CheckBox _chkSkipUnscripted = NewCheckBox("Skip videos with no funscript");

    // --- Application ---

    private readonly CheckBox _chkRememberBounds = NewCheckBox("Remember window size and position");
    private readonly CheckBox _chkRefreshOnLaunch = NewCheckBox("Scan the input folder on launch");
    private readonly CheckBox _chkWarnOverwrite = NewCheckBox("Warn before overwriting an existing output");
    private readonly NumericUpDown _numLogFontSize = NewNumeric(6, 24);

    // --- Buttons ---

    private readonly Button _btnOk = NewButton("OK", Theme.ConfirmAction);
    private readonly Button _btnCancel = NewButton("Cancel", Theme.SecondaryAction);
    private readonly Button _btnDefaults = NewButton("Restore Defaults", Theme.DestructiveAction);

    private readonly ToolTip _toolTip = new() {
        AutoPopDelay = 15000,
        InitialDelay = 400,
        ReshowDelay = 400,
        ShowAlways = true
    };

    private void BuildUi() {
        Text = "Options";
        // Wide enough for the Paths page - caption plus input plus a Browse and a Test button -
        // so that page never needs to scroll sideways to reach its buttons.
        Size = new Size(1000, 640);
        MinimumSize = new Size(900, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;

        _categories.BackColor = Theme.Well;
        _categories.SelectedIndexChanged += Categories_SelectedIndexChanged;

        _btnOk.Click += BtnOk_Click;
        _btnDefaults.Click += BtnDefaults_Click;
        _btnCancel.DialogResult = DialogResult.Cancel;

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        AddPage("Paths & Tools", BuildPathsPage());
        AddPage("Encoding", BuildEncodingPage());
        AddPage("Merge", BuildMergePage());
        AddPage("Application", BuildApplicationPage());

        ConfigureToolTips();

        // Dock order matters: fill first, then edges from innermost outward.
        Controls.Add(_pageHost);
        Controls.Add(BuildButtonBar());
        Controls.Add(BuildCategoryRail());

        _categories.SelectedIndex = 0;
    }

    private Panel BuildCategoryRail() {
        var rail = new Panel {
            Dock = DockStyle.Left,
            Width = 170,
            BackColor = Theme.SidePanel,
            Padding = new Padding(10)
        };

        rail.Controls.Add(_categories);

        return rail;
    }

    private FlowLayoutPanel BuildButtonBar() {
        var bar = new FlowLayoutPanel {
            Dock = DockStyle.Bottom,
            Height = 55,
            Padding = new Padding(10),
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Toolbar
        };

        // Right-to-left flow, so the first added button is the rightmost one.
        bar.Controls.Add(_btnOk);
        bar.Controls.Add(_btnCancel);
        bar.Controls.Add(_btnDefaults);

        return bar;
    }

    /// <summary>Registers one page and its rail entry. Only the first stays visible.</summary>
    private void AddPage(string caption, Panel page) {
        page.Dock = DockStyle.Fill;
        page.Visible = _pages.Count == 0;

        _pages.Add(page);
        _pageHost.Controls.Add(page);
        _categories.Items.Add(caption);
    }

    private Panel BuildPathsPage() {
        Panel page = NewPage();
        TableLayoutPanel grid = NewGrid();

        AddRow(grid, "Input folder:", _txtInputFolder, BrowseFolderButton(_txtInputFolder));
        AddRow(grid, "Temp folder:", _txtTempFolder, BrowseFolderButton(_txtTempFolder));
        AddRow(grid, "Output folder:", _txtOutputFolder, BrowseFolderButton(_txtOutputFolder));
        AddHint(grid, "Leave the output folder blank to write beside the application.");

        AddRow(grid, "Concat list file:", _txtConcatListFile);
        AddRow(grid, "Chapter metadata file:", _txtChapterMetadataFile);
        AddHint(grid, "Scratch files. Relative names are resolved against the application folder.");

        AddRow(grid, "ffmpeg:", _txtFfmpegPath, Trailing(
            BrowseExecutableButton(_txtFfmpegPath), TestToolButton(_txtFfmpegPath)));
        AddRow(grid, "ffprobe:", _txtFfprobePath, Trailing(
            BrowseExecutableButton(_txtFfprobePath), TestToolButton(_txtFfprobePath)));
        AddHint(grid, "A bare name such as \"ffmpeg\" is looked up on PATH.");
        AddFullWidthRow(grid, _lblToolStatus);

        page.Controls.Add(grid);

        return page;
    }

    private Panel BuildEncodingPage() {
        Panel page = NewPage();
        TableLayoutPanel grid = NewGrid();

        AddCheckRow(grid, _chkNvenc);
        AddCheckRow(grid, _chkAv1);

        AddRow(grid, "Target resolution:", _txtTargetResolution);
        AddRow(grid, "Target frame rate:", _numTargetFps);
        AddRow(grid, "Parallel encodes:", _numParallelEncodes);

        AddRow(grid, "AV1 quality (CQ/CRF):", _numAv1Quality);
        AddRow(grid, "H.264 quality (CQ/CRF):", _numH264Quality);
        AddHint(grid, "Lower is better quality and a larger file. AV1 defaults to 30, H.264 to 23.");

        AddRow(grid, "NVENC preset:", _cboNvencPreset);
        AddRow(grid, "libsvtav1 preset:", _cboAv1Preset);
        AddRow(grid, "libx264 preset:", _cboX264Preset);
        AddHint(grid, "Only the preset for the encoder the options above select is used.");

        AddRow(grid, "Audio bitrate:", _txtAudioBitrate);
        AddRow(grid, "Audio channels:", _numAudioChannels);
        AddRow(grid, "Audio sample rate:", _numAudioSampleRate);

        page.Controls.Add(grid);

        return page;
    }

    private Panel BuildMergePage() {
        Panel page = NewPage();
        TableLayoutPanel grid = NewGrid();

        AddRow(grid, "Default output name:", _txtOutputName);
        AddHint(grid, "What the main window starts with. No extension and no axis name.");

        AddRow(grid, "Scene transition (ms):", _numTransitionMs);
        AddHint(grid,
            "Keyframes inside this window at the start of a scene collapse to one, so the device "
            + "eases out of the previous scene instead of snapping. 0 disables it.");

        AddCheckRow(grid, _chkSkipUnscripted);
        AddHint(grid,
            "A video with no funscript of the same name is left out of the merge. Uncheck to "
            + "merge it anyway, where it plays as an unscripted stretch of the timeline.");

        AddRow(grid, "Video extensions:", _txtVideoExtensions);
        AddHint(grid, "Comma separated. These decide what the input folder scan treats as a video.");

        page.Controls.Add(grid);

        return page;
    }

    private Panel BuildApplicationPage() {
        Panel page = NewPage();
        TableLayoutPanel grid = NewGrid();

        AddCheckRow(grid, _chkRememberBounds);
        AddCheckRow(grid, _chkRefreshOnLaunch);
        AddCheckRow(grid, _chkWarnOverwrite);
        AddRow(grid, "Log font size:", _numLogFontSize);

        page.Controls.Add(grid);

        return page;
    }

    // --- Row and control construction ---

    private static Panel NewPage() => new() { AutoScroll = true };

    /// <summary>
    /// Caption / input / trailing-buttons, with the caption column fixed so every page's
    /// inputs line up and the input column taking whatever is left.
    /// </summary>
    private static TableLayoutPanel NewGrid() {
        var grid = new TableLayoutPanel {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            // Room for the vertical scrollbar, so a long page does not clip its trailing buttons.
            Padding = new Padding(0, 0, 20, 0)
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CaptionWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, InputWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string caption, Control input, Control? trailing = null) {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(new Label {
            Text = caption,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 6, 0)
        }, 0, row);

        grid.Controls.Add(input, 1, row);

        // The input never spans into the trailing column, even with nothing in it: that column
        // auto-sizes to the widest buttons on the page, so every input on it ends up one width
        // and every Browse button lines up.
        if (trailing is not null) grid.Controls.Add(trailing, 2, row);
    }

    /// <summary>
    /// A checkbox row. Its own label is the caption, so it starts at the caption column and
    /// runs the full width rather than sitting indented under the inputs.
    /// </summary>
    private static void AddCheckRow(TableLayoutPanel grid, CheckBox check) => AddFullWidthRow(grid, check);

    private static void AddFullWidthRow(TableLayoutPanel grid, Control control) {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 3);
    }

    /// <summary>A full-width note under the row it explains.</summary>
    private static void AddHint(TableLayoutPanel grid, string text) {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label {
            Text = text,
            AutoSize = true,
            // Capped at the input column: a page with no trailing buttons is only that wide,
            // and anything longer is clipped rather than wrapped.
            MaximumSize = new Size(InputWidth, 0),
            ForeColor = Theme.MutedText,
            Margin = new Padding(0, 0, 0, 12)
        };

        grid.Controls.Add(hint, 1, row);
        grid.SetColumnSpan(hint, 2);
    }

    private static FlowLayoutPanel Trailing(params Control[] controls) {
        var panel = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
        panel.Controls.AddRange(controls);

        return panel;
    }

    private Button BrowseFolderButton(TextBox target) {
        Button button = NewSmallButton("Browse...");
        button.Click += (_, _) => BrowseForFolder(target);

        return button;
    }

    private Button BrowseExecutableButton(TextBox target) {
        Button button = NewSmallButton("Browse...");
        button.Click += (_, _) => BrowseForExecutable(target);

        return button;
    }

    private Button TestToolButton(TextBox target) {
        Button button = NewSmallButton("Test");
        button.Click += async (_, _) => await TestToolAsync(target);

        return button;
    }

    private static TextBox NewTextBox() => new() {
        Dock = DockStyle.Fill,
        BackColor = Theme.Field,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 4, 6, 4)
    };

    private static NumericUpDown NewNumeric(int minimum, int maximum, int increment = 1) => new() {
        Minimum = minimum,
        Maximum = maximum,
        Increment = increment,
        Width = 110,
        Anchor = AnchorStyles.Left,
        BackColor = Theme.Field,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 4, 6, 4)
    };

    private static ComboBox NewCombo(string[] items) {
        var combo = new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 140,
            Anchor = AnchorStyles.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 4, 6, 4)
        };

        combo.Items.AddRange(items);

        return combo;
    }

    private static CheckBox NewCheckBox(string caption) => new() {
        Text = caption,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 6, 6)
    };

    private static Button NewButton(string caption, Color background) => new() {
        Text = caption,
        AutoSize = true,
        ForeColor = Color.White,
        BackColor = background,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(6, 0, 0, 0),
        Padding = new Padding(10, 4, 10, 4)
    };

    private static Button NewSmallButton(string caption) => new() {
        Text = caption,
        AutoSize = true,
        ForeColor = Color.White,
        BackColor = Theme.SecondaryAction,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(0, 4, 4, 4)
    };

    private void ConfigureToolTips() {
        _toolTip.SetToolTip(_txtInputFolder, "The folder scanned for source videos and funscripts.");
        _toolTip.SetToolTip(_txtTempFolder, "Where the normalized per-video segments are written before they are joined.");
        _toolTip.SetToolTip(_txtOutputFolder, "Where the merged video and funscript are written. Blank means beside the application.");
        _toolTip.SetToolTip(_txtConcatListFile, "Scratch file listing the segments in concat order. Deleted when the run ends, whether it succeeded or not.");
        _toolTip.SetToolTip(_txtChapterMetadataFile, "Scratch file holding the FFMETADATA chapter markers embedded into the merged video.");
        _toolTip.SetToolTip(_txtFfmpegPath, "The ffmpeg executable used for encoding and concatenation. A bare name is looked up on PATH.");
        _toolTip.SetToolTip(_txtFfprobePath, "The ffprobe executable used to measure durations. A bare name is looked up on PATH.");

        _toolTip.SetToolTip(_chkNvenc, "Uses NVIDIA hardware acceleration for significantly faster video encoding. Requires a compatible NVIDIA GPU.");
        _toolTip.SetToolTip(_chkAv1, "Encodes the merged video in AV1 format (smaller file size, excellent quality). Uncheck to use standard H.264 for broader device support.");
        _toolTip.SetToolTip(_txtTargetResolution, "The frame size every segment is scaled and padded to, as width:height. All segments must share one size for the concat to work.");
        _toolTip.SetToolTip(_numTargetFps, "The frame rate every segment is converted to.");
        _toolTip.SetToolTip(_numParallelEncodes, "How many ffmpeg encodes run at once. Raise it to use more of the CPU or GPU; lower it if the machine becomes unusable during a run.");
        _toolTip.SetToolTip(_numAv1Quality, "Constant-quality value for av1_nvenc and libsvtav1. Lower is better quality and a larger file.");
        _toolTip.SetToolTip(_numH264Quality, "Constant-quality value for h264_nvenc and libx264. Lower is better quality and a larger file.");
        _toolTip.SetToolTip(_cboNvencPreset, "Speed/quality tradeoff for the NVENC encoders: p1 is fastest, p7 is the best quality.");
        _toolTip.SetToolTip(_cboAv1Preset, "Speed/quality tradeoff for software AV1: 0 is slowest and best, 13 is fastest.");
        _toolTip.SetToolTip(_cboX264Preset, "Speed/quality tradeoff for software H.264, from ultrafast to veryslow.");
        _toolTip.SetToolTip(_txtAudioBitrate, "AAC bitrate for every segment, in ffmpeg's form - for example 192k.");
        _toolTip.SetToolTip(_numAudioChannels, "Channel count every segment's audio is downmixed to. Mismatched channel counts break the concat.");
        _toolTip.SetToolTip(_numAudioSampleRate, "Sample rate every segment's audio is resampled to. Mismatched rates break the concat.");

        _toolTip.SetToolTip(_txtOutputName, "The base filename the main window starts with. Do not include extensions or axis names.");
        _toolTip.SetToolTip(_numTransitionMs, "How long the eased seam at the start of each scene lasts. Keyframes inside it collapse to a single point.");
        _toolTip.SetToolTip(_txtVideoExtensions, "Comma separated list of the file extensions treated as input videos, for example .mp4, .mkv.");

        _toolTip.SetToolTip(_chkSkipUnscripted, "Leaves a video out of the merge when no funscript shares its name. Each one that is skipped is named in the log. Unchecked, it is merged and plays unscripted.");

        _toolTip.SetToolTip(_chkRememberBounds, "Reopens the window at the size and position it was last closed at.");
        _toolTip.SetToolTip(_chkRefreshOnLaunch, "Scans the input folder as soon as the app opens. Turn off if the folder is slow to read.");
        _toolTip.SetToolTip(_chkWarnOverwrite, "Asks for confirmation when a run is about to replace an existing merged video or script.");
        _toolTip.SetToolTip(_numLogFontSize, "Point size of the job log's monospaced font.");

        _toolTip.SetToolTip(_btnDefaults, "Resets every option on every page back to its default. Nothing is saved until OK.");
    }
}
