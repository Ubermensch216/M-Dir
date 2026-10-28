using MDir.Layout;
using MDir.Panes;
using MDir.Shell;

namespace MDir;

/// <summary>
/// Main window: up to four Explorer panes arranged by a <see cref="PaneLayout"/>. Menu commands act on the
/// active pane, which is chosen by clicking into a pane or with F6 / Ctrl+Alt+1~4.
/// </summary>
internal sealed class MainForm : Form, IMessageFilter
{
    private readonly PaneControl[] _panes;
    private readonly PaneLayoutPanel _layoutPanel;
    private readonly ShellLocation[] _startLocations;
    private readonly ToolStripMenuItem _backMenu;
    private readonly ToolStripMenuItem _forwardMenu;
    private readonly ToolStripMenuItem _upMenu;
    private readonly ToolStripMenuItem[] _paneMenus = new ToolStripMenuItem[PaneLayout.MaxPanes];
    private readonly List<(ViewPreset Preset, ToolStripMenuItem Item)> _viewItems = [];
    private readonly List<(PaneLayout Layout, ToolStripMenuItem Item)> _layoutItems = [];
    private PaneControl _activePane;

    public MainForm(ShellLocation startLocation)
    {
        Text = "M-Dir";
        Size = new Size(1280, 860);
        StartPosition = FormStartPosition.CenterScreen;

        // Pane 1 opens the requested folder; the others start at common folders.
        _startLocations =
        [
            startLocation,
            ShellLocation.Parse("shell:Personal") ?? startLocation,
            ShellLocation.Parse("shell:Downloads") ?? startLocation,
            // "shell:Desktop" is the namespace root; the folder with the desktop's files is DesktopDirectory.
            ShellLocation.Parse(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) ?? startLocation,
        ];

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

        OnPaneLayoutChanged();
        UpdateActivePaneState();
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

        // Hidden panes keep the location pending and browse when the layout first shows them.
        for (int i = 0; i < _panes.Length; i++)
            _panes[i].Browser.Navigate(_startLocations[i]);

        _activePane.FocusBrowser();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Address bar shortcuts (F4 as in Q-Dir, plus Explorer's Alt+D / Ctrl+L).
        if (keyData is Keys.F4 or (Keys.Alt | Keys.D) or (Keys.Control | Keys.L))
        {
            _activePane.FocusAddressBar();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private MenuStrip BuildMenu()
    {
        var file = new ToolStripMenuItem("파일(&F)");
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

        var menu = new MenuStrip { Dock = DockStyle.Top };
        menu.Items.AddRange([file, view, go, layout]);
        return menu;
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
