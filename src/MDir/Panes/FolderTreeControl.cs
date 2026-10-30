using System.ComponentModel;
using System.Runtime.InteropServices;
using MDir.Features;
using MDir.Integration;
using MDir.Settings;
using MDir.Shell;

namespace MDir.Panes;

public sealed class LocationEventArgs(ShellLocation location) : EventArgs
{
    public ShellLocation Location { get; } = location;
}

/// <summary>
/// Hosts the shell's folder tree (<c>INameSpaceTreeControl</c>, the same control Q-Dir and Explorer use) rooted at
/// the desktop. <see cref="SelectLocation"/> moves the selection without raising <see cref="LocationSelected"/>;
/// that event is only for changes made in the tree itself (mouse or keyboard).
/// </summary>
internal sealed class FolderTreeControl : Control
{
    private const uint ShcontFolders = 0x20;
    private const NstcStyle OptionStyles = NstcStyle.HasLines | NstcStyle.FullRowSelect | NstcStyle.SingleClickExpand;

    private INameSpaceTreeControl2? _tree;
    private TreeSink? _sink;
    private uint _adviseCookie;
    private bool _syncing;
    private ShellLocation? _selected;
    private ShellLocation? _pendingSelect;
    private TreeOptions _options = new();
    private bool _darkMode;
    private ColorRuleSet? _colorRules;
    private ColorTreeHook? _colorHook;
    private int _defaultIndent = -1;

    public FolderTreeControl()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    /// <summary>Raised when the user selects another folder in the tree.</summary>
    public event EventHandler<LocationEventArgs>? LocationSelected;

    /// <summary>The options object is read now; assign it again after changing it to re-apply.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TreeOptions Options
    {
        get => _options;
        set
        {
            _options = value;
            ApplyStyle();
            ApplyIndent();
        }
    }

    /// <summary>Colour rules for the folder names; null turns colouring off.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ColorRuleSet? ColorRules
    {
        get => _colorRules;
        set
        {
            _colorRules = value;
            _colorHook?.Refresh();
        }
    }

    /// <summary>The indentation of tree levels in pixels as the tree view reports it (0 before it exists).</summary>
    internal int CurrentIndent =>
        FindTreeView() is var tree && tree != IntPtr.Zero ? (int)NativeMethods.SendMessage(tree, NativeMethods.TVM_GETINDENT, IntPtr.Zero, IntPtr.Zero) : 0;

    private IntPtr FindTreeView() => IsHandleCreated ? NativeMethods.FindDescendant(Handle, "SysTreeView32") : IntPtr.Zero;

    /// <summary>Applies <see cref="TreeOptions.Indent"/>; 0 puts back the indentation the control started with.</summary>
    private void ApplyIndent()
    {
        IntPtr tree = FindTreeView();
        if (tree == IntPtr.Zero)
            return;

        if (_defaultIndent < 0)
            _defaultIndent = (int)NativeMethods.SendMessage(tree, NativeMethods.TVM_GETINDENT, IntPtr.Zero, IntPtr.Zero);

        int indent = _options.Indent > 0 ? _options.Indent : _defaultIndent;
        if (indent > 0)
            NativeMethods.SendMessage(tree, NativeMethods.TVM_SETINDENT, (IntPtr)indent, IntPtr.Zero);
    }

    /// <summary>Dark scroll bars, background and text.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DarkMode
    {
        get => _darkMode;
        set
        {
            _darkMode = value;
            ApplyDarkMode();
        }
    }

    private void ApplyDarkMode()
    {
        if (_tree is null || !IsHandleCreated)
            return;

        _tree.SetTheme(_darkMode ? "DarkMode_Explorer" : "explorer");
        IntPtr treeView = NativeMethods.FindDescendant(Handle, "SysTreeView32");
        if (treeView == IntPtr.Zero)
            return;

        // The tree draws its own background and text with these colours; the theme alone leaves them light.
        uint background = _darkMode ? 0x00202020u : (uint)NativeMethods.GetSysColor(NativeMethods.COLOR_WINDOW);
        uint text = _darkMode ? 0x00E6E6E6u : (uint)NativeMethods.GetSysColor(NativeMethods.COLOR_WINDOWTEXT);
        NativeMethods.SendMessage(treeView, NativeMethods.TVM_SETBKCOLOR, IntPtr.Zero, (IntPtr)background);
        NativeMethods.SendMessage(treeView, NativeMethods.TVM_SETTEXTCOLOR, IntPtr.Zero, (IntPtr)text);
        AppAppearance.SetControlTheme(treeView, _darkMode);
    }

    /// <summary>The selection as the tree control itself reports it.</summary>
    public ShellLocation? GetSelectedLocation()
    {
        if (_tree is null || _tree.GetSelectedItems(out IShellItemArray items) < 0
            || items.GetCount(out uint count) < 0 || count == 0 || items.GetItemAt(0, out IShellItem item) < 0)
            return null;

        return ShellItems.ToLocation(item);
    }

    /// <summary>Selects (and expands parents of) <paramref name="location"/> without raising <see cref="LocationSelected"/>.</summary>
    public void SelectLocation(ShellLocation location)
    {
        if (_tree is null)
        {
            _pendingSelect = location;
            return;
        }

        if (location.Equals(_selected) || ShellItems.FromLocation(location) is not { } item)
            return;

        NstcItemState state = NstcItemState.Selected | (_options.AutoExpand ? NstcItemState.Expanded : NstcItemState.None);
        _syncing = true;
        try
        {
            int hr = _tree.SetItemState(item, state, state);
            if (hr < 0)
            {
                // The item may not be loaded yet: open its ancestors top-down, then try again.
                ExpandAncestors(location);
                hr = _tree.SetItemState(item, state, state);
            }

            if (hr >= 0)
            {
                _tree.EnsureItemVisible(item);
                _selected = location;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    public void FocusTree()
    {
        Focus();
        if (TreeWindow != IntPtr.Zero)
            NativeMethods.SetFocus(TreeWindow);
    }

    private IntPtr TreeWindow => IsHandleCreated ? NativeMethods.GetWindow(Handle, NativeMethods.GW_CHILD) : IntPtr.Zero;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DesignMode)
            return;

        _tree = (INameSpaceTreeControl2)new NamespaceTreeControlClass();
        _sink = new TreeSink(this);
        var rect = new NativeRect(0, 0, ClientSize.Width, ClientSize.Height);
        Marshal.ThrowExceptionForHR(_tree.Initialize(Handle, ref rect, BuildStyle()));
        _tree.SetTheme("explorer");
        Marshal.ThrowExceptionForHR(_tree.TreeAdvise(_sink, out _adviseCookie));
        ApplyDarkMode();

        // The namespace root (desktop) with This PC, libraries, network... below it, like Explorer's navigation pane.
        if (ShellItems.FromLocation(ShellLocation.Desktop) is { } root)
            Marshal.ThrowExceptionForHR(_tree.AppendRoot(root, ShcontFolders, NstcRootStyle.Expanded, IntPtr.Zero));

        ResizeTreeWindow();
        ApplyIndent();
        IntPtr treeView = FindTreeView();
        if (treeView != IntPtr.Zero && NativeMethods.GetParent(treeView) is var treeParent && treeParent != IntPtr.Zero)
            _colorHook = new ColorTreeHook(treeParent, () => _colorRules);

        if (_pendingSelect is { } pending)
        {
            _pendingSelect = null;
            SelectLocation(pending);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _colorHook?.Dispose();
        _colorHook = null;
        _defaultIndent = -1;
        if (_tree is not null)
        {
            if (_adviseCookie != 0)
                _tree.TreeUnadvise(_adviseCookie);

            Marshal.ReleaseComObject(_tree);
            _tree = null;
            _adviseCookie = 0;
        }

        _sink = null;
        _selected = null;
        base.OnHandleDestroyed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeTreeWindow();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (TreeWindow != IntPtr.Zero)
            NativeMethods.SetFocus(TreeWindow);
    }

    /// <summary>
    /// Keyboard messages for the tree's native window: application shortcuts first, Tab as dialog navigation,
    /// everything else (arrows, F2, Delete...) straight to the tree rather than WinForms' dialog-key handling.
    /// </summary>
    public override bool PreProcessMessage(ref Message msg)
    {
        if (msg.Msg is < NativeMethods.WM_KEYDOWN or > NativeMethods.WM_KEYLAST || msg.Msg == NativeMethods.WM_SYSCHAR)
            return base.PreProcessMessage(ref msg);

        bool keyDown = msg.Msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        Keys keyData = (Keys)(int)(nint)msg.WParam | ModifierKeys;
        if (keyDown && ProcessCmdKey(ref msg, keyData))
            return true;

        return keyDown && (keyData & ~Keys.Shift) == Keys.Tab && ProcessDialogKey(keyData);
    }

    private void ResizeTreeWindow()
    {
        IntPtr window = TreeWindow;
        if (window != IntPtr.Zero)
            NativeMethods.MoveWindow(window, 0, 0, ClientSize.Width, ClientSize.Height, repaint: true);
    }

    private NstcStyle BuildStyle()
    {
        NstcStyle style = NstcStyle.HasExpandos | NstcStyle.HorizontalScroll | NstcStyle.ShowSelectionAlways | NstcStyle.TabStop;
        if (_options.FullRowSelect)
            style |= NstcStyle.FullRowSelect;
        else if (_options.Lines)
            style |= NstcStyle.HasLines;
        if (_options.SingleClickExpand)
            style |= NstcStyle.SingleClickExpand;

        return style;
    }

    private void ApplyStyle() => _tree?.SetControlStyle(OptionStyles, BuildStyle() & OptionStyles);

    private void ExpandAncestors(ShellLocation location)
    {
        var ancestors = new List<ShellLocation>();
        for (ShellLocation? parent = location.GetParent(); parent is { IsDesktop: false }; parent = parent.GetParent())
            ancestors.Add(parent);

        ancestors.Reverse();
        foreach (ShellLocation ancestor in ancestors)
        {
            if (ShellItems.FromLocation(ancestor) is { } item)
                _tree!.SetItemState(item, NstcItemState.Expanded, NstcItemState.Expanded);
        }
    }

    private void OnSelectionChanged(IShellItemArray selection)
    {
        // Our own SelectLocation reports through _syncing; the equality check also drops late echoes of it.
        if (_syncing || selection.GetCount(out uint count) < 0 || count == 0 || selection.GetItemAt(0, out IShellItem item) < 0)
            return;

        if (ShellItems.ToLocation(item) is not { } location || location.Equals(_selected))
            return;

        _selected = location;
        LocationSelected?.Invoke(this, new LocationEventArgs(location));
    }

    [ComVisible(true)]
    private sealed class TreeSink(FolderTreeControl owner) : INameSpaceTreeControlEvents
    {
        private const int ENotImpl = unchecked((int)0x80004001);

        public int OnSelectionChanged(IShellItemArray psiaSelection)
        {
            owner.OnSelectionChanged(psiaSelection);
            return 0;
        }

        public int OnItemClick(IShellItem psi, uint nstceHitTest, NstcStyle nstcsFlags) => 0;

        public int OnPropertyItemCommit(IShellItem psi) => 0;

        public int OnItemStateChanging(IShellItem psi, NstcItemState nstcisMask, NstcItemState nstcisState) => 0;

        public int OnItemStateChanged(IShellItem psi, NstcItemState nstcisMask, NstcItemState nstcisState) => 0;

        public int OnKeyboardInput(uint uMsg, IntPtr wParam, IntPtr lParam) => 0;

        public int OnBeforeExpand(IShellItem psi) => 0;

        public int OnAfterExpand(IShellItem psi) => 0;

        public int OnBeginLabelEdit(IShellItem psi) => 0;

        public int OnEndLabelEdit(IShellItem psi) => 0;

        public int OnGetToolTip(IShellItem psi, IntPtr pszTip, int cchTip) => ENotImpl;

        public int OnBeforeItemDelete(IShellItem psi) => 0;

        public int OnItemAdded(IShellItem psi, bool fIsRoot) => 0;

        public int OnItemDeleted(IShellItem psi, bool fIsRoot) => 0;

        // E_NOTIMPL makes the control use the shell's standard context menu and icons.
        public int OnBeforeContextMenu(IShellItem psi, ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            return ENotImpl;
        }

        public int OnAfterContextMenu(IShellItem psi, IntPtr pcmIn, ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            return ENotImpl;
        }

        public int OnBeforeStateImageChange(IShellItem psi) => 0;

        public int OnGetDefaultIconIndex(IShellItem psi, out int piDefaultIcon, out int piOpenIcon)
        {
            piDefaultIcon = piOpenIcon = 0;
            return ENotImpl;
        }
    }
}
