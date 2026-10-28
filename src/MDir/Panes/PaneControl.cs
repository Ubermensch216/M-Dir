using System.ComponentModel;
using MDir.Shell;

namespace MDir.Panes;

/// <summary>
/// One pane of the main window: navigation bar (back/forward/up, address, refresh, view), the Explorer view
/// and a status bar. The active pane draws a highlighted border; menu commands act on it.
/// </summary>
internal sealed class PaneControl : Panel
{
    private const int LogicalBorder = 2;

    private readonly TextBox _address = new() { Dock = DockStyle.Fill, Margin = new Padding(4, 5, 4, 3) };
    private readonly ToolStripButton _backButton;
    private readonly ToolStripButton _forwardButton;
    private readonly ToolStripButton _upButton;
    private readonly ToolStripStatusLabel _itemsLabel = new();
    private readonly ToolStripStatusLabel _selectionLabel = new();
    private bool _isActive;

    public PaneControl(int index)
    {
        Index = index;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Padding = new Padding(LogicalToDeviceUnits(LogicalBorder));

        Font glyphs = Glyphs.Font;
        _backButton = Glyphs.CreateButton("", "뒤로 (Alt+←)", (_, _) => Browser.GoBack());
        _forwardButton = Glyphs.CreateButton("", "앞으로 (Alt+→)", (_, _) => Browser.GoForward());
        _upButton = Glyphs.CreateButton("", "상위 폴더 (Alt+↑)", (_, _) => Browser.GoUp());

        var status = new StatusStrip { SizingGrip = false };
        status.Items.AddRange([_itemsLabel, _selectionLabel]);

        Controls.Add(Browser);
        Controls.Add(BuildNavigationBar(glyphs));
        Controls.Add(status);
        Browser.BringToFront();

        _address.KeyDown += OnAddressKeyDown;
        _address.HandleCreated += (_, _) => NativeMethods.SHAutoComplete(_address.Handle,
            NativeMethods.SHACF_FILESYS_DIRS | NativeMethods.SHACF_AUTOSUGGEST_FORCE_ON | NativeMethods.SHACF_AUTOAPPEND_FORCE_OFF);
        Browser.NavigationCompleted += (_, _) => UpdateNavigationState();
        Browser.NavigationFailed += (_, e) => MessageBox.Show(FindForm(),
            $"'{e.Location.DisplayName}' 위치를 열 수 없습니다.", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Browser.ItemsChanged += (_, _) => UpdateStatus();
        Browser.ViewModeChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);

        UpdateNavigationState();
    }

    /// <summary>Raised when the folder, history, counts or view mode change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Zero-based pane number (0 = top-left).</summary>
    public int Index { get; }

    public ExplorerBrowserHost Browser { get; } = new() { Dock = DockStyle.Fill };

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;

            _isActive = value;
            Invalidate();
        }
    }

    public void FocusAddressBar()
    {
        _address.Focus();
        _address.SelectAll();
    }

    public void FocusBrowser() => Browser.Focus();

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

    private Control BuildNavigationBar(Font glyphs)
    {
        var left = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill, CanOverflow = false };
        left.Items.AddRange([_backButton, _forwardButton, _upButton]);

        var refresh = Glyphs.CreateButton("", "새로 고침 (F5)", (_, _) => Browser.RefreshView());
        var viewDropDown = new ToolStripDropDownButton("") { Font = glyphs, ToolTipText = "보기" };
        foreach (ViewPreset preset in ViewPreset.All)
            viewDropDown.DropDownItems.Add(new ToolStripMenuItem(preset.Name, null, (_, _) => Browser.SetViewMode(preset.Mode, preset.IconSize)) { Font = Font });

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
            if (Browser.CurrentLocation is { } current && text == current.DisplayName)
                Browser.Navigate(current);
            else if (!Browser.Navigate(text))
                MessageBox.Show(FindForm(), $"'{text}' 위치를 찾을 수 없습니다.", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
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

    private void UpdateStatus()
    {
        _itemsLabel.Text = $"항목 {Browser.ItemCount:N0}개";
        _selectionLabel.Text = Browser.SelectedCount > 0 ? $"|  {Browser.SelectedCount:N0}개 선택됨" : string.Empty;
    }
}

/// <summary>Icon-font glyphs for toolbar buttons (Segoe Fluent Icons on Windows 11, MDL2 Assets on 10).</summary>
internal static class Glyphs
{
    private const string FluentFont = "Segoe Fluent Icons";
    private const string FallbackFont = "Segoe MDL2 Assets";

    public static Font Font { get; } = CreateFont();

    public static ToolStripButton CreateButton(string glyph, string toolTip, EventHandler onClick) =>
        new(glyph, null, onClick) { Font = Font, ToolTipText = toolTip, DisplayStyle = ToolStripItemDisplayStyle.Text };

    private static Font CreateFont()
    {
        using var probe = new Font(FluentFont, 10f);
        return new Font(probe.Name == FluentFont ? FluentFont : FallbackFont, 10f);
    }
}
