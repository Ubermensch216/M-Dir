using System.ComponentModel;
using MDir.Features;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;

namespace MDir.Panes;

/// <summary>
/// One pane of the main window: tab strip, navigation bar (back/forward/up, address with a locations drop-down,
/// refresh, view), the Explorer view and a status bar. The active pane draws a highlighted border; menu commands
/// act on it. All tabs of a pane share one Explorer view, which is pointed at the selected tab.
/// </summary>
internal sealed class PaneControl : Panel
{
    private const int LogicalBorder = 2;

    /// <summary>Folders offered by the address drop-down before the drives (names come from the shell).</summary>
    private static readonly string[] QuickLocations =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "shell:Personal",
        "shell:Downloads",
        "shell:My Pictures",
        "shell:My Music",
        "shell:My Video",
        "shell:MyComputerFolder",
        "shell:NetworkPlacesFolder",
        "shell:RecycleBinFolder",
    ];

    private readonly PaneTabStrip _tabStrip = new();
    private readonly TextBox _address = new() { Dock = DockStyle.Fill, Margin = new Padding(4, 5, 4, 3) };
    private readonly ToolStripButton _backButton;
    private readonly ToolStripButton _forwardButton;
    private readonly ToolStripButton _upButton;
    private readonly ToolStripStatusLabel _itemsLabel = new();
    private readonly ToolStripStatusLabel _selectionLabel = new();
    private readonly PaneFilterBar _filterBar = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Splitter _treeSplitter = new() { Dock = DockStyle.Left, Width = 5, MinSize = 60, MinExtra = 120, Visible = false };
    private FolderTreeControl? _tree;
    private TreeOptions _treeOptions = new();
    private int _treeSplit = Workspace.DefaultTreeSplit;
    private bool _treeEnabled;
    private bool _treeAvailable = true;
    private bool _applyingTreeWidth;
    private PaneTab _currentTab;
    private bool _isActive;
    private bool _filterBarShown;

    public PaneControl(int index)
    {
        Index = index;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Padding = new Padding(LogicalToDeviceUnits(LogicalBorder));

        _backButton = Glyphs.CreateButton(Glyphs.Back, "뒤로 (Alt+←)", (_, _) => Browser.GoBack());
        _forwardButton = Glyphs.CreateButton(Glyphs.Forward, "앞으로 (Alt+→)", (_, _) => Browser.GoForward());
        _upButton = Glyphs.CreateButton(Glyphs.Up, "상위 폴더 (Alt+↑)", (_, _) => Browser.GoUp());

        var status = new StatusStrip { SizingGrip = false };
        status.Items.AddRange([_itemsLabel, _selectionLabel]);

        // Z-order matters for docking: the list (Fill) is last, then the splitter, then the tree (Left) first.
        _content.Controls.Add(Browser);
        _content.Controls.Add(_treeSplitter);
        Controls.Add(_content);
        Controls.Add(BuildNavigationBar());
        Controls.Add(_tabStrip);

        // The filter bar docks above the status bar, so it is added before it.
        Controls.Add(_filterBar);
        Controls.Add(status);
        _content.BringToFront();
        _content.SizeChanged += (_, _) => ApplyTreeWidth();
        _treeSplitter.SplitterMoved += (_, _) => OnTreeSplitterMoved();

        _currentTab = new PaneTab();
        _tabStrip.Insert(0, _currentTab);
        _tabStrip.SelectedIndex = 0;
        Browser.LoadState(_currentTab.History, _currentTab.ViewMode, _currentTab.IconSize);

        _tabStrip.SelectedIndexChanged += (_, _) => OnSelectedTabChanged();
        _tabStrip.NewTabRequested += (_, _) => NewTab();
        _tabStrip.TabCloseRequested += (_, e) => CloseTab(e.Tab!);
        _tabStrip.TabContextMenuRequested += (_, e) => ShowTabMenu(e.Tab, e.Location);

        _address.KeyDown += OnAddressKeyDown;
        _address.HandleCreated += (_, _) => NativeMethods.SHAutoComplete(_address.Handle,
            NativeMethods.SHACF_FILESYS_DIRS | NativeMethods.SHACF_AUTOSUGGEST_FORCE_ON | NativeMethods.SHACF_AUTOAPPEND_FORCE_OFF);
        Browser.NavigationCompleted += (_, _) =>
        {
            _tabStrip.RefreshTabs();
            UpdateNavigationState();
            SyncTree();
        };
        Browser.NavigationFailed += (_, e) => MessageBox.Show(FindForm(),
            $"'{e.Location.DisplayName}' 위치를 열 수 없습니다.", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Browser.ItemsChanged += (_, _) => UpdateStatus();
        Browser.ViewFocused += (_, _) => ViewFocused?.Invoke(this, EventArgs.Empty);
        _filterBar.FilterChanged += (_, _) => ApplyFilterFromBar();
        _filterBar.PatternCommitted += (_, pattern) => FilterCommitted?.Invoke(this, pattern);
        _filterBar.Dismissed += (_, _) => FocusBrowser();
        Browser.ViewModeChanged += (_, _) =>
        {
            _currentTab.ViewMode = Browser.ViewMode;
            _currentTab.IconSize = Browser.IconSize;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        UpdateNavigationState();
    }

    /// <summary>Raised when the folder, history, counts, view mode or selected tab change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when the pane's file list got keyboard focus (so the window can make this pane the active one).</summary>
    public event EventHandler? ViewFocused;

    /// <summary>Raised with a filter pattern the user confirmed, to be remembered in the recent list.</summary>
    public event EventHandler<string>? FilterCommitted;

    /// <summary>Zero-based pane number (0 = top-left).</summary>
    public int Index { get; }

    public ExplorerBrowserHost Browser { get; } = new() { Dock = DockStyle.Fill };

    public IReadOnlyList<PaneTab> Tabs => _tabStrip.Tabs;

    public PaneTab SelectedTab => _currentTab;

    internal PaneTabStrip TabStrip => _tabStrip;

    internal PaneFilterBar FilterBar => _filterBar;

    /// <summary>The pane's own folder tree is switched on (saved with the workspace).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool TreeEnabled
    {
        get => _treeEnabled;
        set
        {
            _treeEnabled = value;
            UpdateTree();
        }
    }

    /// <summary>False while the window uses one shared tree: the pane's own tree stays switched on but hidden.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool TreeAvailable
    {
        get => _treeAvailable;
        set
        {
            _treeAvailable = value;
            UpdateTree();
        }
    }

    /// <summary>Tree width as a share of the pane in 1/10000 (Q-Dir's <c>TreeNsplit</c>).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TreeSplit
    {
        get => _treeSplit;
        set
        {
            _treeSplit = Math.Clamp(value, 500, 9500);
            ApplyTreeWidth();
        }
    }

    internal FolderTreeControl? Tree => _tree;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal TreeOptions TreeOptions
    {
        get => _treeOptions;
        set
        {
            _treeOptions = value;
            if (_tree is not null)
                _tree.Options = value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;

            _isActive = value;
            _tabStrip.Active = value;
            Browser.IsActivePane = value;
            Invalidate();
        }
    }

    /// <summary>The colour rules for this pane's list (null = no colouring).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ColorRuleSet? ColorRules
    {
        get => Browser.ColorRules;
        set => Browser.ColorRules = value;
    }

    /// <summary>The pane's name filter (<see cref="FileFilter.None"/> when off).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal FileFilter Filter
    {
        get => Browser.FileFilter;
        set
        {
            _filterBar.Filter = value;
            Browser.FileFilter = value;
            if (value.IsActive)
                FilterBarVisible = true;
            UpdateStatus();
        }
    }

    /// <summary>
    /// Shows the filter bar. Hiding it also clears the filter, so a filter can never be active without being visible.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FilterBarVisible
    {
        get => _filterBarShown;
        set
        {
            // Own flag: Control.Visible is also false while a parent (e.g. a pane hidden by the layout) is hidden.
            if (_filterBarShown == value)
                return;

            _filterBarShown = value;
            _filterBar.Visible = value;
            if (!value && Browser.FileFilter.IsActive)
                Filter = FileFilter.None;
        }
    }

    /// <summary>Patterns offered by the filter box, newest first.</summary>
    internal void SetRecentFilters(IEnumerable<string> patterns) => _filterBar.SetHistory(patterns);

    /// <summary>Shows the filter bar and puts the caret in its pattern box.</summary>
    public void FocusFilter()
    {
        FilterBarVisible = true;
        _filterBar.FocusPattern();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool MultilineTabs
    {
        get => _tabStrip.Multiline;
        set => _tabStrip.Multiline = value;
    }

    /// <summary>Snapshot for saving: every tab's folder and view mode, and which tab is selected.</summary>
    public PaneState CaptureState()
    {
        // The selected tab's mode may have been changed from the shell's own menus since the last poll.
        if (Browser.IsHandleCreated)
        {
            _currentTab.ViewMode = Browser.ViewMode;
            _currentTab.IconSize = Browser.IconSize;
        }

        ShellLocation fallback = Workspace.DefaultLocation;
        return new PaneState(
            [.. Tabs.Select(tab => new TabState(tab.Location ?? fallback, tab.ViewMode, tab.IconSize))],
            _tabStrip.SelectedIndex,
            _treeEnabled,
            _treeSplit,
            Browser.FileFilter.IsActive ? Browser.FileFilter : null,
            _filterBarShown);
    }

    /// <summary>
    /// Replaces all tabs with a saved pane. Folders that no longer exist fall back to their nearest existing
    /// parent. The view opens the selected tab once, however many tabs were replaced.
    /// </summary>
    public void ApplyState(PaneState state)
    {
        List<PaneTab> tabs =
        [
            .. state.Tabs.Select(tab => new PaneTab(tab.Location.ExistingOrParent())
            {
                ViewMode = tab.ViewMode,
                IconSize = tab.IconSize,
            }),
        ];
        if (tabs.Count == 0)
            tabs.Add(new PaneTab(Workspace.DefaultLocation));

        List<PaneTab> old = [.. Tabs];
        _tabStrip.Replace(tabs, state.SelectedTab);
        _currentTab = _tabStrip.SelectedTab!;
        Browser.LoadState(_currentTab.History, _currentTab.ViewMode, _currentTab.IconSize);
        foreach (PaneTab tab in old)
            tab.Dispose();

        _treeSplit = Math.Clamp(state.TreeSplit, 500, 9500);
        TreeEnabled = state.TreeVisible;
        FilterBarVisible = state.FilterBarVisible;
        Filter = state.Filter ?? FileFilter.None;
        UpdateNavigationState();
    }

    public void FocusAddressBar()
    {
        _address.Focus();
        _address.SelectAll();
    }

    public void FocusBrowser() => Browser.Focus();

    /// <summary>Opens a tab next to the selected one, at <paramref name="location"/> or the current folder.</summary>
    public PaneTab NewTab(ShellLocation? location = null, bool select = true)
    {
        var tab = new PaneTab(location ?? _currentTab.Location ?? ShellLocation.Desktop)
        {
            ViewMode = _currentTab.ViewMode,
            IconSize = _currentTab.IconSize,
        };
        return InsertTab(tab, select);
    }

    /// <summary>Copies the selected tab including its back/forward history.</summary>
    public PaneTab DuplicateTab() => InsertTab(_currentTab.Clone(), select: true);

    /// <summary>Closes a tab; the last tab of a pane stays open.</summary>
    public void CloseTab(PaneTab tab)
    {
        if (Tabs.Count <= 1)
            return;

        _tabStrip.Remove(tab);
        tab.Dispose();
    }

    public void CloseOtherTabs(PaneTab keep)
    {
        foreach (PaneTab tab in Tabs.Where(tab => tab != keep).ToList())
            CloseTab(tab);
    }

    /// <summary>Selects the next (+1) or previous (-1) tab, wrapping around (Ctrl+Tab / Ctrl+Shift+Tab).</summary>
    public void SelectNextTab(int step)
    {
        int count = Tabs.Count;
        _tabStrip.SelectedIndex = ((_tabStrip.SelectedIndex + step) % count + count) % count;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Color color = _isActive ? SystemColors.Highlight : SystemColors.ControlLight;
        using var brush = new SolidBrush(color);
        Rectangle inner = DisplayRectangle;
        using var region = new Region(ClientRectangle);
        region.Exclude(inner);
        e.Graphics.FillRegion(brush, region);
    }

    /// <summary>Moves keyboard focus into this pane's tree (when shown).</summary>
    public void FocusTree() => _tree?.FocusTree();

    private void UpdateTree()
    {
        bool show = _treeEnabled && _treeAvailable;

        // Created on first use: panes that never show a tree never create the shell control.
        if (show && _tree is null)
        {
            _tree = new FolderTreeControl { Dock = DockStyle.Left, Options = _treeOptions };
            _tree.LocationSelected += (_, e) => Browser.Navigate(e.Location);
            _content.Controls.Add(_tree);
        }

        if (_tree is not null)
            _tree.Visible = show;
        _treeSplitter.Visible = show;
        if (show)
        {
            ApplyTreeWidth();
            SyncTree();
        }
    }

    /// <summary>Keeps the tree's selection on the folder the list shows.</summary>
    private void SyncTree()
    {
        if (_treeEnabled && _treeAvailable && Browser.CurrentLocation is { } location)
            _tree?.SelectLocation(location);
    }

    private void ApplyTreeWidth()
    {
        if (_tree is null || !_treeEnabled || !_treeAvailable)
            return;

        int available = _content.ClientSize.Width - _treeSplitter.Width;
        int width = Math.Clamp(available * _treeSplit / PaneLayout.RatioScale, _treeSplitter.MinSize, Math.Max(_treeSplitter.MinSize, available - _treeSplitter.MinExtra));
        if (_tree.Width == width)
            return;

        _applyingTreeWidth = true;
        try
        {
            _tree.Width = width;
        }
        finally
        {
            _applyingTreeWidth = false;
        }
    }

    private void OnTreeSplitterMoved()
    {
        int available = _content.ClientSize.Width - _treeSplitter.Width;
        if (!_applyingTreeWidth && _tree is not null && available > 0)
            _treeSplit = Math.Clamp(_tree.Width * PaneLayout.RatioScale / available, 500, 9500);
    }

    private PaneTab InsertTab(PaneTab tab, bool select)
    {
        _tabStrip.Insert(_tabStrip.SelectedIndex + 1, tab);
        if (select)
            _tabStrip.SelectedIndex = _tabStrip.SelectedIndex + 1;

        return tab;
    }

    private void OnSelectedTabChanged()
    {
        if (_tabStrip.SelectedTab is not { } tab || tab == _currentTab)
            return;

        // Switch first so the view-mode event raised by LoadState is recorded on the new tab.
        _currentTab = tab;
        Browser.LoadState(tab.History, tab.ViewMode, tab.IconSize);
        UpdateNavigationState();
    }

    private void ShowTabMenu(PaneTab? tab, Point location)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("새 탭(&N)", null, (_, _) => NewTab()) { ShortcutKeyDisplayString = "Ctrl+T" });
        if (tab is not null)
        {
            menu.Items.Add(new ToolStripMenuItem("탭 복제(&D)", null, (_, _) =>
            {
                _tabStrip.SelectedIndex = Tabs.ToList().IndexOf(tab);
                DuplicateTab();
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("탭 닫기(&C)", null, (_, _) => CloseTab(tab))
            {
                Enabled = Tabs.Count > 1,
                ShortcutKeyDisplayString = "Ctrl+W",
            });
            menu.Items.Add(new ToolStripMenuItem("다른 탭 모두 닫기(&O)", null, (_, _) => CloseOtherTabs(tab)) { Enabled = Tabs.Count > 1 });
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_tabStrip, location);
    }

    private Control BuildNavigationBar()
    {
        var left = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill, CanOverflow = false };
        left.Items.AddRange([_backButton, _forwardButton, _upButton]);

        var locations = new ToolStripDropDownButton(Glyphs.ChevronDown)
        {
            Font = Glyphs.Font,
            ToolTipText = "위치 (Ctrl+클릭: 새 탭)",
            ShowDropDownArrow = false,
        };
        locations.DropDownOpening += (_, _) => FillLocationsMenu(locations);
        locations.DropDownItems.Add(new ToolStripMenuItem("…"));

        var refresh = Glyphs.CreateButton(Glyphs.Refresh, "새로 고침 (F5)", (_, _) => Browser.RefreshView());
        var viewDropDown = new ToolStripDropDownButton(Glyphs.ViewList) { Font = Glyphs.Font, ToolTipText = "보기" };
        foreach (ViewPreset preset in ViewPreset.All)
            viewDropDown.DropDownItems.Add(new ToolStripMenuItem(preset.Name, null, (_, _) => Browser.SetViewMode(preset.Mode, preset.IconSize)) { Font = Font });

        var right = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill, CanOverflow = false };
        right.Items.AddRange([locations, refresh, viewDropDown]);

        var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 1 };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(_address, 1, 0);
        bar.Controls.Add(right, 2, 0);
        return bar;
    }

    /// <summary>Common folders and all drives, rebuilt on every opening so new drives show up.</summary>
    private void FillLocationsMenu(ToolStripDropDownButton button)
    {
        foreach (ToolStripItem old in button.DropDownItems)
            old.Image?.Dispose();
        button.DropDownItems.Clear();

        IEnumerable<ShellLocation?> folders = QuickLocations.Select(ShellLocation.Parse);
        IEnumerable<ShellLocation?> drives = DriveInfo.GetDrives().Select(drive => ShellLocation.Parse(drive.Name));
        AddLocations(folders);
        button.DropDownItems.Add(new ToolStripSeparator());
        AddLocations(drives);

        void AddLocations(IEnumerable<ShellLocation?> locations)
        {
            foreach (ShellLocation location in locations.OfType<ShellLocation>())
            {
                button.DropDownItems.Add(new ToolStripMenuItem(location.Name, location.GetSmallIcon(), (_, _) => OpenLocation(location))
                {
                    Font = Font,
                    ToolTipText = location.DisplayName,
                });
            }
        }
    }

    /// <summary>Navigates the current tab, or opens a new tab when Ctrl is held.</summary>
    private void OpenLocation(ShellLocation location)
    {
        if ((ModifierKeys & Keys.Control) != 0)
            NewTab(location);
        else
            Browser.Navigate(location);

        Browser.Focus();
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string text = _address.Text;
            ShellLocation? location = Browser.CurrentLocation is { } current && text == current.DisplayName
                ? current
                : ShellLocation.Parse(text);
            if (location is null)
            {
                MessageBox.Show(FindForm(), $"'{text}' 위치를 찾을 수 없습니다.", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Alt+Enter opens the address in a new tab, like a browser.
            if (e.Alt)
            {
                _address.Text = Browser.CurrentLocation?.DisplayName ?? string.Empty;
                NewTab(location);
            }
            else
            {
                Browser.Navigate(location);
            }

            Browser.Focus();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            _address.Text = Browser.CurrentLocation?.DisplayName ?? string.Empty;
            Browser.Focus();
        }
    }

    private void UpdateNavigationState()
    {
        _backButton.Enabled = Browser.CanGoBack;
        _forwardButton.Enabled = Browser.CanGoForward;
        _upButton.Enabled = Browser.CanGoUp;
        _address.Text = Browser.CurrentLocation?.DisplayName ?? string.Empty;
        UpdateStatus();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFilterFromBar()
    {
        Browser.FileFilter = _filterBar.Filter;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _itemsLabel.Text = Browser.FileFilter.IsActive ? $"항목 {Browser.ItemCount:N0}개 (필터됨)" : $"항목 {Browser.ItemCount:N0}개";
        _selectionLabel.Text = Browser.SelectedCount > 0 ? $"|  {Browser.SelectedCount:N0}개 선택됨" : string.Empty;
    }
}
