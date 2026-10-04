using AttcksMergeTool.Services;

namespace AttcksMergeTool.UI;

/// <summary>
/// Picks which folders of the audio library a video draws its voice clips from. Checking a
/// folder takes everything below it, so a whole voice actor - or a whole pack - is one click.
/// </summary>
/// <remarks>
/// The checked state lives in <see cref="_checked"/>, keyed by folder path, rather than on the
/// tree's nodes: the filter rebuilds the tree, and checks on folders it hides have to survive.
/// </remarks>
public sealed class AudioFolderPickerForm : Form
{
    private readonly Func<AudioLibrary> _rescan;
    private readonly Action _openFolder;
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBox _txtFilter = new() {
        Dock = DockStyle.Top,
        PlaceholderText = "Filter folders...",
        BorderStyle = BorderStyle.FixedSingle
    };

    private readonly TreeView _tree = new() {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle
    };

    private readonly Label _lblSummary = new() {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private AudioLibrary _library;

    /// <summary>Set while the tree's checks are being written from <see cref="_checked"/>.</summary>
    private bool _syncing;

    /// <param name="selected">The folders chosen so far, relative to the library root.</param>
    /// <param name="rescan">Rereads the library from disk, for the Rescan button.</param>
    /// <param name="openFolder">Shows the library folder in Explorer, creating it if need be.</param>
    public AudioFolderPickerForm(
        AudioLibrary library,
        IEnumerable<string> selected,
        Func<AudioLibrary> rescan,
        Action openFolder) {
        _library = library;
        _rescan = rescan;
        _openFolder = openFolder;

        foreach (string folder in selected) {
            if (library.FindFolder(folder) is { } node) Check(node, true);
        }

        BuildUi();
        RebuildTree();
    }

    /// <summary>
    /// The chosen folders, each listed once: a checked folder stands for everything below it,
    /// so its subfolders are not listed again.
    /// </summary>
    public IReadOnlyList<string> SelectedFolders {
        get {
            var folders = new List<string>();
            Collect(_library.Root, folders);
            return folders;
        }
    }

    /// <summary>The library as it stands after any rescan, for the caller to keep.</summary>
    public AudioLibrary Library => _library;

    private void Collect(AudioFolder folder, List<string> into) {
        if (_checked.Contains(folder.RelativePath)) {
            into.Add(folder.RelativePath);
            return;
        }

        foreach (AudioFolder child in folder.Children) Collect(child, into);
    }

    private void BuildUi() {
        Text = "Choose Voice Folders";
        Size = new Size(720, 760);
        MinimumSize = new Size(480, 400);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;

        _txtFilter.BackColor = Theme.Field;
        _txtFilter.ForeColor = Theme.Text;
        _txtFilter.TextChanged += (_, _) => RebuildTree();

        _tree.BackColor = Theme.Well;
        _tree.ForeColor = Theme.Text;
        _tree.LineColor = Theme.MutedText;
        _tree.AfterCheck += Tree_AfterCheck;

        var ok = NewButton("OK", Theme.ConfirmAction);
        ok.DialogResult = DialogResult.OK;

        var cancel = NewButton("Cancel", Theme.SecondaryAction);
        cancel.DialogResult = DialogResult.Cancel;

        var clear = NewButton("Clear", Theme.DestructiveAction);
        clear.Click += (_, _) => {
            _checked.Clear();
            SyncChecks(_tree.Nodes);
            UpdateSummary();
        };

        var rescan = NewButton("Rescan", Theme.SecondaryAction);
        rescan.Click += (_, _) => Rescan();

        var open = NewButton("Open Audio Folder", Theme.SecondaryAction);
        open.Click += (_, _) => _openFolder();

        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };

        // Right to left, so the first added is the rightmost.
        buttons.Controls.AddRange([ok, cancel, clear, rescan, open]);

        var bar = new Panel {
            Dock = DockStyle.Bottom,
            Height = 55,
            Padding = new Padding(10, 0, 10, 0),
            BackColor = Theme.Toolbar
        };

        bar.Controls.Add(_lblSummary);
        bar.Controls.Add(buttons);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var gap = new Panel { Dock = DockStyle.Top, Height = 8 };

        // Fill first, then the edges from innermost outward.
        body.Controls.Add(_tree);
        body.Controls.Add(gap);
        body.Controls.Add(_txtFilter);

        Controls.Add(body);
        Controls.Add(bar);
    }

    private static Button NewButton(string caption, Color background) => new() {
        Text = caption,
        AutoSize = true,
        ForeColor = Color.White,
        BackColor = background,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(6, 0, 0, 0),
        Padding = new Padding(8, 3, 8, 3)
    };

    // --- Tree ---

    /// <summary>
    /// Rebuilds the tree, keeping only folders whose path matches the filter, the folders that
    /// lead to them, and everything below a match.
    /// </summary>
    private void RebuildTree() {
        string filter = _txtFilter.Text.Trim();

        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        if (_library.FileCount == 0) {
            _tree.Nodes.Add(new TreeNode($"No audio clips found in {_library.RootPath}") { ForeColor = Theme.MutedText });
        } else if (BuildNode(_library.Root, filter, ancestorMatched: filter.Length == 0) is { } root) {
            _tree.Nodes.Add(root);

            if (filter.Length > 0) {
                root.ExpandAll();
            } else {
                root.Expand();
            }
        }

        _tree.EndUpdate();

        if (_tree.Nodes.Count > 0) _tree.Nodes[0].EnsureVisible();

        UpdateSummary();
    }

    private TreeNode? BuildNode(AudioFolder folder, string filter, bool ancestorMatched) {
        bool matched = ancestorMatched
                       || folder.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var children = new List<TreeNode>();

        foreach (AudioFolder child in folder.Children) {
            if (BuildNode(child, filter, matched) is { } node) children.Add(node);
        }

        if (!matched && children.Count == 0) return null;

        string name = folder.RelativePath.Length == 0 ? "All audio" : folder.Name;

        var item = new TreeNode($"{name} ({folder.TotalFiles})", [.. children]) {
            Tag = folder,
            Checked = _checked.Contains(folder.RelativePath)
        };

        return item;
    }

    private void Tree_AfterCheck(object? sender, TreeViewEventArgs e) {
        if (_syncing || e.Node?.Tag is not AudioFolder folder) return;

        Check(folder, e.Node.Checked);

        // A folder only counts as checked while all of it is, so unchecking anything below a
        // checked folder unchecks that folder - its other children keep their own checks.
        if (!e.Node.Checked) {
            for (TreeNode? parent = e.Node.Parent; parent?.Tag is AudioFolder ancestor; parent = parent.Parent) {
                _checked.Remove(ancestor.RelativePath);
            }
        }

        SyncChecks(_tree.Nodes);
        UpdateSummary();
    }

    /// <summary>Checks or unchecks a folder and everything below it, visible or not.</summary>
    private void Check(AudioFolder folder, bool isChecked) {
        if (isChecked) {
            _checked.Add(folder.RelativePath);
        } else {
            _checked.Remove(folder.RelativePath);
        }

        foreach (AudioFolder child in folder.Children) Check(child, isChecked);
    }

    private void SyncChecks(TreeNodeCollection nodes) {
        _syncing = true;

        try {
            SyncNodes(nodes);
        } finally {
            _syncing = false;
        }
    }

    private void SyncNodes(TreeNodeCollection nodes) {
        foreach (TreeNode node in nodes) {
            if (node.Tag is AudioFolder folder) node.Checked = _checked.Contains(folder.RelativePath);
            SyncNodes(node.Nodes);
        }
    }

    private void UpdateSummary() {
        IReadOnlyList<string> folders = SelectedFolders;

        // The chosen folders never overlap, so their totals add up without double counting.
        int clips = folders.Sum(path => _library.FindFolder(path)?.TotalFiles ?? 0);

        _lblSummary.Text = folders.Count == 0
            ? $"Nothing selected · {_library.FileCount} clips in the library"
            : $"{folders.Count} folder{(folders.Count == 1 ? "" : "s")} · {clips} clips selected";
    }

    private void Rescan() {
        IReadOnlyList<string> selected = SelectedFolders;

        UseWaitCursor = true;

        try {
            _library = _rescan();
        } finally {
            UseWaitCursor = false;
        }

        _checked.Clear();

        foreach (string folder in selected) {
            if (_library.FindFolder(folder) is { } node) Check(node, true);
        }

        RebuildTree();
    }
}
