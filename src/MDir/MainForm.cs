using MDir.Layout;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;

namespace MDir;

/// <summary>
/// Main window: up to four Explorer panes arranged by a <see cref="PaneLayout"/>. Menu commands act on the
/// active pane, which is chosen by clicking into a pane or with F6 / Ctrl+Alt+1~4. The whole arrangement is a
/// <see cref="Workspace"/> that can be saved, opened and (with a profile) restored automatically.
/// </summary>
internal sealed class MainForm : Form, IMessageFilter
{
    private const string WorkspaceFilter = "M-Dir 작업 공간 (*.mdr)|*.mdr|모든 파일 (*.*)|*.*";

    private readonly PaneControl[] _panes;
    private readonly PaneLayoutPanel _layoutPanel;
    private readonly AppProfile? _profile;
    private ToolStripMenuItem _multilineMenu = null!;
    private ToolStripMenuItem _autoWorkspaceMenu = null!;
    private string? _workspacePath;
    private readonly ToolStripMenuItem _backMenu;
    private readonly ToolStripMenuItem _forwardMenu;
    private readonly ToolStripMenuItem _upMenu;
    private readonly ToolStripMenuItem[] _paneMenus = new ToolStripMenuItem[PaneLayout.MaxPanes];
    private readonly List<(ViewPreset Preset, ToolStripMenuItem Item)> _viewItems = [];
    private readonly List<(PaneLayout Layout, ToolStripMenuItem Item)> _layoutItems = [];
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
        }

        _layoutPanel = new PaneLayoutPanel(_panes) { Dock = DockStyle.Fill };
        _layoutPanel.LayoutChanged += (_, _) => OnPaneLayoutChanged();

        _backMenu = new ToolStripMenuItem("뒤로(&B)", null, (_, _) => ActiveBrowser.GoBack(), Keys.Alt | Keys.Left);
        _forwardMenu = new ToolStripMenuItem("앞으로(&F)", null, (_, _) => ActiveBrowser.GoForward(), Keys.Alt | Keys.Right);
        _upMenu = new ToolStripMenuItem("상위 폴더(&U)", null, (_, _) => ActiveBrowser.GoUp(), Keys.Alt | Keys.Up);

        var menu = BuildMenu();
        Controls.Add(_layoutPanel);
        Controls.Add(menu);
        MainMenuStrip = menu;
        _layoutPanel.BringToFront();

        ApplyWorkspace(workspace ?? Workspace.CreateDefault(startFolder ?? Workspace.DefaultLocation), applyWindow: true);
        if (workspace is not null && startFolder is not null)
        {
            // Opened with a folder (shell "open with" or the command line): show it in pane 1.
            _panes[0].Browser.Navigate(startFolder);
            ActivatePane(_panes[0], focus: false);
        }

        OnPaneLayoutChanged();
        UpdateActivePaneState();
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
            MultilineTabs = _multilineMenu.Checked,
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
        for (int i = 0; i < _panes.Length; i++)
            _panes[i].ApplyState(workspace.Panes[i]);

        _multilineMenu.Checked = workspace.MultilineTabs;
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
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Each pane's folder was set by ApplyWorkspace; its shell view browses there when the pane is first shown.
        _activePane.FocusBrowser();
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Address bar shortcuts (F4 as in Q-Dir, plus Explorer's Alt+D / Ctrl+L).
        if (keyData is Keys.F4 or (Keys.Alt | Keys.D) or (Keys.Control | Keys.L))
        {
            _activePane.FocusAddressBar();
            return true;
        }

        // Alternative tab keys: Q-Dir's Ctrl+Alt+A for a new tab, and Ctrl+PageDown/PageUp to switch.
        switch (keyData)
        {
            case Keys.Control | Keys.Alt | Keys.A:
                NewTab();
                return true;
            case Keys.Control | Keys.PageDown:
                _activePane.SelectNextTab(1);
                return true;
            case Keys.Control | Keys.PageUp:
                _activePane.SelectNextTab(-1);
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private MenuStrip BuildMenu()
    {
        var file = new ToolStripMenuItem("파일(&F)");
        file.DropDownItems.Add(new ToolStripMenuItem("새 작업 공간(&N)", null, (_, _) => NewWorkspace(), Keys.Control | Keys.N));
        file.DropDownItems.Add(new ToolStripMenuItem("작업 공간 열기(&O)...", null, (_, _) => OpenWorkspace(), Keys.Control | Keys.O));
        file.DropDownItems.Add(new ToolStripMenuItem("작업 공간 저장(&S)", null, (_, _) => SaveWorkspace(), Keys.Control | Keys.S));
        file.DropDownItems.Add(new ToolStripMenuItem("다른 이름으로 저장(&A)...", null, (_, _) => SaveWorkspaceAs()));
        file.DropDownItems.Add(new ToolStripSeparator());
        _autoWorkspaceMenu = new ToolStripMenuItem("종료 시 저장, 시작 시 복원(&W)")
        {
            CheckOnClick = true,
            Checked = _profile?.Settings.AutoWorkspace ?? false,
            Enabled = _profile is not null,
        };
        _autoWorkspaceMenu.CheckedChanged += (_, _) => SetAutoWorkspace(_autoWorkspaceMenu.Checked);
        file.DropDownItems.Add(_autoWorkspaceMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("끝내기(&X)", null, (_, _) => Close(), Keys.Alt | Keys.F4));

        var view = new ToolStripMenuItem("보기(&V)");
        foreach (ViewPreset preset in ViewPreset.All)
        {
            var item = new ToolStripMenuItem(preset.Name, null, (_, _) => ActiveBrowser.SetViewMode(preset.Mode, preset.IconSize));
            _viewItems.Add((preset, item));
            view.DropDownItems.Add(item);
        }

        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("새로 고침(&R)", null, (_, _) => ActiveBrowser.RefreshView(), Keys.F5));

        var go = new ToolStripMenuItem("이동(&G)");
        go.DropDownItems.AddRange([_backMenu, _forwardMenu, _upMenu]);
        go.DropDownItems.Add(new ToolStripSeparator());
        go.DropDownItems.Add(new ToolStripMenuItem("주소창(&A)", null, (_, _) => _activePane.FocusAddressBar())
        {
            ShortcutKeyDisplayString = "F4",
        });

        // "&L" also gives Q-Dir's Alt+L layout shortcut.
        var layout = new ToolStripMenuItem("레이아웃(&L)");
        int iconSize = LogicalToDeviceUnits(16);
        foreach (PaneLayout paneLayout in PaneLayout.All)
        {
            var item = new ToolStripMenuItem(paneLayout.Name, LayoutIcon.Create(paneLayout, iconSize),
                (_, _) => _layoutPanel.CurrentLayout = paneLayout);
            _layoutItems.Add((paneLayout, item));
            layout.DropDownItems.Add(item);
        }

        layout.DropDownItems.Add(new ToolStripSeparator());
        layout.DropDownItems.Add(new ToolStripMenuItem("다음 패널(&N)", null, (_, _) => ActivateNextPane(1), Keys.F6));
        layout.DropDownItems.Add(new ToolStripMenuItem("이전 패널(&P)", null, (_, _) => ActivateNextPane(-1), Keys.Shift | Keys.F6));
        for (int i = 0; i < PaneLayout.MaxPanes; i++)
        {
            int index = i;
            _paneMenus[i] = new ToolStripMenuItem($"패널 {i + 1}(&{i + 1})", null, (_, _) => ActivatePane(index),
                Keys.Control | Keys.Alt | (Keys.D1 + i));
            layout.DropDownItems.Add(_paneMenus[i]);
        }

        var tabs = new ToolStripMenuItem("탭(&T)");
        tabs.DropDownItems.Add(new ToolStripMenuItem("새 탭(&N)", null, (_, _) => NewTab(), Keys.Control | Keys.T));
        tabs.DropDownItems.Add(new ToolStripMenuItem("탭 복제(&D)", null, (_, _) => _activePane.DuplicateTab()));
        tabs.DropDownItems.Add(new ToolStripMenuItem("탭 닫기(&C)", null, (_, _) => _activePane.CloseTab(_activePane.SelectedTab),
            Keys.Control | Keys.W));
        tabs.DropDownItems.Add(new ToolStripSeparator());
        tabs.DropDownItems.Add(new ToolStripMenuItem("다음 탭", null, (_, _) => _activePane.SelectNextTab(1), Keys.Control | Keys.Tab));
        tabs.DropDownItems.Add(new ToolStripMenuItem("이전 탭", null, (_, _) => _activePane.SelectNextTab(-1),
            Keys.Control | Keys.Shift | Keys.Tab));
        tabs.DropDownItems.Add(new ToolStripSeparator());
        _multilineMenu = new ToolStripMenuItem("여러 줄 탭(&M)") { CheckOnClick = true };
        _multilineMenu.CheckedChanged += (_, _) =>
        {
            foreach (PaneControl pane in _panes)
                pane.MultilineTabs = _multilineMenu.Checked;
        };
        tabs.DropDownItems.Add(_multilineMenu);

        var menu = new MenuStrip { Dock = DockStyle.Top };
        menu.Items.AddRange([file, view, go, tabs, layout]);
        return menu;
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
        using var dialog = new OpenFileDialog { Title = "작업 공간 열기", Filter = WorkspaceFilter, DefaultExt = "mdr" };
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
            Title = "작업 공간 저장",
            Filter = WorkspaceFilter,
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
        try
        {
            _profile.SaveSettings();
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
        }

        if (focus)
            pane.FocusBrowser();
    }

    private void OnPaneLayoutChanged()
    {
        PaneLayout current = _layoutPanel.CurrentLayout;
        foreach (var (paneLayout, item) in _layoutItems)
            item.Checked = paneLayout == current;
        for (int i = 0; i < _paneMenus.Length; i++)
            _paneMenus[i].Enabled = _layoutPanel.IsPaneVisible(i);

        if (!_layoutPanel.IsPaneVisible(_activePane.Index))
            ActivatePane(_panes[0], focus: true);
    }

    private void UpdateActivePaneState()
    {
        ExplorerBrowserHost browser = ActiveBrowser;
        _backMenu.Enabled = browser.CanGoBack;
        _forwardMenu.Enabled = browser.CanGoForward;
        _upMenu.Enabled = browser.CanGoUp;
        foreach (var (preset, item) in _viewItems)
            item.Checked = preset.Matches(browser.ViewMode, browser.IconSize);

        string name = browser.CurrentLocation?.DisplayName ?? string.Empty;
        Text = name.Length > 0 ? $"{name} - M-Dir" : "M-Dir";
    }
}
