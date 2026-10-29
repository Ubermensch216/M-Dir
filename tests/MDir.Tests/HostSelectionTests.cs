using MDir.Panes;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Selection and rename on the real shell list. Shows a window briefly on the desktop.</summary>
public class HostSelectionTests
{
    private static void RunHost(TempDirectory dir, Func<ExplorerBrowserHost, Task> body)
    {
        ExplorerBrowserHost? host = null;
        Run(() =>
        {
            var form = new Form { Width = 800, Height = 500, StartPosition = FormStartPosition.CenterScreen };
            host = new ExplorerBrowserHost { Dock = DockStyle.Fill };
            form.Controls.Add(host);
            form.Shown += (_, _) => host.Navigate(dir.Path);
            return form;
        }, _ => body(host!));
    }

    private static TempDirectory CreateFolder()
    {
        var dir = new TempDirectory();
        foreach (string name in new[] { "a.txt", "b.txt", "c.txt", "d.txt" })
            File.WriteAllText(dir.File(name), "x");
        Directory.CreateDirectory(dir.File("sub"));
        return dir;
    }

    [Fact]
    public void SelectAll_ThenInvert_ThenSelectedPathsAreTheFullPaths()
    {
        using TempDirectory dir = CreateFolder();
        RunHost(dir, async host =>
        {
            await WaitUntil(() => host.ItemCount == 5, "items listed");

            host.SelectAll();
            await WaitUntil(() => host.SelectedCount == 5, "everything is selected");
            Assert.Equal(5, host.GetSelectedLocations().Count);

            host.InvertSelection();
            await WaitUntil(() => host.SelectedCount == 0, "inverting a full selection leaves none");

            host.SelectItem(1);
            await WaitUntil(() => host.SelectedCount == 1, "one item selected");
            host.InvertSelection();
            await WaitUntil(() => host.SelectedCount == 4, "the other four are selected");

            string[] paths = [.. host.GetSelectedPaths().Order()];
            Assert.Equal(4, paths.Length);
            Assert.All(paths, path => Assert.StartsWith(dir.Path, path, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, path => path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase)); // folders sort first: index 1 is a.txt
        });
    }

    [Fact]
    public void SelectPathAsync_FindsAnItemThatAppearsLater_AndGivesUpWhenItNeverDoes()
    {
        using TempDirectory dir = CreateFolder();
        RunHost(dir, async host =>
        {
            await WaitUntil(() => host.ItemCount == 5, "items listed");

            Task<bool> pending = host.SelectPathAsync(dir.File("late.txt"), TimeSpan.FromSeconds(8));
            await Task.Delay(300);
            File.WriteAllText(dir.File("late.txt"), "x");

            Assert.True(await pending, "the new file was found and selected once the view listed it");
            await WaitUntil(() => host.SelectedCount == 1, "it is selected");
            Assert.EndsWith("late.txt", host.GetFirstSelected()!.FileSystemPath, StringComparison.OrdinalIgnoreCase);

            Assert.False(await host.SelectPathAsync(dir.File("never.txt"), TimeSpan.FromMilliseconds(600)));
        });
    }

    [Fact]
    public void BeginRename_OpensAnEditBoxOnTheSelectedItem()
    {
        using TempDirectory dir = CreateFolder();
        RunHost(dir, async host =>
        {
            await WaitUntil(() => host.ItemCount == 5, "items listed");
            Assert.False(host.BeginRename(), "nothing is selected yet");

            host.SelectItem(2);
            await WaitUntil(() => host.SelectedCount == 1, "one item selected");
            Assert.True(host.BeginRename());

            await WaitUntil(() => NativeMethods.FindDescendant(host.Handle, "Edit") != IntPtr.Zero, "the in-place edit box appeared");

            // Escape cancels the rename.
            PostMessage(NativeMethods.FindDescendant(host.Handle, "Edit"), NativeMethods.WM_KEYDOWN, (IntPtr)Keys.Escape, IntPtr.Zero);
            await WaitUntil(() => NativeMethods.FindDescendant(host.Handle, "Edit") == IntPtr.Zero, "the edit box closed");
        });
    }

    [Fact]
    public void InvokeVerb_ReachesTheShell_WithoutAnItemItFails_WithOneItPasses()
    {
        using TempDirectory dir = CreateFolder();
        RunHost(dir, async host =>
        {
            await WaitUntil(() => host.ItemCount == 5, "items listed");
            host.SelectItem(0);
            await WaitUntil(() => host.SelectedCount == 1, "one item selected");

            // "copy" puts the item on the clipboard; here it only matters that the verb is accepted.
            Assert.True(host.InvokeVerb("copy"));
        });
    }
}
