using MDir.Commands;
using MDir.Features;
using MDir.Integration;
using MDir.Layout;
using MDir.Localization;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;

namespace MDir;

/// <summary>
/// Main window: up to four Explorer panes arranged by a <see cref="PaneLayout"/>. Commands act on the active
/// pane, which is chosen by clicking into a pane or with F6 / Ctrl+Alt+1~4. The whole arrangement is a
/// <see cref="Workspace"/> that can be saved, opened and (with a profile) restored automatically.
/// Menus, toolbar and keyboard shortcuts are built from the command registry in MainForm.Commands.cs.
/// </summary>
internal sealed partial class MainForm : Form, IMessageFilter
{
    private const string WorkspaceFilter = "M-Dir 작업 공간 (*.mdr)|*.mdr|모든 파일 (*.*)|*.*";

    private readonly PaneControl[] _panes;
    private readonly PaneLayoutPanel _layoutPanel;
    private readonly AppProfile? _profile;
    private readonly Panel _workArea = new() { Dock = DockStyle.Fill };
    private readonly Splitter _sharedSplitter = new() { Dock = DockStyle.Left, Width = 5, MinSize = 80, MinExtra = 300, Visible = false };
    private readonly AppSettings _fallbackSettings = new();
    private FolderTreeControl? _sharedTree;
    private bool _sharedTreeMode;
    private bool _sharedTreeVisible;
    private int _sharedTreeSplit = Workspace.DefaultTreeSplit;
    private bool _applyingSharedWidth;
    private readonly FavoritesStore? _favorites;
    private readonly Splitter _previewSplitter = new() { Dock = DockStyle.Right, Width = 5, MinSize = 150, MinExtra = 300, Visible = false };
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _slideshowTimer = new() { Interval = 3000 };
    private PreviewPane? _previewPane;
    private bool _previewVisible;
    private int _previewSplit = Workspace.DefaultPreviewSplit;
    private bool _applyingPreviewWidth;
    private bool _multilineTabs;
    private string? _workspacePath;
    private PaneControl _activePane;

    /// <param name="startFolder">A folder from the command line: shown in pane 1 (the default workspace's first pane when there is no saved one).</param>
    /// <param name="workspace">Workspace to open; null starts the default four-pane layout.</param>
    /// <param name="workspacePath">The file <paramref name="workspace"/> came from, so Ctrl+S saves back to it.</param>
    /// <param name="profile">Settings and file locations; null turns automatic saving off (used by tests).</param>
    public MainForm(ShellLocation? startFolder = null, Workspace? workspace = null, string? workspacePath = null, AppProfile? profile = null)
    {
        Text = "M-Dir";
        Size = new Size(1280, 860);
        StartPosition = FormStartPosition.CenterScreen;
        _profile = profile;
        _workspacePath = workspacePath;
        _favorites = CreateFavoritesStore(profile);

        _panes = [.. Enumerable.Range(0, PaneLayout.MaxPanes).Select(i => new PaneControl(i))];
        _activePane = _panes[0];
        _activePane.IsActive = true;
        foreach (PaneControl pane in _panes)
        {
            pane.StateChanged += (_, _) =>
            {
                if (pane == _activePane)
                    UpdateActivePaneState();
            };
            pane.Enter += (_, _) => ActivatePane(pane, focus: false);
            pane.ViewFocused += (_, _) => ActivatePane(pane, focus: false);
            pane.SelectionChanged += (_, _) =>
            {
                if (pane == _activePane)
                {
                    SchedulePreview();
                    UpdateSizeForSelection();
                    RefreshCommandStates();
                }
            };
            pane.Favorites = _favorites;
            pane.Browser.FolderSizes = FolderSizeSource;
            pane.Browser.FolderSizeColumn = Settings.FolderSizeColumn;
            pane.ManageFavoritesRequested += (_, _) => ManageFavorites();
            pane.FilterCommitted += (_, pattern) => RememberFilter(pattern);
            pane.ColorRules = ActiveColorRules;
            pane.SetRecentFilters(Settings.RecentFilters);
        }

        _layoutPanel = new PaneLayoutPanel(_panes) { Dock = DockStyle.Fill };
        _layoutPanel.LayoutChanged += (_, _) => OnPaneLayoutChanged();

        InitializeToolHooks();
        RegisterCommands();
        MenuStrip menu = BuildMenu();
        ToolStrip toolbar = BuildToolbar();

        // Z-order matters for docking: the pane area (Fill) is last, then the splitters, then the shared tree (Left).
        // Top to bottom: menu, toolbar, work area (the later a control is added, the earlier it docks).
        _workArea.Controls.Add(_layoutPanel);
        _workArea.Controls.Add(_sharedSplitter);
        _workArea.Controls.Add(_previewSplitter);
        Controls.Add(_workArea);
        Controls.Add(toolbar);
        Controls.Add(menu);
        MainMenuStrip = menu;
        _workArea.BringToFront();
        _workArea.SizeChanged += (_, _) =>
        {
            ApplySharedTreeWidth();
            ApplyPreviewWidth();
        };
        _previewSplitter.SplitterMoved += (_, _) => OnPreviewSplitterMoved();
        _previewTimer.Tick += (_, _) => RefreshPreview();
        _slideshowTimer.Tick += (_, _) => OnSlideshowTick();

        // The clipboard and other programs change what Paste etc. can do without any event here, so the toolbar is polled.
        _stateTimer.Tick += (_, _) => RefreshCommandStates();
        _sharedSplitter.SplitterMoved += (_, _) => OnSharedSplitterMoved();

        ApplyWorkspace(workspace ?? Workspace.CreateDefault(startFolder ?? Workspace.DefaultLocation), applyWindow: true);
        if (workspace is not null && startFolder is not null)
        {
            // Opened with a folder (shell "open with" or the command line): show it in pane 1.
            _panes[0].Browser.Navigate(startFolder);
            ActivatePane(_panes[0], focus: false);
        }

        OnPaneLayoutChanged();
        UpdateActivePaneState();
        RefreshAllMenus();
        ApplyAppearance(processMode: false);
        ApplyUiScale();
        foreach (PaneControl pane in _panes)
            pane.ApplyLanguage();
    }

    internal string? WorkspacePath => _workspacePath;

    /// <summary>The current arrangement, ready to save.</summary>
    internal Workspace CaptureWorkspace()
    {
        // RestoreBounds is only filled in once the window has been moved or resized (a freshly centered window
        // reports -1,-1), so it is used just for the maximized case, where Bounds is the whole screen.
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        return new Workspace
        {
            Layout = _layoutPanel.CurrentLayout,
            Ratios = [.. _layoutPanel.Ratios],
            ActivePane = _activePane.Index,
            MultilineTabs = _multilineTabs,
            SharedTree = _sharedTreeMode,
            SharedTreeVisible = _sharedTreeVisible,
            SharedTreeSplit = _sharedTreeSplit,
            PreviewVisible = _previewVisible,
            PreviewSplit = _previewSplit,
            Window = new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized),
            Panes = [.. _panes.Select(pane => pane.CaptureState())],
        };
    }

    /// <summary>
    /// Replaces layout, tabs and active pane with <paramref name="workspace"/>. The window position is only
    /// applied at startup; opening a workspace file later leaves the window where the user put it.
    /// </summary>
    internal void ApplyWorkspace(Workspace workspace, bool applyWindow)
    {
        _layoutPanel.Restore(workspace.Layout, workspace.Ratios);

        // Decide where the tree lives before the panes load, so a shared-tree window never builds per-pane trees.
        _sharedTreeMode = workspace.SharedTree;
        _sharedTreeVisible = workspace.SharedTreeVisible;
        _sharedTreeSplit = workspace.SharedTreeSplit;
        for (int i = 0; i < _panes.Length; i++)
        {
            _panes[i].TreeAvailable = !_sharedTreeMode;
            _panes[i].TreeOptions = TreeSettings;
            _panes[i].ApplyState(workspace.Panes[i]);
        }

        UpdateSharedTree();
        _previewVisible = workspace.PreviewVisible;
        _previewSplit = workspace.PreviewSplit;
        UpdatePreview();
        SetMultilineTabs(workspace.MultilineTabs);
        if (applyWindow && workspace.Window is { } window)
            ApplyWindowPlacement(window);

        int active = _layoutPanel.IsPaneVisible(workspace.ActivePane) ? workspace.ActivePane : 0;
        ActivatePane(_panes[active], focus: false);
        OnPaneLayoutChanged();
        UpdateActivePaneState();
    }

    private void ApplyWindowPlacement(WindowPlacement window)
    {
        // A monitor may have been unplugged since the last run: only restore a position that is still on screen.
        var bounds = new Rectangle(window.X, window.Y, window.Width, window.Height);
        bool onScreen = Screen.AllScreens.Any(screen =>
        {
            Rectangle overlap = Rectangle.Intersect(screen.WorkingArea, bounds);
            return overlap.Width >= 100 && overlap.Height >= 50;
        });
        if (!onScreen)
            return;

        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (window.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    internal PaneLayoutPanel LayoutPanel => _layoutPanel;

    internal IReadOnlyList<PaneControl> Panes => _panes;

    internal PaneControl ActivePane => _activePane;

    private ExplorerBrowserHost ActiveBrowser => _activePane.Browser;

    /// <summary>Makes pane <paramref name="index"/> active and moves keyboard focus into it, if the layout shows it.</summary>
    internal void ActivatePane(int index)
    {
        if (_layoutPanel.IsPaneVisible(index))
            ActivatePane(_panes[index], focus: true);
    }

    /// <summary>Cycles through the visible panes (F6 / Shift+F6).</summary>
    internal void ActivateNextPane(int step)
    {
        int count = _layoutPanel.CurrentLayout.PaneCount;
        int next = ((_activePane.Index + step) % count + count) % count;
        ActivatePane(_panes[next], focus: true);
    }

    /// <summary>Clicking anywhere in a pane activates it, including inside the shell's own child windows.</summary>
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        if (m.Msg == ListScroll.WM_MOUSEWHEEL && ListScroll.TryScrollSideways(m.HWnd, m.WParam))
            return true;

        if (m.Msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
            or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN)
        {
            foreach (PaneControl pane in _panes)
            {
                if (pane.Visible && pane.IsHandleCreated && (m.HWnd == pane.Handle || NativeMethods.IsChild(pane.Handle, m.HWnd)))
                {
                    ActivatePane(pane, focus: false);
                    break;
                }
            }
        }

        return false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AppAppearance.SetTitleBar(Handle, IsDarkMode);
        Application.AddMessageFilter(this);
        _stateTimer.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _stateTimer.Stop();
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Each pane's folder was set by ApplyWorkspace; its shell view browses there when the pane is first shown.
        _activePane.FocusBrowser();
        if (_startMinimized)
        {
            _startMinimized = false;
            WindowState = FormWindowState.Minimized;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel && _profile is { Settings.AutoWorkspace: true })
        {
            try
            {
                CaptureWorkspace().Save(_profile.Paths.StartWorkspaceFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Closing must not fail because the profile folder is read-only; the next start uses the old file.
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewTimer.Dispose();
            _slideshowTimer.Dispose();
            _folderSizeColumn?.Dispose();
            _stateTimer.Dispose();
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

            _baseFont = null;
        }

        base.Dispose(disposing);
    }

    private AppSettings Settings => _profile?.Settings ?? _fallbackSettings;

    private TreeOptions TreeSettings => Settings.Tree;

    private static FavoritesStore? CreateFavoritesStore(AppProfile? profile)
    {
        if (profile is null)
            return null;

        var store = new FavoritesStore(profile.Paths.FavoritesDirectory);
        try
        {
            store.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return store;
    }

    private void ManageFavorites()
    {
        if (_favorites is null)
            return;

        using var dialog = new FavoritesDialog(_favorites);
        ShowLocalized(dialog);
    }

    internal PreviewPane? PreviewPane => _previewPane;

    internal bool PreviewVisible => _previewVisible;

    /// <summary>Shows or hides the preview panel (F12).</summary>
    internal void TogglePreview()
    {
        _previewVisible = !_previewVisible;
        UpdatePreview();
        RefreshCommandStates();
    }

    private void UpdatePreview()
    {
        if (_previewVisible && _previewPane is null)
        {
            // Created on first use; added last so it is docked first and the splitter finds it.
            _previewPane = new PreviewPane { Dock = DockStyle.Right, Width = 300 };
            Loc.Apply(_previewPane);
            _previewPane.NavigateRequested += (_, step) => StepPreviewItem(step, imagesOnly: false);
            _previewPane.SlideshowChanged += (_, _) => UpdateSlideshow();
            _workArea.Controls.Add(_previewPane);
        }

        if (_previewPane is not null)
            _previewPane.Visible = _previewVisible;
        _previewSplitter.Visible = _previewVisible;

        if (_previewVisible)
        {
            ApplyPreviewWidth();
            SchedulePreview();
        }
        else
        {
            // Hidden: stop work and let a preview handler release its file.
            _previewTimer.Stop();
            if (_previewPane is not null)
                _previewPane.SlideshowActive = false;
            _previewPane?.Clear();
        }
    }

    /// <summary>Previews the active pane's selection shortly after it stops changing (arrow-key scrolling must not load every file).</summary>
    private void SchedulePreview()
    {
        if (!_previewVisible)
            return;

        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async void RefreshPreview()
    {
        _previewTimer.Stop();
        if (!_previewVisible || _previewPane is not { } pane)
            return;

        try
        {
            ExplorerBrowserHost browser = ActiveBrowser;
            await pane.ShowAsync(browser.SelectedCount == 1 ? browser.GetFirstSelected() : null, browser.SelectedCount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A preview that fails must never take the window down; the pane shows its own message for known failures.
        }
    }

    /// <summary>Milliseconds between two pictures of the slideshow.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SlideshowIntervalMs
    {
        get => _slideshowTimer.Interval;
        set => _slideshowTimer.Interval = Math.Max(100, value);
    }

    private void UpdateSlideshow()
    {
        if (_previewPane?.SlideshowActive == true)
            _slideshowTimer.Start();
        else
            _slideshowTimer.Stop();
    }

    private void OnSlideshowTick()
    {
        if (!_previewVisible || _previewPane is not { SlideshowActive: true })
        {
            _slideshowTimer.Stop();
            return;
        }

        // Only pictures are shown; after the last one it starts again from the first.
        StepPreviewItem(1, imagesOnly: true, wrap: true);
    }

    /// <summary>
    /// Selects the item before or after the focused one in the active pane, which the preview then shows. With
    /// <paramref name="imagesOnly"/> other files are skipped; with <paramref name="wrap"/> the search goes on at the
    /// other end of the list. Returns false when there is nothing to move to.
    /// </summary>
    internal bool StepPreviewItem(int step, bool imagesOnly, bool wrap = false)
    {
        if (CurrentFolderPath is null || step == 0)
            return false;

        IReadOnlyList<string> paths = ActiveBrowser.GetAllItemPaths();
        int count = paths.Count;
        if (count == 0)
            return false;

        int start = ActiveBrowser.FocusedItemIndex;
        for (int i = 1; i <= count; i++)
        {
            int index = start < 0 ? (step > 0 ? i - 1 : count - i) : start + Math.Sign(step) * i;
            if (wrap)
                index = (index % count + count) % count;
            else if (index < 0 || index >= count)
                return false;

            if (!imagesOnly || PreviewPane.IsImagePath(paths[index]))
                return ActiveBrowser.SelectItem(index);
        }

        return false;
    }

    private void ApplyPreviewWidth()
    {
        if (_previewPane is null || !_previewVisible)
            return;

        int available = _workArea.ClientSize.Width - _previewSplitter.Width;
        int width = Math.Clamp(available * _previewSplit / PaneLayout.RatioScale, _previewSplitter.MinSize,
            Math.Max(_previewSplitter.MinSize, available - _previewSplitter.MinExtra));
        if (_previewPane.Width == width)
            return;

        _applyingPreviewWidth = true;
        try
        {
            _previewPane.Width = width;
        }
        finally
        {
            _applyingPreviewWidth = false;
        }
    }

    private void OnPreviewSplitterMoved()
    {
        int available = _workArea.ClientSize.Width - _previewSplitter.Width;
        if (!_applyingPreviewWidth && _previewPane is not null && available > 0)
            _previewSplit = Math.Clamp(_previewPane.Width * PaneLayout.RatioScale / available, 500, 9500);
    }

    /// <summary>The colour rules the panes use right now: null while colouring is switched off.</summary>
    private ColorRuleSet? ActiveColorRules => Settings.ColorFilterEnabled ? Settings.ColorRules : null;

    /// <summary>Shows the active pane's filter bar (and focuses it), or hides it, which also clears its filter.</summary>
    internal void ToggleFilter()
    {
        if (_activePane.FilterBarVisible)
        {
            _activePane.FilterBarVisible = false;
            _activePane.FocusBrowser();
        }
        else
        {
            _activePane.FocusFilter();
        }

        RefreshCommandStates();
    }

    internal void SetColorFilterEnabled(bool enabled)
    {
        Settings.ColorFilterEnabled = enabled;
        ApplyColorRules();
        SaveSettings();
    }

    private void EditColorFilter()
    {
        using var dialog = new ColorFilterDialog(Settings.ColorRules);
        if (ShowLocalized(dialog) != DialogResult.OK)
            return;

        Settings.ColorRules = dialog.Result;
        ApplyColorRules();
        SaveSettings();
    }

    private void ApplyColorRules()
    {
        foreach (PaneControl pane in _panes)
            pane.ColorRules = ActiveColorRules;
        if (_sharedTree is not null)
            _sharedTree.ColorRules = ActiveColorRules;

        RefreshCommandStates();
    }

    private void RememberFilter(string pattern)
    {
        Settings.AddRecentFilter(pattern);
        foreach (PaneControl pane in _panes)
            pane.SetRecentFilters(Settings.RecentFilters);

        SaveSettings();
    }

    /// <summary>Shows or hides the tree: the active pane's own tree, or the shared one in shared mode.</summary>
    internal void ToggleTree()
    {
        if (_sharedTreeMode)
        {
            _sharedTreeVisible = !_sharedTreeVisible;
            UpdateSharedTree();
        }
        else
        {
            _activePane.TreeEnabled = !_activePane.TreeEnabled;
        }

        RefreshCommandStates();
    }

    /// <summary>Switches between a tree per pane and one tree for the window; each mode keeps its own on/off state.</summary>
    internal void SetSharedTreeMode(bool shared)
    {
        if (_sharedTreeMode == shared)
            return;

        _sharedTreeMode = shared;
        foreach (PaneControl pane in _panes)
            pane.TreeAvailable = !shared;

        UpdateSharedTree();
        RefreshCommandStates();
    }

    internal FolderTreeControl? SharedTree => _sharedTree;

    internal bool SharedTreeMode => _sharedTreeMode;

    private bool IsTreeShown => _sharedTreeMode ? _sharedTreeVisible : _activePane.TreeEnabled;

    private void ApplyTreeOptions()
    {
        foreach (PaneControl pane in _panes)
            pane.TreeOptions = TreeSettings;
        if (_sharedTree is not null)
            _sharedTree.Options = TreeSettings;

        RefreshCommandStates();
        SaveSettings();
    }

    private void UpdateSharedTree()
    {
        bool show = _sharedTreeMode && _sharedTreeVisible;
        if (show && _sharedTree is null)
        {
            _sharedTree = new FolderTreeControl { Dock = DockStyle.Left, Options = TreeSettings, DarkMode = IsDarkMode, ColorRules = ActiveColorRules };
            _sharedTree.LocationSelected += (_, e) => ActiveBrowser.Navigate(e.Location);
            _workArea.Controls.Add(_sharedTree);
        }

        if (_sharedTree is not null)
            _sharedTree.Visible = show;
        _sharedSplitter.Visible = show;
        if (show)
        {
            ApplySharedTreeWidth();
            SyncSharedTree();
        }
    }

    /// <summary>The shared tree follows the active pane's current folder.</summary>
    private void SyncSharedTree()
    {
        if (_sharedTreeMode && _sharedTreeVisible && ActiveBrowser.CurrentLocation is { } location)
            _sharedTree?.SelectLocation(location);
    }

    private void ApplySharedTreeWidth()
    {
        if (_sharedTree is null || !_sharedTreeMode || !_sharedTreeVisible)
            return;

        int available = _workArea.ClientSize.Width - _sharedSplitter.Width;
        int width = Math.Clamp(available * _sharedTreeSplit / PaneLayout.RatioScale, _sharedSplitter.MinSize,
            Math.Max(_sharedSplitter.MinSize, available - _sharedSplitter.MinExtra));
        if (_sharedTree.Width == width)
            return;

        _applyingSharedWidth = true;
        try
        {
            _sharedTree.Width = width;
        }
        finally
        {
            _applyingSharedWidth = false;
        }
    }

    private void OnSharedSplitterMoved()
    {
        int available = _workArea.ClientSize.Width - _sharedSplitter.Width;
        if (!_applyingSharedWidth && _sharedTree is not null && available > 0)
            _sharedTreeSplit = Math.Clamp(_sharedTree.Width * PaneLayout.RatioScale / available, 500, 9500);
    }

    internal bool MultilineTabs => _multilineTabs;

    internal void SetMultilineTabs(bool multiline)
    {
        _multilineTabs = multiline;
        foreach (PaneControl pane in _panes)
            pane.MultilineTabs = multiline;
    }

    private void NewTab()
    {
        _activePane.NewTab();
        _activePane.FocusBrowser();
    }

    private void NewWorkspace()
    {
        ApplyWorkspace(Workspace.CreateDefault(Workspace.DefaultLocation), applyWindow: false);
        _workspacePath = null;
        _activePane.FocusBrowser();
    }

    private void OpenWorkspace()
    {
        using var dialog = new OpenFileDialog { Title = Loc.T("작업 공간 열기"), Filter = Loc.T(WorkspaceFilter), DefaultExt = "mdr" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (Workspace.Load(dialog.FileName) is not { } workspace)
        {
            MessageBox.Show(this, $"'{dialog.FileName}' 파일은 M-Dir 작업 공간이 아니거나 읽을 수 없습니다.", "M-Dir",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplyWorkspace(workspace, applyWindow: false);
        _workspacePath = dialog.FileName;
        _activePane.FocusBrowser();
    }

    private void SaveWorkspace()
    {
        if (_workspacePath is null)
            SaveWorkspaceAs();
        else
            WriteWorkspace(_workspacePath);
    }

    private void SaveWorkspaceAs()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Loc.T("작업 공간 저장"),
            Filter = Loc.T(WorkspaceFilter),
            DefaultExt = "mdr",
            AddExtension = true,
            FileName = _workspacePath is null ? "workspace.mdr" : Path.GetFileName(_workspacePath),
            InitialDirectory = _workspacePath is null ? _profile?.Paths.DataDirectory ?? string.Empty : Path.GetDirectoryName(_workspacePath) ?? string.Empty,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK && WriteWorkspace(dialog.FileName))
            _workspacePath = dialog.FileName;
    }

    private bool WriteWorkspace(string path)
    {
        try
        {
            CaptureWorkspace().Save(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"작업 공간을 저장하지 못했습니다.\n{ex.Message}", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void SetAutoWorkspace(bool enabled)
    {
        if (_profile is null)
            return;

        _profile.Settings.AutoWorkspace = enabled;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _profile?.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"설정을 저장하지 못했습니다.\n{ex.Message}", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ActivatePane(PaneControl pane, bool focus)
    {
        if (pane != _activePane)
        {
            _activePane.IsActive = false;
            _activePane = pane;
            pane.IsActive = true;
            UpdateActivePaneState();
            SchedulePreview();
        }

        if (focus)
            pane.FocusBrowser();
    }

    private void OnPaneLayoutChanged()
    {
        if (!_layoutPanel.IsPaneVisible(_activePane.Index))
            ActivatePane(_panes[0], focus: true);

        RefreshCommandStates();
    }

    private void UpdateActivePaneState()
    {
        ExplorerBrowserHost browser = ActiveBrowser;
        string name = browser.CurrentLocation?.DisplayName ?? string.Empty;
        Text = name.Length > 0 ? $"{name} - M-Dir" : "M-Dir";

        SyncSharedTree();
        RefreshCommandStates();
    }
}
