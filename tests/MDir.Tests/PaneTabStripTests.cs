using MDir.Panes;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Tab strip behaviour with tabs that have no location (no shell calls needed).</summary>
public class PaneTabStripTests
{
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;

    [Fact]
    public void ClickingATab_SelectsIt()
    {
        RunSta(() =>
        {
            using var strip = CreateStrip(3, out PaneTab[] tabs);
            int changes = 0;
            strip.SelectedIndexChanged += (_, _) => changes++;

            Click(strip, Center(strip.TabBounds[2]));

            Assert.Equal(2, strip.SelectedIndex);
            Assert.Same(tabs[2], strip.SelectedTab);
            Assert.Equal(1, changes);
        });
    }

    [Fact]
    public void MiddleClickAndDoubleClick_RequestClose_EmptyDoubleClickRequestsNewTab()
    {
        RunSta(() =>
        {
            using var strip = CreateStrip(3, out PaneTab[] tabs);
            var closed = new List<PaneTab?>();
            int newTabs = 0;
            strip.TabCloseRequested += (_, e) => closed.Add(e.Tab);
            strip.NewTabRequested += (_, _) => newTabs++;

            IntPtr middle = MakePoint(Center(strip.TabBounds[1]));
            SendMessage(strip.Handle, WM_MBUTTONDOWN, 0, middle);
            SendMessage(strip.Handle, WM_MBUTTONUP, 0, middle);
            Assert.Equal([tabs[1]], closed);

            // WinForms only raises double-clicks for windows under the real cursor, so call the handler directly.
            strip.HandleDoubleClick(new Point(strip.TabBounds[0].Left + 4, strip.TabBounds[0].Top + 4));
            Assert.Equal([tabs[1], tabs[0]], closed);

            strip.HandleDoubleClick(new Point(strip.NewButtonBounds.Right + 40, strip.NewButtonBounds.Top + 4));
            Assert.Equal(1, newTabs);

            Click(strip, Center(strip.NewButtonBounds));
            Assert.Equal(2, newTabs);
        });
    }

    [Fact]
    public void CloseButton_OnSelectedTabRequestsClose()
    {
        RunSta(() =>
        {
            using var strip = CreateStrip(2, out PaneTab[] tabs);
            Rectangle close = strip.GetCloseButtonBounds(0);
            Assert.False(close.IsEmpty);
            Assert.True(strip.GetCloseButtonBounds(1).IsEmpty, "unselected, unhovered tabs hide the close button");

            PaneTab? closed = null;
            strip.TabCloseRequested += (_, e) => closed = e.Tab;
            Click(strip, Center(close));

            Assert.Same(tabs[0], closed);
            Assert.Equal(0, strip.SelectedIndex);
        });
    }

    [Fact]
    public void DraggingATab_ReordersAndKeepsSelection()
    {
        RunSta(() =>
        {
            using var strip = CreateStrip(3, out PaneTab[] tabs);
            Point start = Center(strip.TabBounds[0]);
            Point end = Center(strip.TabBounds[2]);

            SendMessage(strip.Handle, WM_LBUTTONDOWN, MK_LBUTTON, MakePoint(start));
            SendMessage(strip.Handle, WM_MOUSEMOVE, MK_LBUTTON, MakePoint(new Point(start.X + 20, start.Y)));
            SendMessage(strip.Handle, WM_MOUSEMOVE, MK_LBUTTON, MakePoint(end));
            SendMessage(strip.Handle, WM_LBUTTONUP, 0, MakePoint(end));

            Assert.Equal([tabs[1], tabs[2], tabs[0]], strip.Tabs);
            Assert.Same(tabs[0], strip.SelectedTab);
            Assert.Equal(2, strip.SelectedIndex);
        });
    }

    [Fact]
    public void RemovingSelectedTab_SelectsTheNextOne()
    {
        RunSta(() =>
        {
            using var strip = CreateStrip(3, out PaneTab[] tabs);
            strip.SelectedIndex = 1;
            strip.Remove(tabs[1]);
            Assert.Same(tabs[2], strip.SelectedTab);

            strip.Remove(tabs[2]);
            Assert.Same(tabs[0], strip.SelectedTab);

            strip.Insert(0, tabs[1]);
            Assert.Same(tabs[0], strip.SelectedTab);
            Assert.Equal(1, strip.SelectedIndex);
        });
    }

    [Fact]
    public void ManyTabs_SingleLineOverflowsButKeepsSelectedVisible_MultilineWraps()
    {
        RunSta(() =>
        {
            using var strip = CreateStrip(20, out _);
            int singleHeight = strip.Height;
            strip.SelectedIndex = 19;

            Assert.False(strip.OverflowButtonBounds.IsEmpty);
            Assert.False(strip.TabBounds[19].IsEmpty);
            Assert.True(strip.TabBounds[0].IsEmpty);
            Assert.True(strip.TabBounds.Where(r => !r.IsEmpty).All(r => r.Right <= strip.OverflowButtonBounds.Left));

            strip.Multiline = true;
            Assert.True(strip.Height >= singleHeight * 3);
            Assert.True(strip.OverflowButtonBounds.IsEmpty);
            Assert.All(strip.TabBounds, r => Assert.False(r.IsEmpty));
            Assert.All(strip.TabBounds, r => Assert.True(r.Right <= strip.Width));

            strip.Multiline = false;
            Assert.Equal(singleHeight, strip.Height);
        });
    }

    private static PaneTabStrip CreateStrip(int count, out PaneTab[] tabs)
    {
        tabs = [.. Enumerable.Range(0, count).Select(_ => new PaneTab())];
        var strip = new PaneTabStrip { Width = 600 };
        for (int i = 0; i < tabs.Length; i++)
            strip.Insert(i, tabs[i]);
        strip.SelectedIndex = 0;
        _ = strip.Handle;
        return strip;
    }

    private static void Click(PaneTabStrip strip, Point point)
    {
        SendMessage(strip.Handle, WM_MOUSEMOVE, 0, MakePoint(point));
        SendMessage(strip.Handle, WM_LBUTTONDOWN, MK_LBUTTON, MakePoint(point));
        SendMessage(strip.Handle, WM_LBUTTONUP, 0, MakePoint(point));
    }

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static IntPtr MakePoint(Point point) => UiTest.MakePoint(point.X, point.Y);
}
