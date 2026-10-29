using System.Runtime.InteropServices;
using System.Text;

namespace MDir.Features;

/// <summary>
/// Organizes the favorites: a tree of folders and shortcuts that can be renamed in place (F2), reordered and moved
/// by dragging (drop on a folder's middle to move into it, on an edge to place before/after), deleted, and given an
/// icon. Every change is written to disk at once; the dialog only has a Close button.
/// </summary>
internal sealed class FavoritesDialog : Form
{
    private readonly FavoritesStore _store;
    private readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill,
        LabelEdit = true,
        AllowDrop = true,
        HideSelection = false,
        ShowNodeToolTips = true,
        ShowLines = true,
    };

    private readonly ImageList _images = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly Button _renameButton = CreateButton("이름 바꾸기 (F2)");
    private readonly Button _deleteButton = CreateButton("삭제");
    private readonly Button _upButton = CreateButton("위로");
    private readonly Button _downButton = CreateButton("아래로");
    private readonly Button _iconButton = CreateButton("아이콘 변경...");
    private readonly Button _resetIconButton = CreateButton("기본 아이콘");

    public FavoritesDialog(FavoritesStore store)
    {
        _store = store;
        Text = "즐겨찾기 정리";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 480);
        MinimumSize = new Size(480, 360);

        _images.ImageSize = new Size(LogicalToDeviceUnits(16), LogicalToDeviceUnits(16));
        _tree.ImageList = _images;

        var newFolder = CreateButton("새 폴더");
        newFolder.Click += (_, _) => NewFolderInteractive();
        _renameButton.Click += (_, _) => _tree.SelectedNode?.BeginEdit();
        _deleteButton.Click += (_, _) => DeleteInteractive();
        _upButton.Click += (_, _) => MoveSelected(-1);
        _downButton.Click += (_, _) => MoveSelected(1);
        _iconButton.Click += (_, _) => ChangeIconInteractive();
        _resetIconButton.Click += (_, _) => SetSelectedIcon(null, 0);

        var close = new Button { Text = "닫기", DialogResult = DialogResult.OK, AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        AcceptButton = CancelButton = close;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([newFolder, _renameButton, _deleteButton, _upButton, _downButton, _iconButton, _resetIconButton]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_tree, 0, 0);
        layout.Controls.Add(buttons, 1, 0);
        layout.Controls.Add(new Label
        {
            Text = "끌어서 순서를 바꾸거나 폴더로 옮깁니다. 폴더 가운데에 놓으면 그 안으로 들어갑니다.",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        }, 0, 1);
        layout.Controls.Add(close, 1, 1);
        Controls.Add(layout);

        _tree.AfterSelect += (_, _) => UpdateButtons();
        _tree.AfterLabelEdit += OnAfterLabelEdit;
        _tree.ItemDrag += (_, e) => _tree.DoDragDrop(e.Item!, DragDropEffects.Move);
        _tree.DragOver += OnDragOver;
        _tree.DragDrop += OnDragDrop;
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2 && _tree.SelectedNode is { } node && !IsProtected(node))
                node.BeginEdit();
            else if (e.KeyCode == Keys.Delete)
                DeleteInteractive();
        };

        Rebuild(selectPath: null);
    }

    internal TreeView Tree => _tree;

    /// <summary>The path of the selected favorite, or null.</summary>
    internal string? SelectedPath => SelectedEntry?.Path;

    private FavoriteEntry? SelectedEntry => _tree.SelectedNode?.Tag as FavoriteEntry;

    /// <summary>Reloads the tree from disk, keeping folders expanded and selecting <paramref name="selectPath"/> if given.</summary>
    internal void Rebuild(string? selectPath)
    {
        HashSet<string> expanded = [.. AllNodes(_tree.Nodes).Where(node => node.IsExpanded).Select(node => ((FavoriteEntry)node.Tag!).Path)];
        selectPath ??= SelectedPath;

        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            foreach (Image image in _images.Images.Cast<Image>().ToList())
                image.Dispose();
            _images.Images.Clear();
            AddNodes(_tree.Nodes, _store.Root, expanded);

            // Top level folders start opened the first time.
            if (expanded.Count == 0)
                _tree.ExpandAll();

            if (selectPath is not null && AllNodes(_tree.Nodes).FirstOrDefault(node => SamePath(((FavoriteEntry)node.Tag!).Path, selectPath)) is { } node)
                _tree.SelectedNode = node;
            else if (_tree.Nodes.Count > 0)
                _tree.SelectedNode = _tree.Nodes[0];
        }
        finally
        {
            _tree.EndUpdate();
        }

        UpdateButtons();
    }

    /// <summary>Creates a folder in the selected folder (or next to the selected shortcut) and selects it.</summary>
    internal string CreateFolder(string name)
    {
        string parent = SelectedEntry is { IsFolder: true } folder ? folder.Path
            : SelectedEntry is { } link ? Path.GetDirectoryName(link.Path)! : _store.Root;
        string path = _store.CreateFolder(parent, name);
        Rebuild(path);
        return path;
    }

    internal string RenameSelected(string newName)
    {
        FavoriteEntry entry = SelectedEntry ?? throw new InvalidOperationException("Nothing is selected.");
        string path = _store.Rename(entry.Path, newName);
        Rebuild(path);
        return path;
    }

    internal void DeleteSelected()
    {
        if (SelectedEntry is not { } entry)
            return;

        int index = _tree.SelectedNode!.Index;
        TreeNode? parent = _tree.SelectedNode.Parent;
        _store.Delete(entry.Path);
        Rebuild(selectPath: null);
        TreeNodeCollection siblings = parent?.Nodes ?? _tree.Nodes;
        if (siblings.Count > 0)
            _tree.SelectedNode = siblings[Math.Min(index, siblings.Count - 1)];
    }

    /// <summary>Moves the selected entry up or down among its siblings.</summary>
    internal void MoveSelected(int step)
    {
        if (SelectedEntry is not { } entry || _tree.SelectedNode is not { } node)
            return;

        int target = node.Index + Math.Sign(step);
        TreeNodeCollection siblings = node.Parent?.Nodes ?? _tree.Nodes;
        if (target < 0 || target >= siblings.Count)
            return;

        string path = _store.Move(entry.Path, Path.GetDirectoryName(entry.Path)!, target);
        Rebuild(path);
    }

    internal void SetSelectedIcon(string? iconPath, int index)
    {
        if (SelectedEntry is not { IsFolder: false } entry)
            return;

        _store.SetIcon(entry.Path, iconPath, index);
        Rebuild(entry.Path);
    }

    /// <summary>
    /// Where a drop lands: in a folder when the cursor is over the middle of a folder's row, otherwise before (upper
    /// half) or after (lower half) the row's entry. <paramref name="fraction"/> is the cursor's height within the row (0..1).
    /// </summary>
    internal (string Folder, int Index) DropPosition(TreeNode? target, float fraction)
    {
        if (target?.Tag is not FavoriteEntry entry)
            return (_store.Root, -1);

        if (entry.IsFolder && fraction is > 0.25f and < 0.75f)
            return (entry.Path, -1);

        return (Path.GetDirectoryName(entry.Path)!, target.Index + (fraction >= 0.5f ? 1 : 0));
    }

    /// <summary>Moves <paramref name="source"/> to the position a drop on <paramref name="target"/> means.</summary>
    internal void Drop(TreeNode source, TreeNode? target, float fraction)
    {
        if (source.Tag is not FavoriteEntry entry || _store.IsQuickLinkFolder(entry.Path))
            return;

        (string folder, int index) = DropPosition(target, fraction);

        // Dropping lower in the same list: the entry's own slot disappears first.
        if (index >= 0 && SamePath(folder, Path.GetDirectoryName(entry.Path)!) && source.Index < index)
            index--;

        try
        {
            Rebuild(_store.Move(entry.Path, folder, index));
        }
        catch (ArgumentException)
        {
            // A folder dropped into itself or its own subfolder: nothing to do.
        }
    }

    private void NewFolderInteractive()
    {
        string path = CreateFolder("새 폴더");
        if (AllNodes(_tree.Nodes).FirstOrDefault(node => SamePath(((FavoriteEntry)node.Tag!).Path, path)) is { } node)
            node.BeginEdit();
    }

    private void DeleteInteractive()
    {
        if (SelectedEntry is not { } entry || _store.IsQuickLinkFolder(entry.Path))
            return;

        string what = entry.IsFolder ? $"'{entry.Name}' 폴더와 그 안의 즐겨찾기" : $"'{entry.Name}'";
        if (MessageBox.Show(this, $"{what}를 삭제할까요?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            DeleteSelected();
    }

    private void ChangeIconInteractive()
    {
        if (SelectedEntry is not { IsFolder: false } entry)
            return;

        var path = new StringBuilder(@"%SystemRoot%\system32\shell32.dll", 520);
        int index = 0;
        if (_store.Read(entry.Path) is { IconPath: { } current } info)
        {
            path.Clear().Append(current);
            index = info.IconIndex;
        }

        if (PickIconDlg(Handle, path, path.Capacity, ref index) != 0)
            SetSelectedIcon(path.ToString(), index);
    }

    private void OnAfterLabelEdit(object? sender, NodeLabelEditEventArgs e)
    {
        e.CancelEdit = true;
        if (e.Label is null || e.Node?.Tag is not FavoriteEntry entry)
            return;

        try
        {
            string path = _store.Rename(entry.Path, e.Label);

            // Rebuilding inside the event would pull the node away from under it.
            BeginInvoke(() => Rebuild(path));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetData(typeof(TreeNode)) is TreeNode { Tag: FavoriteEntry entry } && !_store.IsQuickLinkFolder(entry.Path)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(TreeNode)) is not TreeNode source)
            return;

        Point point = _tree.PointToClient(new Point(e.X, e.Y));
        TreeNode? target = _tree.GetNodeAt(point);
        float fraction = target is null ? 1f : Math.Clamp((point.Y - target.Bounds.Top) / (float)Math.Max(1, target.Bounds.Height), 0f, 1f);
        Drop(source, target, fraction);
    }

    private void UpdateButtons()
    {
        FavoriteEntry? entry = SelectedEntry;
        bool editable = entry is not null && !_store.IsQuickLinkFolder(entry.Path);
        _renameButton.Enabled = _deleteButton.Enabled = editable;
        _upButton.Enabled = _downButton.Enabled = editable;
        _iconButton.Enabled = _resetIconButton.Enabled = entry is { IsFolder: false };
    }

    private bool IsProtected(TreeNode node) => node.Tag is FavoriteEntry entry && _store.IsQuickLinkFolder(entry.Path);

    private void AddNodes(TreeNodeCollection nodes, string folder, HashSet<string> expanded)
    {
        foreach (FavoriteEntry entry in _store.List(folder))
        {
            Bitmap? icon = FavoritesMenu.LoadIcon(_store, entry);
            int image = -1;
            if (icon is not null)
            {
                _images.Images.Add(icon);
                image = _images.Images.Count - 1;
            }

            var node = new TreeNode(entry.Name) { Tag = entry, ImageIndex = image, SelectedImageIndex = image };
            if (!entry.IsFolder && _store.Read(entry.Path)?.Target is { } target)
                node.ToolTipText = target.DisplayName;

            nodes.Add(node);
            if (entry.IsFolder)
            {
                AddNodes(node.Nodes, entry.Path, expanded);
                if (expanded.Contains(entry.Path))
                    node.Expand();
            }
        }
    }

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (TreeNode child in AllNodes(node.Nodes))
                yield return child;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static Button CreateButton(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3), MinimumSize = new Size(120, 0) };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Image image in _images.Images.Cast<Image>().ToList())
                image.Dispose();
            _images.Dispose();
        }

        base.Dispose(disposing);
    }

    // shell32 exports the "choose icon" dialog by ordinal only.
#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", EntryPoint = "#62", CharSet = CharSet.Unicode)]
    private static extern int PickIconDlg(IntPtr hwnd, StringBuilder pszIconPath, int cchIconPath, ref int piIconIndex);
#pragma warning restore SYSLIB1054
}
