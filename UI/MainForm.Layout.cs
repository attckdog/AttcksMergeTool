using AttcksMergeTool.Models;

namespace AttcksMergeTool.UI;

/// <summary>
/// Control declarations and layout construction for <see cref="MainForm"/>. Kept in
/// its own partial file so the behaviour half stays readable; there is no designer.
/// </summary>
public sealed partial class MainForm
{
    // Top toolbar. The encoding switches used to live here; they are settings now, so they
    // sit in OptionsForm where they persist instead of resetting on every launch.
    private readonly TextBox _txtOutputName = new() { Text = MergeOptions.DefaultOutputName, Width = 200, Margin = new Padding(0, 3, 15, 0) };
    private readonly Button _btnStart = new() { Text = "Start Merge", ForeColor = Color.White, FlatStyle = FlatStyle.Flat, AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "Cancel", ForeColor = Color.White, FlatStyle = FlatStyle.Flat, AutoSize = true, Enabled = false, Margin = new Padding(5, 3, 5, 0) };
    private readonly Button _btnRefresh = new() { Text = "Refresh Files", ForeColor = Color.White, FlatStyle = FlatStyle.Flat, AutoSize = true, Margin = new Padding(10, 3, 5, 0) };
    private readonly Button _btnOpenInput = new() { Text = "Open Input Folder", ForeColor = Color.White, FlatStyle = FlatStyle.Flat, AutoSize = true, Margin = new Padding(5, 3, 5, 0) };
    private readonly Button _btnOpenOutput = new() { Text = "Open Export Folder", ForeColor = Color.White, FlatStyle = FlatStyle.Flat, AutoSize = true, Margin = new Padding(5, 3, 5, 0) };
    private readonly Button _btnOptions = new() { Text = "Options...", ForeColor = Color.White, FlatStyle = FlatStyle.Flat, AutoSize = true, Margin = new Padding(5, 3, 5, 0) };

    /// <summary>
    /// Splits the log from the side panel. The side panel is the fixed half, so widening the
    /// window gives the extra room to the log rather than to a panel the user already sized.
    /// </summary>
    private readonly SplitContainer _split = new() {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        FixedPanel = FixedPanel.Panel2,
        SplitterWidth = 6
    };

    // Side panel: video list, merge order and trim settings
    private readonly ListView _lstVideos = new DoubleBufferedListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        OwnerDraw = true,
        BorderStyle = BorderStyle.FixedSingle,
        ForeColor = Color.White
    };

    // Merge order. The list's order is the concat order, so these decide where each scene
    // lands in the merged video and its script.
    private readonly Button _btnMoveUp = new() { Text = "Move Up", ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnMoveDown = new() { Text = "Move Down", ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnShuffleOrder = new() { Text = "Random", ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnSortOrder = new() { Text = "Alphabetical", ForeColor = Color.White, FlatStyle = FlatStyle.Flat };

    private readonly CheckBox _chkEnableTrim = new() { Text = "Enable Trimming", Dock = DockStyle.Top, Height = 30 };
    private readonly NumericUpDown _numStart = new() { Dock = DockStyle.Left, Width = 120, DecimalPlaces = 3, Maximum = MaxTrimSeconds };
    private readonly NumericUpDown _numEnd = new() { Dock = DockStyle.Left, Width = 120, DecimalPlaces = 3, Maximum = MaxTrimSeconds };

    // Output
    private readonly RichTextBox _txtLog = new() { Dock = DockStyle.Fill, ForeColor = Color.LightGray, ReadOnly = true, Font = Theme.LogFont };
    private readonly ProgressBar _progressBar = new() { Dock = DockStyle.Bottom, Height = 25, Style = ProgressBarStyle.Continuous, Visible = false };

    private readonly ToolTip _toolTip = new() {
        AutoPopDelay = 10000,
        InitialDelay = 500,
        ReshowDelay = 500,
        ShowAlways = true
    };

    /// <summary>
    /// The size the window opens at when there is nothing saved to restore. Twice what it
    /// used to be, so the log and the video list both start with room to read.
    /// </summary>
    private static readonly Size PreferredWindowSize = new(2000, 1300);

    /// <summary>24 hours, the practical ceiling for a trim timestamp.</summary>
    private const decimal MaxTrimSeconds = 86400;

    /// <summary>
    /// Narrowest the log half may be squeezed to. The log wraps at whatever width it is given,
    /// so this is about keeping it readable rather than about anything clipping.
    /// </summary>
    private const int MinLogWidth = 260;

    // The video list's columns, by index. Named because the row builder and the resize
    // handler both address them, and a bare 2 says nothing about which column it is.
    private const int NameColumn = 0;
    private const int LengthColumn = 1;
    private const int ScriptColumn = 2;
    private const int AxesColumn = 3;

    /// <summary>Blank space at each edge of a cell, so its text does not touch its neighbour.</summary>
    private const int CellInset = 2;

    /// <summary>
    /// What a measured column adds to its widest string: the inset above at both edges, and
    /// enough slack that a header does not sit flush against the column that follows it.
    /// </summary>
    private const int CellPadding = (2 * CellInset) + 8;

    /// <summary>
    /// The three measured columns together. Read back off the columns rather than tracked
    /// separately, so it cannot drift from what they were actually sized to.
    /// </summary>
    private int FixedColumnsWidth =>
        _lstVideos.Columns[LengthColumn].Width
        + _lstVideos.Columns[ScriptColumn].Width
        + _lstVideos.Columns[AxesColumn].Width;

    private void BuildUi() {
        Text = "Attcks Funscript & Video Merger";
        Size = DefaultWindowSize();
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;

        _lstVideos.BackColor = Theme.Well;
        _txtLog.BackColor = Theme.Well;
        _btnStart.BackColor = Theme.PrimaryAction;
        _btnCancel.BackColor = Theme.DestructiveAction;
        _btnRefresh.BackColor = Theme.SecondaryAction;
        _btnOpenInput.BackColor = Theme.SecondaryAction;
        _btnOpenOutput.BackColor = Theme.SecondaryAction;
        _btnOptions.BackColor = Theme.SecondaryAction;

        foreach (Button button in OrderButtons) button.BackColor = Theme.SecondaryAction;

        _btnStart.Click += BtnStart_Click;
        _btnCancel.Click += BtnCancel_Click;
        _btnRefresh.Click += (_, _) => LoadInputFiles();
        _btnOpenInput.Click += (_, _) => OpenFolder(InputFolder, "Input folder");
        _btnOpenOutput.Click += (_, _) => OpenFolder(OutputFolder, "Export folder");
        _btnOptions.Click += BtnOptions_Click;

        BuildVideoColumns();
        _lstVideos.SelectedIndexChanged += LstVideos_SelectedIndexChanged;

        _btnMoveUp.Click += (_, _) => MoveSelectedVideo(-1);
        _btnMoveDown.Click += (_, _) => MoveSelectedVideo(1);
        _btnShuffleOrder.Click += BtnShuffleOrder_Click;
        _btnSortOrder.Click += BtnSortOrder_Click;

        ConfigureToolTips();

        // The splitter bar is the container's own background showing between the two panels.
        _split.BackColor = Theme.Toolbar;
        _split.Panel1.BackColor = Theme.Window;
        _split.Panel2.BackColor = Theme.SidePanel;

        // Dock order matters: fill first, then edges from innermost outward.
        _split.Panel1.Controls.Add(_txtLog);
        _split.Panel1.Controls.Add(_progressBar);
        _split.Panel2.Controls.Add(BuildSidePanel());

        Controls.Add(_split);
        Controls.Add(BuildTopPanel());

        // Only now that the container has been docked and has a real width. Both minimums are
        // validated against it, and a SplitContainer that has not been laid out yet is 150
        // pixels wide - narrower than the two together, which the setter rejects outright.
        _split.Panel1MinSize = MinLogWidth;
        _split.Panel2MinSize = AppSettings.MinSidePanelWidth;
    }

    /// <summary>
    /// <see cref="PreferredWindowSize"/>, cut down to whatever the screen can actually show.
    /// The doubled height is taller than a 1080p desktop, and a window that opens off the
    /// bottom of the screen hides the controls along its edges.
    /// </summary>
    private static Size DefaultWindowSize() {
        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea
            ?? new Rectangle(Point.Empty, PreferredWindowSize);

        return new Size(
            Math.Min(PreferredWindowSize.Width, workingArea.Width),
            Math.Min(PreferredWindowSize.Height, workingArea.Height));
    }

    private FlowLayoutPanel BuildTopPanel() {
        var panel = new FlowLayoutPanel {
            Dock = DockStyle.Top,
            Height = 55,
            Padding = new Padding(10),
            BackColor = Theme.Toolbar
        };

        panel.Controls.Add(new Label { Text = "Output Name:", AutoSize = true, Margin = new Padding(0, 5, 5, 0) });
        panel.Controls.Add(_txtOutputName);
        panel.Controls.Add(_btnStart);
        panel.Controls.Add(_btnCancel);
        panel.Controls.Add(_btnRefresh);
        panel.Controls.Add(_btnOpenInput);
        panel.Controls.Add(_btnOpenOutput);
        panel.Controls.Add(_btnOptions);

        return panel;
    }

    /// <summary>
    /// The side panel's contents. A grid rather than a stack of docked children: the panel is
    /// resizable now, and the list is the row that absorbs the change while the two groups
    /// below it keep the heights they were measured for.
    /// </summary>
    private TableLayoutPanel BuildSidePanel() {
        GroupBox orderGroup = BuildOrderGroup();
        GroupBox trimGroup = BuildTrimGroup();

        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            BackColor = Theme.SidePanel,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 4
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        // The gap that used to be a spacer panel between the two groups. Set before the rows
        // are measured: a margin is taken out of the row it sits in, so a row sized to the
        // group alone would give the group back less than it was measured for and clip it.
        _lstVideos.Margin = new Padding(0);
        orderGroup.Margin = new Padding(0, 5, 0, 15);
        trimGroup.Margin = new Padding(0);

        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, orderGroup.Height + orderGroup.Margin.Vertical));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, trimGroup.Height + trimGroup.Margin.Vertical));

        var listLabel = new Label {
            Text = "Detected Videos:",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 5)
        };

        panel.Controls.Add(listLabel, 0, 0);
        panel.Controls.Add(_lstVideos, 0, 1);
        panel.Controls.Add(orderGroup, 0, 2);
        panel.Controls.Add(trimGroup, 0, 3);

        return panel;
    }

    /// <summary>
    /// The video list's columns. The three on the right are sized to their own contents, and
    /// the name column takes whatever is left - see <see cref="ResizeNameColumn"/>.
    /// </summary>
    /// <remarks>
    /// Measured rather than fixed, for the same reason the reorder buttons are: they are drawn
    /// in the system text size, and a width that fits a header at one size clips it at the next
    /// size up.
    /// </remarks>
    private void BuildVideoColumns() {
        _lstVideos.Columns.Add("Name", 150);
        AddMeasuredColumn("Length", TrimMarker + "0:00:00", HorizontalAlignment.Right);
        AddMeasuredColumn("Script", "Yes", HorizontalAlignment.Center);
        AddMeasuredColumn("Axes", "99", HorizontalAlignment.Right);

        // The header and the rows are drawn by hand: a Details ListView otherwise paints them
        // in the system colours, which read as a bright strip in this dark panel.
        _lstVideos.DrawColumnHeader += LstVideos_DrawColumnHeader;
        _lstVideos.DrawItem += LstVideos_DrawItem;

        _lstVideos.Resize += (_, _) => ResizeNameColumn();
    }

    /// <summary>
    /// Adds a column wide enough for both its header and the widest value it will ever hold.
    /// </summary>
    private void AddMeasuredColumn(string header, string widestValue, HorizontalAlignment alignment) =>
        _lstVideos.Columns.Add(
            header, Math.Max(MeasureCell(header), MeasureCell(widestValue)), alignment);

    private int MeasureCell(string text) =>
        TextRenderer.MeasureText(text, _lstVideos.Font).Width + CellPadding;

    /// <summary>
    /// Gives the name column whatever the measured columns leave over, so a wider side panel
    /// shows more of the filename rather than more empty space.
    /// </summary>
    private void ResizeNameColumn() {
        // Resize can arrive before the columns exist, while the list is being laid out.
        if (_lstVideos.Columns.Count <= AxesColumn) return;

        // ClientSize already excludes the vertical scrollbar when the list is showing one, so
        // the couple of pixels taken off here are only the ones a rounded-up column would spill
        // over. Called again once the rows are in - see RefreshVideoList - because that is when
        // a scrollbar appears, and it narrows the client area without resizing the control.
        int available = _lstVideos.ClientSize.Width - FixedColumnsWidth - (2 * CellInset);

        _lstVideos.Columns[NameColumn].Width = Math.Max(60, available);
    }

    private void LstVideos_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e) {
        using var background = new SolidBrush(Theme.Toolbar);
        e.Graphics.FillRectangle(background, e.Bounds);

        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? string.Empty,
            _lstVideos.Font,
            Rectangle.Inflate(e.Bounds, -CellInset, 0),
            Theme.MutedText,

            // Ellipsis, because a right-aligned header that does not fit is otherwise clipped
            // at its front - the end the reader needs to tell one column from another.
            AlignmentOf(e.Header?.TextAlign ?? HorizontalAlignment.Left) | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// Draws a whole row: its background, then every cell across it.
    /// </summary>
    /// <remarks>
    /// All of it here rather than the background here and the text in a DrawSubItem handler,
    /// which is the split the documentation suggests. Moving the mouse over a row repaints it
    /// through this event alone, without the per-subitem events following, so a handler that
    /// filled the background and left the text to them blanked every cell but the name as soon
    /// as the pointer crossed the row.
    /// </remarks>
    private void LstVideos_DrawItem(object? sender, DrawListViewItemEventArgs e) {
        using var brush = new SolidBrush(e.Item.Selected ? Theme.PrimaryAction : Theme.Well);
        e.Graphics.FillRectangle(brush, e.Bounds);

        // Walked left to right from the row's own left edge, which already accounts for how
        // far the list is scrolled; a subitem's own Bounds cannot be used, because the first
        // one reports the whole row rather than the name column.
        int x = e.Bounds.X;

        for (int column = 0; column < _lstVideos.Columns.Count && column < e.Item.SubItems.Count; column++) {
            int width = _lstVideos.Columns[column].Width;

            // The row builder de-emphasises what it could not determine; a selected row
            // overrides that, because muted grey on the selection colour is the one
            // combination that fails.
            Color foreground = e.Item.Selected ? Color.White : e.Item.SubItems[column].ForeColor;

            TextRenderer.DrawText(
                e.Graphics,
                e.Item.SubItems[column].Text,
                _lstVideos.Font,
                new Rectangle(x + CellInset, e.Bounds.Y, width - (2 * CellInset), e.Bounds.Height),
                foreground,
                AlignmentOf(_lstVideos.Columns[column].TextAlign) | TextFormatFlags.EndEllipsis);

            x += width;
        }
    }

    private static TextFormatFlags AlignmentOf(HorizontalAlignment alignment) =>
        TextFormatFlags.VerticalCenter | alignment switch {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left
        };

    /// <summary>The four reorder buttons, in the order they are laid out.</summary>
    private Button[] OrderButtons => [_btnMoveUp, _btnMoveDown, _btnShuffleOrder, _btnSortOrder];

    /// <summary>
    /// The reorder buttons, directly under the list they act on. A two-by-two grid rather than
    /// a flow: the panel is narrow enough that a flow would wrap differently as soon as a
    /// button's text or the panel's width changed.
    /// </summary>
    private GroupBox BuildOrderGroup() {
        // Measured from the font rather than fixed: the window does not auto-scale, so a
        // hardcoded height is only ever right at one system text size and clips the button
        // labels outright at any larger one.
        int rowHeight = Font.Height + 18;

        var group = new GroupBox {
            Text = "Merge Order",
            Dock = DockStyle.Fill,

            // The two rows, plus the caption the GroupBox takes off its own top, plus the
            // padding below. This is what the grid row it sits in is sized from, and a
            // GroupBox cuts off what does not fit rather than growing.
            Height = (2 * rowHeight) + Font.Height + 24,
            ForeColor = Color.White,
            Padding = new Padding(10, 4, 10, 10)
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };

        for (int column = 0; column < grid.ColumnCount; column++) {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        }

        for (int row = 0; row < grid.RowCount; row++) {
            // Absolute, not percent: a percent row splits whatever the group turned out to be,
            // which hides a group that is too short instead of showing it.
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        }

        // Added left to right, top row first - the grid fills cells in the order it is given.
        foreach (Button button in OrderButtons) {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(2);
            grid.Controls.Add(button);
        }

        group.Controls.Add(grid);

        return group;
    }

    private GroupBox BuildTrimGroup() {
        var group = new GroupBox {
            Text = "Trim Settings",
            Dock = DockStyle.Fill,
            Height = 175,
            ForeColor = Color.White,
            Padding = new Padding(10)
        };

        var applyButton = new Button {
            Text = "Apply to Selected Video",
            Dock = DockStyle.Bottom,
            Height = 35,
            BackColor = Theme.ConfirmAction,
            FlatStyle = FlatStyle.Flat
        };
        applyButton.Click += BtnApplyTrim_Click;

        group.Controls.Add(BuildTimeRow("End (sec):", _numEnd));
        group.Controls.Add(BuildTimeRow("Start (sec):", _numStart));
        group.Controls.Add(_chkEnableTrim);
        group.Controls.Add(applyButton);

        return group;
    }

    private static Panel BuildTimeRow(string caption, NumericUpDown input) {
        var row = new Panel { Dock = DockStyle.Top, Height = 35, Padding = new Padding(0, 5, 0, 0) };

        row.Controls.Add(new Label {
            Text = caption,
            Width = 80,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft
        });
        row.Controls.Add(input);

        return row;
    }

    private void ConfigureToolTips() {
        _toolTip.SetToolTip(_txtOutputName, "The base filename for the merged video and script. Do not include extensions or axis names");

        _toolTip.SetToolTip(_btnCancel, "Stops the running job. The current ffmpeg step is killed and the intermediate files are cleared.");
        _toolTip.SetToolTip(_btnOpenInput, "Opens the folder that is scanned for source videos and funscripts in Explorer.");
        _toolTip.SetToolTip(_btnOpenOutput, "Opens the folder the merged video and funscript are written to in Explorer.");
        _toolTip.SetToolTip(_btnOptions, "Folders, ffmpeg and ffprobe paths, encoding quality and everything else that is remembered between launches.");

        _toolTip.SetToolTip(_lstVideos, "The merge order, top to bottom. Select a video here to reorder it or configure its trim settings. Drag the divider on the left to resize this panel.");

        _toolTip.SetToolTip(_btnMoveUp, "Moves the selected video one place earlier in the merge, taking its funscripts with it.");
        _toolTip.SetToolTip(_btnMoveDown, "Moves the selected video one place later in the merge, taking its funscripts with it.");
        _toolTip.SetToolTip(_btnShuffleOrder, "Shuffles the videos into a random merge order.");
        _toolTip.SetToolTip(_btnSortOrder, "Sorts the videos by filename - the order they are listed in after a refresh.");
        _toolTip.SetToolTip(_chkEnableTrim, "Check this to trim the currently selected video in the list.");
        _toolTip.SetToolTip(_numStart, "The timestamp in seconds where the video segment should start.");
        _toolTip.SetToolTip(_numEnd, "The timestamp in seconds where the video segment should end. Set to 0 to keep the rest of the video.");
    }
}
