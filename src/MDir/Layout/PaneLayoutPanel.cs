using System.ComponentModel;

namespace MDir.Layout;

/// <summary>
/// Arranges up to <see cref="PaneLayout.MaxPanes"/> pane controls by a <see cref="PaneLayout"/> and lets the user
/// drag the splitter bars between them. Panes outside the current layout are hidden, not destroyed, so they keep
/// their folder and history when the layout grows again.
/// </summary>
public sealed class PaneLayoutPanel : Control
{
    private const int LogicalSplitterSize = 6;
    private const int LogicalMinPaneSize = 48;

    private readonly IReadOnlyList<Control> _panes;
    private PaneLayout _layout = PaneLayout.Default;
    private int[] _ratios = [.. PaneLayout.Default.DefaultRatios];
    private LayoutArrangement? _arrangement;
    private SplitterPlacement? _drag;
    private int _dragGrabOffset;

    public PaneLayoutPanel(IReadOnlyList<Control> panes)
    {
        if (panes.Count != PaneLayout.MaxPanes)
            throw new ArgumentException($"Exactly {PaneLayout.MaxPanes} panes are required.", nameof(panes));

        _panes = panes;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        foreach (Control pane in panes)
            Controls.Add(pane);

        ApplyVisibility();
    }

    /// <summary>Raised after <see cref="CurrentLayout"/> changes.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Raised when a splitter ratio changes (drag, double-click reset or <see cref="SetRatio"/>).</summary>
    public event EventHandler? RatiosChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PaneLayout CurrentLayout
    {
        get => _layout;
        set
        {
            if (value == _layout)
                return;

            _layout = value;
            _ratios = [.. value.DefaultRatios];
            _drag = null;
            ApplyVisibility();
            PerformLayout();
            Invalidate();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Current splitter ratios in pre-order, in 1/<see cref="PaneLayout.RatioScale"/> units.</summary>
    public IReadOnlyList<int> Ratios => _ratios;

    internal LayoutArrangement? Arrangement => _arrangement;

    public bool IsPaneVisible(int index) => index < _layout.PaneCount;

    public void SetRatio(int splitter, int ratio)
    {
        ratio = Math.Clamp(ratio, 0, PaneLayout.RatioScale);
        if (_ratios[splitter] == ratio)
            return;

        _ratios[splitter] = ratio;
        PerformLayout();
        Invalidate();
        RatiosChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        _arrangement = PaneArranger.Arrange(_layout, _ratios, ClientRectangle, LogicalToDeviceUnits(LogicalSplitterSize));
        for (int i = 0; i < _arrangement.Panes.Count; i++)
            _panes[i].Bounds = _arrangement.Panes[i];
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _arrangement?.HitTest(e.Location) is not { } splitter)
            return;

        _drag = splitter;
        _dragGrabOffset = splitter.Direction == SplitDirection.Columns ? e.X - splitter.Bar.Left : e.Y - splitter.Bar.Top;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is { } drag)
        {
            int barStart = (drag.Direction == SplitDirection.Columns ? e.X : e.Y) - _dragGrabOffset;
            SetRatio(drag.Index, PaneArranger.RatioForBarPosition(drag, barStart, LogicalToDeviceUnits(LogicalMinPaneSize)));
            return;
        }

        Cursor = _arrangement?.HitTest(e.Location) switch
        {
            { Direction: SplitDirection.Columns } => Cursors.VSplit,
            { Direction: SplitDirection.Rows } => Cursors.HSplit,
            _ => Cursors.Default,
        };
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = null;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        _drag = null;
    }

    /// <summary>Double-clicking a splitter restores its default position.</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && _arrangement?.HitTest(e.Location) is { } splitter)
            SetRatio(splitter.Index, _layout.DefaultRatios[splitter.Index]);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_arrangement is null)
            return;

        foreach (SplitterPlacement splitter in _arrangement.Splitters)
        {
            // A thin centred line makes the bar discoverable without looking heavy.
            Rectangle bar = splitter.Bar;
            Rectangle line = splitter.Direction == SplitDirection.Columns
                ? new Rectangle(bar.Left + bar.Width / 2, bar.Top, 1, bar.Height)
                : new Rectangle(bar.Left, bar.Top + bar.Height / 2, bar.Width, 1);
            e.Graphics.FillRectangle(SystemBrushes.ControlDark, line);
        }
    }

    private void ApplyVisibility()
    {
        for (int i = 0; i < _panes.Count; i++)
            _panes[i].Visible = IsPaneVisible(i);
    }
}
