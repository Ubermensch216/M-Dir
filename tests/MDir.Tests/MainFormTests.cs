using MDir.Layout;
using MDir.Panes;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Integration tests for the multi-pane main window. They show a window briefly on the desktop.</summary>
public class MainFormTests(ITestOutputHelper output)
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void HiddenPanes_BrowseWhenTheLayoutFirstShowsThem()
    {
        RunMainForm(PaneLayout.FromId(2)!, async form =>
        {
            await WaitUntil(() => IsAt(form.Panes[0], WindowsDir), "pane 1 opened the start folder");
            await WaitUntil(() => form.Panes[1].Browser.CurrentLocation is not null, "pane 2 browsed");
            Assert.False(form.Panes[3].Visible);
            Assert.Null(form.Panes[3].Browser.CurrentLocation);

            form.LayoutPanel.CurrentLayout = PaneLayout.Default;
            await WaitUntil(() => form.Panes[3].Browser.CurrentLocation is not null, "pane 4 browsed once shown");
            await WaitUntil(() => form.Panes.All(pane => pane.Browser.ItemCount > 0), "all panes enumerated");

            output.WriteLine($"Screenshot: {SaveScreenshot(form, "main-form-grid.png")}");
        });
    }

    [Fact]
    public void F6AndCtrlAltNumbers_MoveFocusBetweenPanes()
    {
        RunMainForm(PaneLayout.Default, async form =>
        {
            await WaitUntil(() => form.Panes.All(pane => pane.Browser.ItemCount > 0), "all panes enumerated");
            await WaitUntil(() => form.Panes[0].ContainsFocus, "pane 1 has focus at start");
            Assert.True(form.ActivePane.IsActive);

            // F6 goes through the shell view's key routing to the menu shortcut.
            PostMessage(GetFocus(), NativeMethods.WM_KEYDOWN, (IntPtr)Keys.F6, IntPtr.Zero);
            await WaitUntil(() => form.ActivePane.Index == 1 && form.Panes[1].ContainsFocus, "F6 moved to pane 2");
            Assert.False(form.Panes[0].IsActive);

            form.ActivateNextPane(-1);
            Assert.Equal(0, form.ActivePane.Index);
            form.ActivateNextPane(-1);
            Assert.Equal(3, form.ActivePane.Index);

            form.ActivatePane(2);
            await WaitUntil(() => form.Panes[2].ContainsFocus, "Ctrl+Alt+3 target has focus");
        });
    }

    [Fact]
    public void ClickingInsideShellView_ActivatesThatPane()
    {
        RunMainForm(PaneLayout.Default, async form =>
        {
            await WaitUntil(() => form.Panes.All(pane => pane.Browser.ItemCount > 0), "all panes enumerated");
            Assert.Equal(0, form.ActivePane.Index);

            // The click lands on a native shell window, which WinForms focus events never see.
            IntPtr shellWindow = FirstChildWindow(form.Panes[3].Browser.Handle);
            Assert.NotEqual(IntPtr.Zero, shellWindow);
            PostMessage(shellWindow, WM_LBUTTONDOWN, MK_LBUTTON, MakePoint(2, 2));
            PostMessage(shellWindow, WM_LBUTTONUP, 0, MakePoint(2, 2));

            await WaitUntil(() => form.ActivePane.Index == 3, "click activated pane 4");
        });
    }

    [Fact]
    public void ShrinkingLayout_MovesActivePaneToAVisibleOne()
    {
        RunMainForm(PaneLayout.Default, async form =>
        {
            await WaitUntil(() => form.Panes.All(pane => pane.Browser.CurrentLocation is not null), "all panes browsed");
            form.ActivatePane(3);
            ShellLocation pane4Location = form.Panes[3].Browser.CurrentLocation!;

            form.LayoutPanel.CurrentLayout = PaneLayout.FromId(1)!;
            Assert.Equal(0, form.ActivePane.Index);
            form.ActivatePane(3);
            Assert.Equal(0, form.ActivePane.Index);

            form.LayoutPanel.CurrentLayout = PaneLayout.FromId(13)!;
            Assert.True(form.Panes[3].Visible);
            Assert.Equal(pane4Location, form.Panes[3].Browser.CurrentLocation);
        });
    }

    private static void RunMainForm(PaneLayout layout, Func<MainForm, Task> body) =>
        Run(() =>
        {
            var form = new MainForm(ShellLocation.Parse(WindowsDir)!) { Size = new Size(1200, 800) };
            form.LayoutPanel.CurrentLayout = layout;
            return form;
        }, body);

    private static bool IsAt(PaneControl pane, string path) =>
        string.Equals(pane.Browser.CurrentLocation?.FileSystemPath, path, StringComparison.OrdinalIgnoreCase);
}
