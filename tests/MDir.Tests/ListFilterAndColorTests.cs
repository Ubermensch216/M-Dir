using MDir.Features;
using MDir.Panes;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Name filter and colour rules on the real shell list. Shows a window briefly on the desktop.</summary>
public class ListFilterAndColorTests(ITestOutputHelper output)
{
    [Fact]
    public void FileFilter_HidesMatchingFilesAndKeepsFoldersVisible()
    {
        using TempDirectory dir = CreateSampleFolder();
        RunHost(dir, async (_, host) =>
        {
            await WaitUntil(() => host.ItemCount == 6, "all items listed");

            host.FileFilter = new FileFilter("*.bak;Thumbs.db", Negative: true);
            await WaitUntil(() => host.ItemCount == 4, "the exclude filter removed .bak files and Thumbs.db");

            host.FileFilter = new FileFilter("*.txt");
            await WaitUntil(() => host.ItemCount == 3, "include filter keeps two .txt files and the folder");

            host.FileFilter = new FileFilter("*.txt", FilterFolders: true);
            await WaitUntil(() => host.ItemCount == 2, "folders are filtered too when asked");

            host.FileFilter = new FileFilter("report");
            await WaitUntil(() => host.ItemCount == 2, "a plain word matches anywhere in the name (report.txt, plus the folder)");

            host.FileFilter = FileFilter.None;
            await WaitUntil(() => host.ItemCount == 6, "clearing the filter shows everything again");
        });
    }

    [Fact]
    public void Filter_StaysActiveWhenNavigatingToAnotherFolder()
    {
        using TempDirectory dir = CreateSampleFolder();
        string sub = Path.Combine(dir.Path, "sub");
        File.WriteAllText(Path.Combine(sub, "x.bak"), "");
        File.WriteAllText(Path.Combine(sub, "y.txt"), "");

        RunHost(dir, async (_, host) =>
        {
            await WaitUntil(() => host.ItemCount == 6, "all items listed");
            host.FileFilter = new FileFilter("*.bak", Negative: true);
            await WaitUntil(() => host.ItemCount == 5, "filter applied");

            host.Navigate(sub);
            await WaitUntil(() => host.CurrentLocation?.FileSystemPath is { } p && p.EndsWith("sub", StringComparison.OrdinalIgnoreCase), "navigated");
            await WaitUntil(() => host.ItemCount == 1, "the filter also applied in the other folder");
        });
    }

    [Fact]
    public void SelectionChange_IsReportedByTheShellView()
    {
        using TempDirectory dir = CreateSampleFolder();
        RunHost(dir, async (_, host) =>
        {
            await WaitUntil(() => host.ItemCount == 6, "all items listed");
            await Task.Delay(300);
            var states = new List<uint>();
            host.ViewSite.StateChanged += states.Add;

            // The shell view reports changes while it is the active view.
            host.FocusView();
            Assert.True(host.SelectItem(1));

            // ICommDlgBrowser::OnStateChange(CDBOSC_SELCHANGE) arrives from the shell view (shortly after the change).
            await WaitUntil(() => states.Contains(CommDlgState.SelectionChanged), "the shell view reported the selection change");
            await WaitUntil(() => host.SelectedCount == 1, "the selected count was updated");
        });
    }

    [Fact]
    public void ColourRules_PaintMatchingItemsInTheirColours()
    {
        using TempDirectory dir = CreateSampleFolder();
        var magenta = Color.FromArgb(250, 0, 250);
        var yellow = Color.FromArgb(255, 255, 0);
        RunHost(dir, async (form, host) =>
        {
            await WaitUntil(() => host.ItemCount == 6, "all items listed");
            await Task.Delay(500);
            using (Bitmap before = Capture(form))
            {
                Assert.Equal(0, CountPixels(before, magenta, 60));
                Assert.Equal(0, CountPixels(before, yellow, 25));
            }

            host.ColorRules = new ColorRuleSet(
            [
                new ColorRule("*.exe", Text: magenta),
                new ColorRule("*.bak", Background: yellow),
            ]);
            await Task.Delay(500);

            using Bitmap after = Capture(form);
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "color-rules.png")}");
            Assert.True(CountPixels(after, magenta, 60) > 30, "text of the .exe items is magenta");
            Assert.True(CountPixels(after, yellow, 25) > 100, "the .bak items have a yellow background");

            host.ColorRules = null;
            await Task.Delay(500);
            using Bitmap off = Capture(form);
            Assert.Equal(0, CountPixels(off, magenta, 60));
        });
    }

    [Fact]
    public void ColourRules_AreStillAppliedAfterNavigatingToAnotherFolder()
    {
        using TempDirectory dir = CreateSampleFolder();
        string sub = Path.Combine(dir.Path, "sub");
        File.WriteAllText(Path.Combine(sub, "tool.exe"), "");
        var magenta = Color.FromArgb(250, 0, 250);
        RunHost(dir, async (form, host) =>
        {
            await WaitUntil(() => host.ItemCount == 6, "all items listed");
            host.ColorRules = new ColorRuleSet([new ColorRule("*.exe", Text: magenta)]);

            host.Navigate(sub);
            await WaitUntil(() => host.CurrentLocation?.FileSystemPath is { } p && p.EndsWith("sub", StringComparison.OrdinalIgnoreCase), "navigated");
            await WaitUntil(() => host.ItemCount == 1, "items listed");
            await Task.Delay(500);

            using Bitmap bitmap = Capture(form);
            Assert.True(CountPixels(bitmap, magenta, 60) > 15, "the new view is coloured too");
        });
    }

    [Fact]
    public void ListBackgroundRules_FollowTheActivePane()
    {
        using TempDirectory dir = CreateSampleFolder();
        var inactive = Color.FromArgb(230, 240, 255);
        var active = Color.FromArgb(255, 245, 200);
        RunHost(dir, async (form, host) =>
        {
            await WaitUntil(() => host.ItemCount == 6, "all items listed");
            host.ColorRules = new ColorRuleSet([new ColorRule("#BG", Text: inactive), new ColorRule("#BG-A", Text: active)]);
            await Task.Delay(400);
            using (Bitmap bitmap = Capture(form))
                Assert.True(CountPixels(bitmap, inactive, 4) > 5000, "inactive pane uses #BG");

            host.IsActivePane = true;
            await Task.Delay(400);
            using (Bitmap bitmap = Capture(form))
                Assert.True(CountPixels(bitmap, active, 4) > 5000, "the active pane uses #BG-A");

            host.ColorRules = null;
            await Task.Delay(400);
            using Bitmap off = Capture(form);
            Assert.Equal(0, CountPixels(off, active, 4));
        });
    }

    /// <summary>Two text files, two .bak/.db clutter files, an exe-like file and a folder: six items.</summary>
    private static TempDirectory CreateSampleFolder()
    {
        var dir = new TempDirectory();
        foreach (string name in new[] { "report.txt", "notes.txt", "old.bak", "Thumbs.db", "setup.exe" })
            File.WriteAllText(dir.File(name), "x");
        Directory.CreateDirectory(dir.File("sub"));
        return dir;
    }

    private static void RunHost(TempDirectory dir, Func<Form, ExplorerBrowserHost, Task> body)
    {
        ExplorerBrowserHost? host = null;
        Run(() =>
        {
            var form = new Form { Width = 900, Height = 600, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir filter test" };
            host = new ExplorerBrowserHost { Dock = DockStyle.Fill };
            form.Controls.Add(host);
            form.Shown += (_, _) => host.Navigate(dir.Path);
            return form;
        }, form => body(form, host!));
    }
}
