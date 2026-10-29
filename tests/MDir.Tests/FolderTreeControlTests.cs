using MDir.Panes;
using MDir.Settings;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Hosts the shell's real folder tree control. Shows a window briefly on the desktop.</summary>
public class FolderTreeControlTests(ITestOutputHelper output)
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System32Dir = Path.Combine(WindowsDir, "System32");

    [Fact]
    public void SelectLocation_SelectsDeepFolderWithoutRaisingTheUserEvent()
    {
        RunTree(async (form, tree) =>
        {
            int userSelections = 0;
            tree.LocationSelected += (_, _) => userSelections++;

            ShellLocation windows = ShellLocation.Parse(WindowsDir)!;
            tree.SelectLocation(windows);
            await WaitUntil(() => windows.Equals(tree.GetSelectedLocation()), "the tree selected the Windows folder");

            ShellLocation system32 = ShellLocation.Parse(System32Dir)!;
            tree.SelectLocation(system32);
            await WaitUntil(() => system32.Equals(tree.GetSelectedLocation()), "the tree selected System32");

            await Task.Delay(300);
            Assert.Equal(0, userSelections);
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "folder-tree.png")}");
        });
    }

    [Fact]
    public void SelectionBeforeTheHandleExists_IsAppliedWhenTheTreeIsCreated()
    {
        ShellLocation windows = ShellLocation.Parse(WindowsDir)!;
        RunTree(async (_, tree) =>
        {
            await WaitUntil(() => windows.Equals(tree.GetSelectedLocation()), "the early selection was applied");
        }, before: tree => tree.SelectLocation(windows));
    }

    [Fact]
    public void ArrowKeysInTheTree_RaiseLocationSelected()
    {
        RunTree(async (_, tree) =>
        {
            ShellLocation windows = ShellLocation.Parse(WindowsDir)!;
            tree.SelectLocation(windows);
            await WaitUntil(() => windows.Equals(tree.GetSelectedLocation()), "the tree selected the Windows folder");

            ShellLocation? picked = null;
            tree.LocationSelected += (_, e) => picked = e.Location;
            tree.FocusTree();
            PostMessage(GetFocus(), NativeMethods.WM_KEYDOWN, (IntPtr)Keys.Down, IntPtr.Zero);

            await WaitUntil(() => picked is not null, "Down arrow selected another folder");
            Assert.NotEqual(windows, picked);
            Assert.Equal(picked, tree.GetSelectedLocation());
        });
    }

    private static void RunTree(Func<Form, FolderTreeControl, Task> body, Action<FolderTreeControl>? before = null)
    {
        FolderTreeControl? tree = null;
        Run(() =>
        {
            var form = new Form { Width = 400, Height = 600, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir tree test" };
            tree = new FolderTreeControl { Dock = DockStyle.Fill, Options = new TreeOptions() };
            before?.Invoke(tree);
            form.Controls.Add(tree);
            return form;
        }, form => body(form, tree!));
    }
}
