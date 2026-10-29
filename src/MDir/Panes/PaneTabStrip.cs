using System.ComponentModel;

namespace MDir.Panes;

/// <summary>
/// Owner-drawn tab bar of a pane. Supports a close button per tab, middle-click and double-click closing,
/// drag reordering, a "+" button, multi-line wrapping, and an overflow list when single-line tabs don't fit.
/// The strip only reports requests (new/close/context menu); the pane decides what they do.
/// </summary>
internal sealed class PaneTabStrip : Control
{
    private const int LogicalMinTabWidth = 72;
    private const int LogicalMaxTabWidth = 220;
    private const int LogicalPadding = 6;
    private const int LogicalIconSize = 16;
    private const int LogicalCloseSize = 18;
    private const int LogicalButtonWidth = 28;
    private const int LogicalRowPadding = 12;

    private readonly List<PaneTab> _tabs = [];
    private readonly ToolTip _toolTip = new();
    private readonly Font _smallGlyphs = new(Glyphs.Font.FontFamily, 7f);
    private Rectangle[] _tabBounds = [];
    private Rectangle _newButton;
    private Rectangle _overflowButton;
    private int _firstVisible;
    private int _selectedIndex = -1;
    private bool _multiline;
    private bool _active;
    private int _hotIndex = -1;
    private bool _hotClose;
    private HotButton _hotButton;
    private int _pressedIndex = -1;
    private bool _pressedClose;
    private Point _pressPoint;
    private bool _dragging;

    private enum HotButton
    {
        None,
        New,
        Overflow,
    }

    public PaneTabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.Selectable, false);
        Dock = DockStyle.Top;
        Arrange();
    }

    public event EventHandler? SelectedIndexChanged;

    public event EventHandler? NewTabRequested;

    public event EventHandler<TabEventArgs>? TabCloseRequested;

    /// <summary>Right-click on a tab (<see cref="TabEventArgs.Tab"/> set) or on empty space (null).</summary>
    public event EventHandler<TabEventArgs>? TabContextMenuRequested;

    public IReadOnlyList<PaneTab> Tabs => _tabs;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            value = _tabs.Count == 0 ? -1 : Math.Clamp(value, 0, _tabs.Count - 1);
            if (value == _selectedIndex)
                return;

            _selectedIndex = value;
            Arrange();
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PaneTab? SelectedTab => _selectedIndex >= 0 ? _tabs[_selectedIndex] : null;

    /// <summary>Wrap tabs onto several rows instead of shrinking them.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Multiline
    {
        get => _multiline;
        set
        {
            if (_multiline == value)
                return;

            _multiline = value;
            Arrange();
            Invalidate();
        }
    }

    /// <summary>Draws the selected tab's accent in the highlight colour (the pane is active).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;

            _active = value;
            Invalidate();
        }
    }

    internal IReadOnlyList<Rectangle> TabBounds => _tabBounds;

    internal Rectangle NewButtonBounds => _newButton;

    internal Rectangle OverflowButtonBounds => _overflowButton;

    public void Insert(int index, PaneTab tab)
    {
        _tabs.Insert(index, tab);
        if (index <= _selectedIndex)
            _selectedIndex++;

        RefreshTabs();
    }

    /// <summary>Removes a tab; when it was selected, the tab that takes its place (or the previous one) is selected.</summary>
    public void Remove(PaneTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
            return;

        _tabs.RemoveAt(index);
        _hotIndex = _pressedIndex = -1;
        if (index < _selectedIndex)
        {
            _selectedIndex--;
            RefreshTabs();
        }
        else if (index == _selectedIndex)
        {
            _selectedIndex = -1;
            RefreshTabs();
            SelectedIndex = Math.Min(index, _tabs.Count - 1);
        }
        else
        {
            RefreshTabs();
        }
    }

    public void MoveTab(int from, int to)
    {
        if (from == to)
            return;

        PaneTab? selected = SelectedTab;
        PaneTab tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);
        _selectedIndex = selected is null ? -1 : _tabs.IndexOf(selected);
        RefreshTabs();
    }

    /// <summary>Re-measures after tab titles change.</summary>
    public void RefreshTabs()
    {
        Arrange();
        Invalidate();
    }

    /// <summary>Index of the visible tab at <paramref name="point"/>, or -1.</summary>
    public int IndexAt(Point point)
    {
        for (int i = 0; i < _tabBounds.Length; i++)
        {
            if (_tabBounds[i].Contains(point))
                return i;
        }

        return -1;
    }

    internal Rectangle GetCloseButtonBounds(int index)
    {
        Rectangle tab = _tabBounds[index];
        int size = LogicalToDeviceUnits(LogicalCloseSize);
        int padding = LogicalToDeviceUnits(LogicalPadding);
        bool fits = tab.Width >= size + LogicalToDeviceUnits(LogicalIconSize) + padding * 3;
        if (!fits || (index != _selectedIndex && index != _hotIndex))
            return Rectangle.Empty;

        return new Rectangle(tab.Right - padding / 2 - size, tab.Top + (tab.Height - size) / 2, size, size);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _smallGlyphs.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Arrange();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Arrange();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Arrange();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        int index = IndexAt(e.Location);
        if (index < 0)
            return;

        _pressedIndex = index;
        _pressedClose = GetCloseButtonBounds(index).Contains(e.Location);
        _pressPoint = e.Location;
        _dragging = false;
        if (!_pressedClose)
            SelectedIndex = index;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // _pressedIndex is only set between a left button down and up (or capture loss).
        if (_pressedIndex >= 0 && !_pressedClose)
        {
            Size drag = SystemInformation.DragSize;
            if (!_dragging && (Math.Abs(e.X - _pressPoint.X) > drag.Width || Math.Abs(e.Y - _pressPoint.Y) > drag.Height))
                _dragging = true;

            if (_dragging)
            {
                int target = IndexAt(e.Location);
                if (target >= 0 && target != _pressedIndex)
                {
                    MoveTab(_pressedIndex, target);
                    _pressedIndex = target;
                }

                return;
            }
        }

        UpdateHot(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = IndexAt(e.Location);
        switch (e.Button)
        {
            case MouseButtons.Left:
                if (_pressedClose && index == _pressedIndex && GetCloseButtonBounds(index).Contains(e.Location))
                    TabCloseRequested?.Invoke(this, new TabEventArgs(_tabs[index], e.Location));
                else if (_pressedIndex < 0 && _newButton.Contains(e.Location))
                    NewTabRequested?.Invoke(this, EventArgs.Empty);
                else if (_pressedIndex < 0 && _overflowButton.Contains(e.Location))
                    ShowOverflowMenu();
                break;

            case MouseButtons.Middle when index >= 0:
                TabCloseRequested?.Invoke(this, new TabEventArgs(_tabs[index], e.Location));
                break;

            case MouseButtons.Right:
                TabContextMenuRequested?.Invoke(this, new TabEventArgs(index >= 0 ? _tabs[index] : null, e.Location));
                break;
        }

        _pressedIndex = -1;
        _pressedClose = false;
        _dragging = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        _pressedIndex = -1;
        _pressedClose = false;
        _dragging = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left)
            HandleDoubleClick(e.Location);
    }

    /// <summary>Double-clicking a tab closes it (Q-Dir's default); double-clicking empty space opens a new tab.</summary>
    internal void HandleDoubleClick(Point point)
    {
        if (_newButton.Contains(point) || _overflowButton.Contains(point))
            return;

        int index = IndexAt(point);
        if (index < 0)
            NewTabRequested?.Invoke(this, EventArgs.Empty);
        else if (!GetCloseButtonBounds(index).Contains(point))
            TabCloseRequested?.Invoke(this, new TabEventArgs(_tabs[index], point));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateHot(new Point(-1, -1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(SystemColors.Control);
        int padding = LogicalToDeviceUnits(LogicalPadding);
        int iconSize = LogicalToDeviceUnits(LogicalIconSize);

        for (int i = 0; i < _tabBounds.Length; i++)
        {
            Rectangle bounds = _tabBounds[i];
            if (bounds.IsEmpty)
                continue;

            PaneTab tab = _tabs[i];
            bool selected = i == _selectedIndex;
            var body = Rectangle.FromLTRB(bounds.Left + 1, bounds.Top + 2, bounds.Right - 1, bounds.Bottom);
            if (selected)
            {
                g.FillRectangle(SystemBrushes.Window, body);
                g.FillRectangle(_active ? SystemBrushes.Highlight : SystemBrushes.ControlDark,
                    body.Left, body.Top, body.Width, LogicalToDeviceUnits(2));
            }
            else if (i == _hotIndex)
            {
                g.FillRectangle(SystemBrushes.ControlLight, body);
            }
            else
            {
                g.FillRectangle(SystemBrushes.ControlDark, body.Right, body.Top + body.Height / 4, 1, body.Height / 2);
            }

            int x = body.Left + padding;
            if (tab.Icon is { } icon)
                g.DrawImage(icon, new Rectangle(x, body.Top + (body.Height - iconSize) / 2, iconSize, iconSize));
            x += iconSize + padding / 2;

            Rectangle close = GetCloseButtonBounds(i);
            int textRight = close.IsEmpty ? body.Right - padding : close.Left - 2;
            var textBounds = Rectangle.FromLTRB(x, body.Top, Math.Max(x, textRight), body.Bottom);
            TextRenderer.DrawText(g, tab.Title, Font, textBounds, selected ? SystemColors.WindowText : SystemColors.ControlText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (!close.IsEmpty)
            {
                if (i == _hotIndex && _hotClose)
                    g.FillRectangle(SystemBrushes.ControlDark, close);
                TextRenderer.DrawText(g, Glyphs.Close, _smallGlyphs, close, SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        DrawButton(g, _newButton, Glyphs.Add, _hotButton == HotButton.New);
        DrawButton(g, _overflowButton, Glyphs.ChevronDown, _hotButton == HotButton.Overflow);
    }

    private void DrawButton(Graphics g, Rectangle bounds, string glyph, bool hot)
    {
        if (bounds.IsEmpty)
            return;

        if (hot)
            g.FillRectangle(SystemBrushes.ControlLight, bounds);
        TextRenderer.DrawText(g, glyph, _smallGlyphs, bounds, SystemColors.ControlText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>Computes tab, "+" and overflow button bounds for the current width, and the strip's height.</summary>
    private void Arrange()
    {
        int rowHeight = Font.Height + LogicalToDeviceUnits(LogicalRowPadding);
        int buttonWidth = LogicalToDeviceUnits(LogicalButtonWidth);
        int minWidth = LogicalToDeviceUnits(LogicalMinTabWidth);
        int maxWidth = LogicalToDeviceUnits(LogicalMaxTabWidth);
        int chrome = LogicalToDeviceUnits(LogicalPadding) * 3 + LogicalToDeviceUnits(LogicalIconSize) + LogicalToDeviceUnits(LogicalCloseSize);
        int width = Math.Max(ClientSize.Width, buttonWidth * 2 + minWidth);

        int[] natural = [.. _tabs.Select(tab => Math.Clamp(
            TextRenderer.MeasureText(tab.Title, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + chrome,
            minWidth, maxWidth))];
        _tabBounds = new Rectangle[_tabs.Count];
        _overflowButton = Rectangle.Empty;
        int rows = 1;

        if (_multiline)
        {
            int x = 0;
            for (int i = 0; i < _tabs.Count; i++)
            {
                int w = Math.Min(natural[i], width);
                if (x > 0 && x + w > width)
                {
                    rows++;
                    x = 0;
                }

                _tabBounds[i] = new Rectangle(x, (rows - 1) * rowHeight, w, rowHeight);
                x += w;
            }

            if (x + buttonWidth > width)
            {
                rows++;
                x = 0;
            }

            _newButton = new Rectangle(x, (rows - 1) * rowHeight, buttonWidth, rowHeight);
        }
        else
        {
            int available = width - buttonWidth;
            int total = natural.Sum();
            int count = _tabs.Count;
            int visible = count;
            if (total > available && count > 0 && available / count < minWidth)
            {
                available -= buttonWidth;
                visible = Math.Max(1, available / minWidth);
            }

            // Keep the selected tab in the visible window.
            _firstVisible = Math.Clamp(_firstVisible, 0, Math.Max(0, count - visible));
            if (_selectedIndex >= 0 && _selectedIndex < _firstVisible)
                _firstVisible = _selectedIndex;
            else if (_selectedIndex >= _firstVisible + visible)
                _firstVisible = _selectedIndex - visible + 1;

            int shrunk = total > available && visible > 0 ? Math.Max(minWidth, available / visible) : int.MaxValue;
            int x = 0;
            for (int i = _firstVisible; i < Math.Min(count, _firstVisible + visible); i++)
            {
                int w = Math.Min(natural[i], shrunk);
                _tabBounds[i] = new Rectangle(x, 0, w, rowHeight);
                x += w;
            }

            _newButton = new Rectangle(x, 0, buttonWidth, rowHeight);
            if (visible < count)
                _overflowButton = new Rectangle(width - buttonWidth, 0, buttonWidth, rowHeight);
        }

        int height = rows * rowHeight;
        if (Height != height)
            Height = height;
    }

    private void UpdateHot(Point point)
    {
        int index = IndexAt(point);
        bool close = index >= 0 && GetCloseButtonBounds(index).Contains(point);
        HotButton button = _newButton.Contains(point) ? HotButton.New
            : _overflowButton.Contains(point) ? HotButton.Overflow
            : HotButton.None;
        if (index == _hotIndex && close == _hotClose && button == _hotButton)
            return;

        if (index != _hotIndex || button != _hotButton)
            _toolTip.SetToolTip(this, index >= 0 ? _tabs[index].ToolTip : button == HotButton.New ? "새 탭 (Ctrl+T)" : null);

        _hotIndex = index;
        _hotClose = close;
        _hotButton = button;
        Invalidate();
    }

    private void ShowOverflowMenu()
    {
        var menu = new ContextMenuStrip();
        for (int i = 0; i < _tabs.Count; i++)
        {
            int index = i;
            menu.Items.Add(new ToolStripMenuItem(_tabs[i].Title, _tabs[i].Icon, (_, _) => SelectedIndex = index)
            {
                Checked = i == _selectedIndex,
            });
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, new Point(_overflowButton.Left, _overflowButton.Bottom));
    }
}

internal sealed class TabEventArgs(PaneTab? tab, Point location) : EventArgs
{
    public PaneTab? Tab { get; } = tab;

    /// <summary>Mouse position in strip client coordinates.</summary>
    public Point Location { get; } = location;
}
