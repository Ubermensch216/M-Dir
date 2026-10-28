namespace MDir.Layout;

/// <summary>A splitter bar placed by <see cref="PaneArranger.Arrange"/>.</summary>
/// <param name="Index">Pre-order splitter number (index into the ratio list).</param>
/// <param name="Bar">The bar itself.</param>
/// <param name="Area">The whole area the split divides; drag positions are relative to it.</param>
public readonly record struct SplitterPlacement(int Index, SplitDirection Direction, Rectangle Bar, Rectangle Area);

/// <param name="Panes">Pane bounds indexed by pane number.</param>
public sealed record LayoutArrangement(IReadOnlyList<Rectangle> Panes, IReadOnlyList<SplitterPlacement> Splitters)
{
    public SplitterPlacement? HitTest(Point point)
    {
        foreach (SplitterPlacement splitter in Splitters)
        {
            if (splitter.Bar.Contains(point))
                return splitter;
        }

        return null;
    }
}

/// <summary>Turns a layout's split tree and ratios into pane and splitter rectangles.</summary>
public static class PaneArranger
{
    public static LayoutArrangement Arrange(PaneLayout layout, IReadOnlyList<int> ratios, Rectangle bounds, int splitterSize)
    {
        if (ratios.Count != layout.SplitterCount)
            throw new ArgumentException($"Layout {layout.Id} needs {layout.SplitterCount} ratios.", nameof(ratios));

        var panes = new Rectangle[layout.PaneCount];
        var splitters = new List<SplitterPlacement>(layout.SplitterCount);
        int nextSplitter = 0;
        Place(layout.Root, bounds);
        return new LayoutArrangement(panes, splitters);

        void Place(LayoutNode node, Rectangle area)
        {
            switch (node)
            {
                case LayoutNode.Pane pane:
                    panes[pane.Index] = area;
                    break;

                case LayoutNode.Split split:
                    int index = nextSplitter++;
                    bool columns = split.Direction == SplitDirection.Columns;
                    int extent = columns ? area.Width : area.Height;
                    int bar = Math.Min(splitterSize, extent);
                    int available = extent - bar;
                    int first = (int)Math.Round(available * (double)Math.Clamp(ratios[index], 0, PaneLayout.RatioScale) / PaneLayout.RatioScale);

                    Rectangle firstArea, barArea, secondArea;
                    if (columns)
                    {
                        firstArea = new Rectangle(area.Left, area.Top, first, area.Height);
                        barArea = new Rectangle(area.Left + first, area.Top, bar, area.Height);
                        secondArea = Rectangle.FromLTRB(barArea.Right, area.Top, area.Right, area.Bottom);
                    }
                    else
                    {
                        firstArea = new Rectangle(area.Left, area.Top, area.Width, first);
                        barArea = new Rectangle(area.Left, area.Top + first, area.Width, bar);
                        secondArea = Rectangle.FromLTRB(area.Left, barArea.Bottom, area.Right, area.Bottom);
                    }

                    splitters.Add(new SplitterPlacement(index, split.Direction, barArea, area));
                    Place(split.First, firstArea);
                    Place(split.Second, secondArea);
                    break;
            }
        }
    }

    /// <summary>
    /// Ratio that puts the splitter's bar at <paramref name="barStart"/> (client coordinates), keeping at least
    /// <paramref name="minPaneSize"/> on both sides when there is room for it.
    /// </summary>
    public static int RatioForBarPosition(SplitterPlacement splitter, int barStart, int minPaneSize)
    {
        bool columns = splitter.Direction == SplitDirection.Columns;
        int barSize = columns ? splitter.Bar.Width : splitter.Bar.Height;
        int available = (columns ? splitter.Area.Width : splitter.Area.Height) - barSize;
        if (available <= 0)
            return PaneLayout.RatioScale / 2;

        int offset = barStart - (columns ? splitter.Area.Left : splitter.Area.Top);
        int min = Math.Min(minPaneSize, available / 2);
        offset = Math.Clamp(offset, min, available - min);
        return (int)Math.Round(offset * (double)PaneLayout.RatioScale / available);
    }
}
