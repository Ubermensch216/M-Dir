using MDir.Layout;

namespace MDir.Tests;

public class PaneLayoutTests
{
    private static readonly Rectangle Bounds = new(0, 0, 1006, 806);
    private const int Splitter = 6;

    public static TheoryData<int> LayoutIds => [.. PaneLayout.All.Select(layout => layout.Id)];

    [Fact]
    public void Catalog_HasThirteenUniqueLayouts()
    {
        Assert.Equal(13, PaneLayout.All.Count);
        Assert.Equal(PaneLayout.All.Count, PaneLayout.All.Select(layout => layout.Id).Distinct().Count());
        Assert.Equal(PaneLayout.All.Count, PaneLayout.All.Select(layout => layout.Name).Distinct().Count());
        Assert.Equal(4, PaneLayout.Default.PaneCount);
    }

    [Theory]
    [MemberData(nameof(LayoutIds))]
    public void Arrange_FillsBoundsWithoutOverlap(int id)
    {
        PaneLayout layout = PaneLayout.FromId(id)!;
        Assert.InRange(layout.PaneCount, 1, PaneLayout.MaxPanes);
        Assert.Equal(layout.PaneCount - 1, layout.SplitterCount);

        LayoutArrangement arrangement = PaneArranger.Arrange(layout, layout.DefaultRatios, Bounds, Splitter);
        Rectangle[] all = [.. arrangement.Panes, .. arrangement.Splitters.Select(s => s.Bar)];

        Assert.All(all, r => Assert.True(Bounds.Contains(r), $"{r} is outside {Bounds}"));
        Assert.All(arrangement.Panes, r => Assert.True(r.Width > 100 && r.Height > 100, $"pane {r} is too small"));
        for (int i = 0; i < all.Length; i++)
        {
            for (int j = i + 1; j < all.Length; j++)
                Assert.False(all[i].IntersectsWith(all[j]), $"{all[i]} overlaps {all[j]}");
        }

        Assert.Equal(Bounds.Width * Bounds.Height, all.Sum(r => r.Width * r.Height));
        Assert.Equal(Enumerable.Range(0, layout.SplitterCount), arrangement.Splitters.Select(s => s.Index).Order());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    public void DefaultRatios_SplitColumnsEvenly(int id)
    {
        PaneLayout layout = PaneLayout.FromId(id)!;
        int splitters = layout.SplitterCount;
        var bounds = new Rectangle(0, 0, 1200 + splitters * Splitter, 300);

        LayoutArrangement arrangement = PaneArranger.Arrange(layout, layout.DefaultRatios, bounds, Splitter);

        int expected = 1200 / layout.PaneCount;
        Assert.All(arrangement.Panes, r => Assert.InRange(r.Width, expected - 3, expected + 3));
    }

    [Fact]
    public void Grid_PanesAreNumberedInReadingOrder()
    {
        PaneLayout grid = PaneLayout.Default;
        LayoutArrangement arrangement = PaneArranger.Arrange(grid, grid.DefaultRatios, Bounds, Splitter);
        IReadOnlyList<Rectangle> p = arrangement.Panes;

        Assert.True(p[0].Left < p[1].Left && p[0].Top == p[1].Top, "pane 2 is right of pane 1");
        Assert.True(p[2].Top > p[0].Top && p[2].Left == p[0].Left, "pane 3 is below pane 1");
        Assert.True(p[3].Top > p[1].Top && p[3].Left == p[1].Left, "pane 4 is below pane 2");

        // Like Q-Dir's SP0 (vertical bar) then SP1/SP2 (horizontal bars of each column).
        Assert.Equal(SplitDirection.Columns, arrangement.Splitters[0].Direction);
        Assert.Equal(SplitDirection.Rows, arrangement.Splitters[1].Direction);
        Assert.Equal(SplitDirection.Rows, arrangement.Splitters[2].Direction);
    }

    [Fact]
    public void Arrange_UsesRatios()
    {
        PaneLayout twoColumns = PaneLayout.FromId(2)!;
        var bounds = new Rectangle(10, 20, 1000 + Splitter, 500);

        LayoutArrangement arrangement = PaneArranger.Arrange(twoColumns, [2500], bounds, Splitter);

        Assert.Equal(new Rectangle(10, 20, 250, 500), arrangement.Panes[0]);
        Assert.Equal(new Rectangle(260, 20, Splitter, 500), arrangement.Splitters[0].Bar);
        Assert.Equal(new Rectangle(266, 20, 750, 500), arrangement.Panes[1]);
    }

    [Fact]
    public void Arrange_RejectsWrongRatioCount() =>
        Assert.Throws<ArgumentException>(() => PaneArranger.Arrange(PaneLayout.Default, [5000], Bounds, Splitter));

    [Fact]
    public void Arrange_ToleratesTinyBounds()
    {
        LayoutArrangement arrangement = PaneArranger.Arrange(PaneLayout.Default, PaneLayout.Default.DefaultRatios, new Rectangle(0, 0, 3, 2), Splitter);
        Assert.All(arrangement.Panes, r => Assert.True(r.Width >= 0 && r.Height >= 0));
    }

    [Fact]
    public void RatioForBarPosition_RoundTripsAndClamps()
    {
        PaneLayout twoRows = PaneLayout.FromId(3)!;
        var bounds = new Rectangle(0, 100, 400, 1000 + Splitter);
        SplitterPlacement splitter = PaneArranger.Arrange(twoRows, [5000], bounds, Splitter).Splitters[0];

        Assert.Equal(3000, PaneArranger.RatioForBarPosition(splitter, 100 + 300, minPaneSize: 50));
        Assert.Equal(500, PaneArranger.RatioForBarPosition(splitter, -500, minPaneSize: 50));
        Assert.Equal(9500, PaneArranger.RatioForBarPosition(splitter, 5000, minPaneSize: 50));
    }

    [Fact]
    public void HitTest_FindsSplitterBars()
    {
        LayoutArrangement arrangement = PaneArranger.Arrange(PaneLayout.Default, PaneLayout.Default.DefaultRatios, Bounds, Splitter);
        SplitterPlacement vertical = arrangement.Splitters[0];

        Assert.Equal(0, arrangement.HitTest(new Point(vertical.Bar.Left + 1, 10))?.Index);
        Assert.Null(arrangement.HitTest(new Point(5, 5)));
    }
}
