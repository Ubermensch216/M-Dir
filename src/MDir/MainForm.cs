using MDir.Panes;
using MDir.Shell;

namespace MDir;

/// <summary>Stage-1 main window: one Explorer pane with an address bar, navigation buttons and a status bar.</summary>
internal sealed class MainForm : Form
{
    private const string GlyphFont = "Segoe Fluent Icons";
    private const string GlyphFallbackFont = "Segoe MDL2 Assets";

    private readonly ExplorerBrowserHost _browser = new() { Dock = DockStyle.Fill };
    private readonly TextBox _address = new() { Dock = DockStyle.Fill, Margin = new Padding(4, 5, 4, 3) };
    private readonly ToolStripButton _backButton;
    private readonly ToolStripButton _forwardButton;
    private readonly ToolStripButton _upButton;
    private readonly ToolStripMenuItem _backMenu;
    private readonly ToolStripMenuItem _forwardMenu;
    private readonly ToolStripMenuItem _upMenu;
    private readonly List<(ViewPreset Preset, ToolStripMenuItem Item)> _viewItems = [];
    private readonly ToolStripStatusLabel _itemsLabel = new();
    private readonly ToolStripStatusLabel _selectionLabel = new();
    private readonly ShellLocation _startLocation;

    public MainForm(ShellLocation startLocation)
    {
        _startLocation = startLocation;
        Text = "M-Dir";
        Size = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;

        Font glyphs = CreateGlyphFont();
        _backButton = CreateGlyphButton("", "뒤로 (Alt+←)", glyphs, (_, _) => _browser.GoBack());
        _forwardButton = CreateGlyphButton("", "앞으로 (Alt+→)", glyphs, (_, _) => _browser.GoForward());
        _upButton = CreateGlyphButton("", "상위 폴더 (Alt+↑)", glyphs, (_, _) => _browser.GoUp());
        _backMenu = new ToolStripMenuItem("뒤로(&B)", null, (_, _) => _browser.GoBack(), Keys.Alt | Keys.Left);
        _forwardMenu = new ToolStripMenuItem("앞으로(&F)", null, (_, _) => _browser.GoForward(), Keys.Alt | Keys.Right);
        _upMenu = new ToolStripMenuItem("상위 폴더(&U)", null, (_, _) => _browser.GoUp(), Keys.Alt | Keys.Up);

        var menu = BuildMenu();
        var navigationBar = BuildNavigationBar(glyphs);
        var status = new StatusStrip();
        status.Items.AddRange([_itemsLabel, _selectionLabel]);

        Controls.Add(_browser);
        Controls.Add(navigationBar);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;
        _browser.BringToFront();

        _address.KeyDown += OnAddressKeyDown;
        _address.HandleCreated += (_, _) => NativeMethods.SHAutoComplete(_address.Handle,
            NativeMethods.SHACF_FILESYS_DIRS | NativeMethods.SHACF_AUTOSUGGEST_FORCE_ON | NativeMethods.SHACF_AUTOAPPEND_FORCE_OFF);
        _browser.NavigationCompleted += (_, _) => UpdateNavigationState();
        _browser.NavigationFailed += (_, e) => MessageBox.Show(this,
            $"'{e.Location.DisplayName}' 위치를 열 수 없습니다.", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _browser.ItemsChanged += (_, _) => UpdateStatus();
        _browser.ViewModeChanged += (_, _) => UpdateViewChecks();

        UpdateNavigationState();
        UpdateViewChecks();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _browser.Navigate(_startLocation);
        _browser.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Address bar shortcuts (F4 as in Q-Dir, plus Explorer's Alt+D / Ctrl+L).
        if (keyData is Keys.F4 or (Keys.Alt | Keys.D) or (Keys.Control | Keys.L))
        {
            _address.Focus();
            _address.SelectAll();
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
            var item = new ToolStripMenuItem(preset.Name, null, (_, _) => _browser.SetViewMode(preset.Mode, preset.IconSize));
            _viewItems.Add((preset, item));
            view.DropDownItems.Add(item);
        }

        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("새로 고침(&R)", null, (_, _) => _browser.RefreshView(), Keys.F5));

        var go = new ToolStripMenuItem("이동(&G)");
        go.DropDownItems.AddRange([_backMenu, _forwardMenu, _upMenu]);
        go.DropDownItems.Add(new ToolStripSeparator());
        go.DropDownItems.Add(new ToolStripMenuItem("주소창(&A)", null, (_, _) => { _address.Focus(); _address.SelectAll(); })
        {
            ShortcutKeyDisplayString = "F4",
        });

        var menu = new MenuStrip { Dock = DockStyle.Top };
        menu.Items.AddRange([file, view, go]);
        return menu;
    }

    private Control BuildNavigationBar(Font glyphs)
    {
        var left = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill, CanOverflow = false };
        left.Items.AddRange([_backButton, _forwardButton, _upButton]);

        var refresh = CreateGlyphButton("", "새로 고침 (F5)", glyphs, (_, _) => _browser.RefreshView());
        var viewDropDown = new ToolStripDropDownButton("") { Font = glyphs, ToolTipText = "보기" };
        foreach (ViewPreset preset in ViewPreset.All)
            viewDropDown.DropDownItems.Add(new ToolStripMenuItem(preset.Name, null, (_, _) => _browser.SetViewMode(preset.Mode, preset.IconSize)) { Font = Font });

        var right = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill, CanOverflow = false };
        right.Items.AddRange([refresh, viewDropDown]);

        var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 1 };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(_address, 1, 0);
        bar.Controls.Add(right, 2, 0);
        return bar;
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string text = _address.Text;
            if (_browser.CurrentLocation is { } current && text == current.DisplayName)
                _browser.Navigate(current);
            else if (!_browser.Navigate(text))
                MessageBox.Show(this, $"'{text}' 위치를 찾을 수 없습니다.", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                _browser.Focus();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            _address.Text = _browser.CurrentLocation?.DisplayName ?? string.Empty;
            _browser.Focus();
        }
    }

    private void UpdateNavigationState()
    {
        _backButton.Enabled = _backMenu.Enabled = _browser.CanGoBack;
        _forwardButton.Enabled = _forwardMenu.Enabled = _browser.CanGoForward;
        _upButton.Enabled = _upMenu.Enabled = _browser.CanGoUp;

        string name = _browser.CurrentLocation?.DisplayName ?? string.Empty;
        _address.Text = name;
        Text = name.Length > 0 ? $"{name} - M-Dir" : "M-Dir";
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _itemsLabel.Text = $"항목 {_browser.ItemCount:N0}개";
        _selectionLabel.Text = _browser.SelectedCount > 0 ? $"|  {_browser.SelectedCount:N0}개 선택됨" : string.Empty;
    }

    private void UpdateViewChecks()
    {
        foreach (var (preset, item) in _viewItems)
            item.Checked = preset.Matches(_browser.ViewMode, _browser.IconSize);
    }

    private static Font CreateGlyphFont()
    {
        using var probe = new Font(GlyphFont, 10f);
        return new Font(probe.Name == GlyphFont ? GlyphFont : GlyphFallbackFont, 10f);
    }

    private static ToolStripButton CreateGlyphButton(string glyph, string toolTip, Font font, EventHandler onClick) =>
        new(glyph, null, onClick) { Font = font, ToolTipText = toolTip, DisplayStyle = ToolStripItemDisplayStyle.Text };
}
