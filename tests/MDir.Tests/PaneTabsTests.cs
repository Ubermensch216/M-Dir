using MDir.Panes;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Tabs on a real pane: each tab keeps its own folder, history and view mode. Shows a window briefly.</summary>
public class PaneTabsTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System32Dir = Path.Combine(WindowsDir, "System32");

    [Fact]
    public void Tabs_KeepTheirOwnLocationHistoryAndViewMode()
    {
        RunPane(async pane =>
        {
            ExplorerBrowserHost browser = pane.Browser;
            browser.Navigate(WindowsDir);
            await WaitUntil(() => IsAt(browser, WindowsDir), "first tab opened Windows");

            PaneTab first = pane.SelectedTab;
            PaneTab second = pane.NewTab();
            Assert.Equal(2, pane.Tabs.Count);
            Assert.Same(second, pane.SelectedTab);
            await WaitUntil(() => IsAt(browser, WindowsDir), "new tab opened the current folder");

            browser.Navigate(System32Dir);
            await WaitUntil(() => IsAt(browser, System32Dir), "second tab browsed to System32");
            browser.SetViewMode(FolderViewMode.Icon, 96);
            Assert.Equal("System32", second.Title, ignoreCase: true);
            Assert.True(browser.CanGoBack);

            pane.SelectNextTab(1);
            Assert.Same(first, pane.SelectedTab);
            await WaitUntil(() => IsAt(browser, WindowsDir), "first tab shows Windows again");
            Assert.False(browser.CanGoBack);
            await WaitUntil(() => browser.ViewMode == FolderViewMode.Details, "first tab kept Details view");
            Assert.Equal(FolderViewMode.Icon, second.ViewMode);

            pane.NewTab(ShellLocation.Parse(@"C:\Users"), select: false);
            await WaitUntil(() => browser.ItemCount > 0, "items enumerated");
            SaveScreenshot(pane.FindForm()!, "pane-tabs.png");

            pane.SelectNextTab(-1);
            await WaitUntil(() => IsAt(browser, System32Dir), "second tab restored");
            await WaitUntil(() => browser.ViewMode == FolderViewMode.Icon && browser.IconSize == 96, "second tab kept its view mode");
            Assert.True(browser.CanGoBack);
        });
    }

    [Fact]
    public void DuplicateTab_CopiesHistory_CloseTabSelectsNeighbourAndKeepsLastTab()
    {
        RunPane(async pane =>
        {
            ExplorerBrowserHost browser = pane.Browser;
            browser.Navigate(WindowsDir);
            await WaitUntil(() => IsAt(browser, WindowsDir), "opened Windows");
            browser.Navigate(System32Dir);
            await WaitUntil(() => IsAt(browser, System32Dir), "opened System32");

            PaneTab original = pane.SelectedTab;
            PaneTab copy = pane.DuplicateTab();
            await WaitUntil(() => IsAt(browser, System32Dir), "duplicate shows System32");

            browser.GoBack();
            await WaitUntil(() => IsAt(browser, WindowsDir), "duplicate went back");
            Assert.Equal(System32Dir, original.Location!.FileSystemPath, ignoreCase: true);

            pane.CloseTab(copy);
            Assert.Single(pane.Tabs);
            Assert.Same(original, pane.SelectedTab);
            await WaitUntil(() => IsAt(browser, System32Dir), "original tab shown after closing the copy");

            pane.CloseTab(original);
            Assert.Single(pane.Tabs);
        });
    }

    [Fact]
    public void NewTab_AtGivenLocation_CanOpenInBackground()
    {
        RunPane(async pane =>
        {
            pane.Browser.Navigate(WindowsDir);
            await WaitUntil(() => IsAt(pane.Browser, WindowsDir), "opened Windows");

            PaneTab background = pane.NewTab(ShellLocation.Parse(System32Dir), select: false);

            Assert.NotSame(background, pane.SelectedTab);
            Assert.Equal(1, pane.Tabs.ToList().IndexOf(background));
            Assert.Equal("System32", background.Title, ignoreCase: true);
            Assert.True(IsAt(pane.Browser, WindowsDir));
        });
    }

    private static void RunPane(Func<PaneControl, Task> body)
    {
        PaneControl? pane = null;
        Run(() =>
        {
            var form = new Form { Width = 900, Height = 600, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir tabs test" };
            pane = new PaneControl(0) { Dock = DockStyle.Fill };
            form.Controls.Add(pane);
            return form;
        }, _ => body(pane!));
    }

    private static bool IsAt(ExplorerBrowserHost browser, string path) =>
        string.Equals(browser.CurrentLocation?.FileSystemPath, path, StringComparison.OrdinalIgnoreCase);
}
