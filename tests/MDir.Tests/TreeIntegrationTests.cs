using MDir.Layout;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Folder trees inside panes and the window: list and tree stay in sync both ways. Shows a window briefly.</summary>
public class TreeIntegrationTests(ITestOutputHelper output)
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System32Dir = Path.Combine(WindowsDir, "System32");

    [Fact]
    public void PaneTree_IsCreatedOnDemandAndFollowsTheList()
    {
        RunPane(async pane =>
        {
            pane.Browser.Navigate(WindowsDir);
            await WaitUntil(() => IsAt(pane.Browser, WindowsDir), "pane opened Windows");
            Assert.Null(pane.Tree);

            pane.TreeEnabled = true;
            Assert.NotNull(pane.Tree);
            await WaitUntil(() => Equals(pane.Tree!.GetSelectedLocation(), pane.Browser.CurrentLocation), "tree shows the current folder");

            pane.Browser.Navigate(System32Dir);
            ShellLocation system32 = ShellLocation.Parse(System32Dir)!;
            await WaitUntil(() => system32.Equals(pane.Tree!.GetSelectedLocation()), "tree followed the list to System32");
        });
    }

    [Fact]
    public void SelectingInThePaneTree_NavigatesTheList()
    {
        RunPane(async pane =>
        {
            pane.Browser.Navigate(WindowsDir);
            await WaitUntil(() => IsAt(pane.Browser, WindowsDir), "pane opened Windows");
            pane.TreeEnabled = true;
            await WaitUntil(() => Equals(pane.Tree!.GetSelectedLocation(), pane.Browser.CurrentLocation), "tree shows the current folder");

            // A key press in the tree is a user selection: the list must follow it.
            ShellLocation before = pane.Browser.CurrentLocation!;
            pane.FocusTree();
            PostMessage(GetFocus(), NativeMethods.WM_KEYDOWN, (IntPtr)Keys.Down, IntPtr.Zero);

            await WaitUntil(() => !before.Equals(pane.Browser.CurrentLocation), "the list navigated to the tree's selection");
            await WaitUntil(() => Equals(pane.Tree!.GetSelectedLocation(), pane.Browser.CurrentLocation), "tree and list agree");
        });
    }

    [Fact]
    public void TabSwitch_MovesTheTreeSelectionToo()
    {
        RunPane(async pane =>
        {
            pane.Browser.Navigate(WindowsDir);
            await WaitUntil(() => IsAt(pane.Browser, WindowsDir), "pane opened Windows");
            pane.NewTab(ShellLocation.Parse(System32Dir), select: false);
            pane.TreeEnabled = true;
            await WaitUntil(() => Equals(pane.Tree!.GetSelectedLocation(), pane.Browser.CurrentLocation), "tree shows Windows");

            pane.SelectNextTab(1);

            ShellLocation system32 = ShellLocation.Parse(System32Dir)!;
            await WaitUntil(() => system32.Equals(pane.Tree!.GetSelectedLocation()), "tree followed the tab switch");
        });
    }

    [Fact]
    public void HidingAndShowingTheTree_KeepsTheWidthAndTheSelection()
    {
        RunPane(async pane =>
        {
            pane.Browser.Navigate(WindowsDir);
            await WaitUntil(() => IsAt(pane.Browser, WindowsDir), "pane opened Windows");
            pane.TreeSplit = 4000;
            pane.TreeEnabled = true;
            await WaitUntil(() => pane.Tree!.Visible, "tree visible");
            Assert.InRange(pane.Tree!.Width * 10000 / pane.ClientSize.Width, 3500, 4300);

            pane.TreeEnabled = false;
            Assert.False(pane.Tree.Visible);
            pane.Browser.Navigate(System32Dir);
            await WaitUntil(() => IsAt(pane.Browser, System32Dir), "pane opened System32 while the tree was hidden");

            pane.TreeEnabled = true;
            ShellLocation system32 = ShellLocation.Parse(System32Dir)!;
            await WaitUntil(() => system32.Equals(pane.Tree!.GetSelectedLocation()), "the tree caught up when shown again");
            Assert.Equal(4000, pane.TreeSplit);
        });
    }

    [Fact]
    public void SharedTree_FollowsTheActivePane_AndNavigatesIt()
    {
        RunMainForm(CreateWorkspace(sharedTreeVisible: true), async form =>
        {
            await WaitUntil(() => form.Panes.All(pane => pane.Browser.ItemCount > 0), "all panes enumerated");
            Assert.True(form.SharedTreeMode);
            Assert.NotNull(form.SharedTree);
            Assert.All(form.Panes, pane => Assert.Null(pane.Tree));

            ShellLocation windows = ShellLocation.Parse(WindowsDir)!;
            await WaitUntil(() => windows.Equals(form.SharedTree!.GetSelectedLocation()), "shared tree shows the active pane's folder");

            form.ActivatePane(1);
            await WaitUntil(() => Equals(form.SharedTree!.GetSelectedLocation(), form.Panes[1].Browser.CurrentLocation),
                "shared tree followed the active pane");

            ShellLocation before = form.Panes[1].Browser.CurrentLocation!;
            form.SharedTree!.FocusTree();
            PostMessage(GetFocus(), NativeMethods.WM_KEYDOWN, (IntPtr)Keys.Down, IntPtr.Zero);
            await WaitUntil(() => !before.Equals(form.Panes[1].Browser.CurrentLocation), "the active pane navigated");
            Assert.Equal(windows, form.Panes[0].Browser.CurrentLocation);

            output.WriteLine($"Screenshot: {SaveScreenshot(form, "shared-tree.png")}");
        });
    }

    [Fact]
    public void ModeSwitch_KeepsEachModesOwnVisibility()
    {
        RunMainForm(CreateWorkspace(sharedTreeVisible: false), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount > 0, "pane 1 enumerated");
            Assert.False(form.SharedTreeMode);

            form.ToggleTree();
            Assert.True(form.ActivePane.TreeEnabled);
            Assert.NotNull(form.ActivePane.Tree);
            Assert.False(form.Panes[1].TreeEnabled);

            form.SetSharedTreeMode(true);
            Assert.False(form.ActivePane.Tree!.Visible, "the pane's tree is hidden, not forgotten, in shared mode");
            Assert.True(form.ActivePane.TreeEnabled);
            Assert.Null(form.SharedTree);

            form.ToggleTree();
            Assert.NotNull(form.SharedTree);
            await WaitUntil(() => Equals(form.SharedTree!.GetSelectedLocation(), form.ActivePane.Browser.CurrentLocation), "shared tree synced");

            form.SetSharedTreeMode(false);
            Assert.False(form.SharedTree!.Visible);
            Assert.True(form.ActivePane.Tree!.Visible);
        });
    }

    [Fact]
    public void TreeStateIsSavedAndRestored()
    {
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());

        RunMainForm(null, profile, async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount > 0, "pane 1 enumerated");
            form.ActivatePane(2);
            form.ToggleTree();
            form.Panes[2].TreeSplit = 3500;
            form.SetSharedTreeMode(true);
            form.ToggleTree();
        });

        Workspace saved = Workspace.Load(profile.Paths.StartWorkspaceFile)!;
        Assert.True(saved.SharedTree);
        Assert.True(saved.SharedTreeVisible);
        Assert.True(saved.Panes[2].TreeVisible);
        Assert.Equal(3500, saved.Panes[2].TreeSplit);
        Assert.False(saved.Panes[0].TreeVisible);

        RunMainForm(saved, profile, async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount > 0, "pane 1 enumerated");
            Assert.True(form.SharedTreeMode);
            Assert.NotNull(form.SharedTree);
            Assert.True(form.SharedTree!.Visible);
            Assert.Null(form.Panes[0].Tree);
            Assert.True(form.Panes[2].TreeEnabled);
            Assert.Equal(3500, form.Panes[2].TreeSplit);
        });
    }

    [Fact]
    public void Options_ChangeTheTreeStyleAndAreSavedInSettings()
    {
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());

        RunMainForm(null, profile, async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount > 0, "pane 1 enumerated");
            form.ToggleTree();
            profile.Settings.Tree.SingleClickExpand = true;
            profile.Settings.Tree.Lines = false;
            form.ActivePane.TreeOptions = profile.Settings.Tree;
            Assert.Same(profile.Settings.Tree, form.ActivePane.Tree!.Options);
        });

        var settings = new AppSettings();
        settings.Tree.Lines = false;
        settings.Tree.SingleClickExpand = true;
        settings.Tree.AutoExpand = false;
        settings.Save(profile.Paths.SettingsFile);
        TreeOptions loaded = AppSettings.Load(profile.Paths.SettingsFile).Tree;
        Assert.False(loaded.Lines);
        Assert.True(loaded.SingleClickExpand);
        Assert.False(loaded.AutoExpand);
        Assert.False(loaded.FullRowSelect);
    }

    private static Workspace CreateWorkspace(bool sharedTreeVisible)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(WindowsDir)!);
        return new Workspace
        {
            Layout = PaneLayout.Default,
            Ratios = PaneLayout.Default.DefaultRatios,
            Panes = basis.Panes,
            SharedTree = sharedTreeVisible,
            SharedTreeVisible = sharedTreeVisible,
        };
    }

    private static void RunPane(Func<PaneControl, Task> body)
    {
        PaneControl? pane = null;
        Run(() =>
        {
            var form = new Form { Width = 1000, Height = 600, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir tree test" };
            pane = new PaneControl(0) { Dock = DockStyle.Fill };
            form.Controls.Add(pane);
            return form;
        }, _ => body(pane!));
    }

    private static void RunMainForm(Workspace? workspace, Func<MainForm, Task> body) => RunMainForm(workspace, null, body);

    private static void RunMainForm(Workspace? workspace, AppProfile? profile, Func<MainForm, Task> body) =>
        Run(() =>
        {
            var form = new MainForm(workspace is null ? ShellLocation.Parse(WindowsDir) : null, workspace, null, profile);
            if (workspace is null)
                form.Size = new Size(1200, 800);
            return form;
        }, body);

    private static bool IsAt(ExplorerBrowserHost browser, string path) =>
        string.Equals(browser.CurrentLocation?.FileSystemPath, path, StringComparison.OrdinalIgnoreCase);
}
