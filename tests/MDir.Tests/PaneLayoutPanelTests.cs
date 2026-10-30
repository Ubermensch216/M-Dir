using MDir.Layout;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class PaneLayoutPanelTests
{
    [Fact]
    public void ChangingLayout_HidesUnusedPanesAndArrangesTheRest()
    {
        RunSta(() =>
        {
            using var panel = CreatePanel(out Control[] panes);
            Assert.All(panes, pane => Assert.True(pane.Visible));

            int layoutChanges = 0;
            panel.LayoutChanged += (_, _) => layoutChanges++;
            panel.CurrentLayout = PaneLayout.FromId(1)!;

            Assert.Equal(1, layoutChanges);
            Assert.True(panes[0].Visible);
            Assert.All(panes[1..], pane => Assert.False(pane.Visible));
            Assert.Equal(panel.ClientRectangle, panes[0].Bounds);

            panel.CurrentLayout = PaneLayout.FromId(8)!;
            Assert.Equal([true, true, true, false], panes.Select(pane => pane.Visible));
            Assert.Equal(panel.Arrangement!.Panes, panes[..3].Select(pane => pane.Bounds));
        });
    }

    [Fact]
    public void DraggingSplitter_ChangesRatioAndPaneBounds()
    {
        RunSta(() =>
        {
            using var panel = CreatePanel(out Control[] panes);
            panel.CurrentLayout = PaneLayout.FromId(2)!;
            int ratioChanges = 0;
            panel.RatiosChanged += (_, _) => ratioChanges++;

            Rectangle bar = panel.Arrangement!.Splitters[0].Bar;
            int y = bar.Top + bar.Height / 2;
            SendMessage(panel.Handle, WM_LBUTTONDOWN, MK_LBUTTON, MakePoint(bar.Left, y));
            SendMessage(panel.Handle, WM_MOUSEMOVE, MK_LBUTTON, MakePoint(200, y));
            SendMessage(panel.Handle, WM_LBUTTONUP, 0, MakePoint(200, y));

            Assert.True(ratioChanges > 0);
            Assert.InRange(panel.Ratios[0], 2400, 2600);
            Assert.Equal(200, panes[0].Width);

            // Moving after the button is released must not drag any more.
            SendMessage(panel.Handle, WM_MOUSEMOVE, 0, MakePoint(500, y));
            Assert.Equal(200, panes[0].Width);
        });
    }

    [Fact]
    public void ChangingLayout_ResetsRatiosToItsDefaults()
    {
        RunSta(() =>
        {
            using var panel = CreatePanel(out _);
            panel.SetRatio(0, 2000);
            panel.CurrentLayout = PaneLayout.FromId(13)!;

            Assert.Equal(PaneLayout.FromId(13)!.DefaultRatios, panel.Ratios);
        });
    }

    [Fact]
    public void HoveringSplitter_DoesNotLeakResizeCursorToChildPanes()
    {
        RunSta(() =>
        {
            using var panel = CreatePanel(out Control[] panes);
            var tabStrip = new Panel();
            panes[2].Controls.Add(tabStrip);

            SplitterPlacement horizontal = panel.Arrangement!.Splitters.First(s => s.Direction == SplitDirection.Rows);
            SendMessage(panel.Handle, WM_MOUSEMOVE, 0, MakePoint(horizontal.Bar.Left + 10, horizontal.Bar.Top + 1));

            Assert.Equal(Cursors.HSplit, panel.Cursor);
            Assert.Equal(Cursors.Default, panes[2].Cursor);
            Assert.Equal(Cursors.Default, tabStrip.Cursor);

            const int WM_MOUSELEAVE = 0x02A3;
            SendMessage(panel.Handle, WM_MOUSELEAVE, 0, 0);
            Assert.Equal(Cursors.Default, panel.Cursor);
        });
    }

    private static PaneLayoutPanel CreatePanel(out Control[] panes)
    {
        panes = [.. Enumerable.Range(0, PaneLayout.MaxPanes).Select(i => new Label { Text = $"Pane {i + 1}" })];
        var panel = new PaneLayoutPanel(panes) { Size = new Size(806, 606) };
        _ = panel.Handle;
        panel.PerformLayout();
        return panel;
    }
}
