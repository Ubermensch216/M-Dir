using MDir.Layout;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>"Close the window and start it again" tests: the window state must come back from the saved workspace.</summary>
public class MainFormWorkspaceTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System32Dir = Path.Combine(WindowsDir, "System32");

    [Fact]
    public void ClosingSavesTheWorkspaceAndTheNextStartRestoresIt()
    {
        using var temp = new TempDirectory();
        AppProfile profile = CreateProfile(temp);
        var savedBounds = new Rectangle(60, 50, 1000, 700);

        RunMainForm(null, profile, async form =>
        {
            await WaitUntil(() => form.Panes.Take(3).All(pane => pane.Browser.CurrentLocation is not null), "panes opened");
            form.LayoutPanel.CurrentLayout = PaneLayout.FromId(6)!;
            form.LayoutPanel.SetRatio(0, 3000);
            form.Panes[0].NewTab(ShellLocation.Parse(System32Dir));
            form.Panes[1].Browser.SetViewMode(FolderViewMode.Icon, 96);
            form.ActivatePane(1);
            form.Bounds = savedBounds;
            await WaitUntil(() => form.Panes[0].Browser.ItemCount > 0 && form.Panes[1].Browser.ItemCount > 0, "panes enumerated");
        });

        Assert.True(File.Exists(profile.Paths.StartWorkspaceFile));
        Workspace saved = Workspace.Load(profile.Paths.StartWorkspaceFile)!;
        Assert.Equal(6, saved.Layout.Id);
        Assert.Equal(2, saved.Panes[0].Tabs.Count);

        RunMainForm(saved, profile, async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount > 0 && form.Panes[1].Browser.ItemCount > 0, "panes enumerated");

            Assert.Equal(6, form.LayoutPanel.CurrentLayout.Id);
            Assert.Equal(3000, form.LayoutPanel.Ratios[0]);
            Assert.Equal(1, form.ActivePane.Index);

            Assert.Equal(2, form.Panes[0].Tabs.Count);
            Assert.Equal(1, form.Panes[0].TabStrip.SelectedIndex);
            Assert.Equal(System32Dir, form.Panes[0].Browser.CurrentLocation!.FileSystemPath, ignoreCase: true);
            Assert.Equal(WindowsDir, form.Panes[0].Tabs[0].Location!.FileSystemPath, ignoreCase: true);

            await WaitUntil(() => form.Panes[1].Browser.ViewMode == FolderViewMode.Icon && form.Panes[1].Browser.IconSize == 96,
                "pane 2 view mode restored");
            Assert.False(form.Panes[3].Browser.IsHandleCreated, "the hidden pane has no shell view yet");
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                form.Panes[3].Tabs[0].Location!.FileSystemPath, ignoreCase: true);

            Assert.InRange(form.Bounds.X, savedBounds.X - 2, savedBounds.X + 2);
            Assert.InRange(form.Bounds.Y, savedBounds.Y - 2, savedBounds.Y + 2);
            Assert.InRange(form.Bounds.Width, savedBounds.Width - 2, savedBounds.Width + 2);
            Assert.InRange(form.Bounds.Height, savedBounds.Height - 2, savedBounds.Height + 2);
        });
    }

    [Fact]
    public void AutoWorkspaceOff_DoesNotWriteTheStartWorkspace()
    {
        using var temp = new TempDirectory();
        AppProfile profile = CreateProfile(temp);
        profile.Settings.AutoWorkspace = false;

        RunMainForm(null, profile, _ => Task.CompletedTask);

        Assert.False(File.Exists(profile.Paths.StartWorkspaceFile));
    }

    [Fact]
    public void Restore_FallsBackToAnExistingParentWhenAFolderWasDeleted()
    {
        using var temp = new TempDirectory();
        string gone = Path.Combine(temp.Path, "gone");
        Directory.CreateDirectory(gone);
        Workspace workspace = Workspace.CreateDefault(ShellLocation.Parse(gone)!);
        Directory.Delete(gone);

        RunMainForm(workspace, null, async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount >= 0 && form.Panes[0].Browser.CurrentLocation is not null, "pane 1 opened");

            Assert.Equal(temp.Path, form.Panes[0].Tabs[0].Location!.FileSystemPath, ignoreCase: true);
            Assert.Equal(temp.Path, form.Panes[0].Browser.CurrentLocation!.FileSystemPath, ignoreCase: true);
        });
    }

    [Fact]
    public void StartFolder_OpensInPaneOneOverTheSavedWorkspace()
    {
        Workspace saved = Workspace.CreateDefault(ShellLocation.Parse(WindowsDir)!);

        Run(() => new MainForm(ShellLocation.Parse(System32Dir), saved), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.CurrentLocation?.FileSystemPath is { } path
                && string.Equals(path, System32Dir, StringComparison.OrdinalIgnoreCase), "pane 1 shows the start folder");
            Assert.Equal(0, form.ActivePane.Index);
        });
    }

    [Fact]
    public void CaptureWorkspace_ReflectsLayoutTabsAndMultilineSetting()
    {
        RunMainForm(null, null, form =>
        {
            form.LayoutPanel.CurrentLayout = PaneLayout.FromId(3)!;
            form.LayoutPanel.SetRatio(0, 2500);
            form.Panes[2].NewTab(ShellLocation.Parse(System32Dir));

            Workspace captured = form.CaptureWorkspace();

            Assert.Equal(3, captured.Layout.Id);
            Assert.Equal([2500], captured.Ratios);
            Assert.Equal(4, captured.Panes.Count);
            Assert.Equal(2, captured.Panes[2].Tabs.Count);
            Assert.Equal(1, captured.Panes[2].SelectedTab);
            Assert.NotNull(captured.Window);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void CaptureWorkspace_WindowIsTheRealPositionEvenIfNeverMoved()
    {
        RunMainForm(null, null, form =>
        {
            // The form was only centered by the system: RestoreBounds would report -1,-1 here.
            WindowPlacement window = form.CaptureWorkspace().Window!;

            Assert.Equal(form.Bounds, new Rectangle(window.X, window.Y, window.Width, window.Height));
            Assert.False(window.Maximized);
            Assert.NotEqual(-1, window.X);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void CaptureWorkspace_MaximizedWindowSavesTheNormalSize()
    {
        RunMainForm(null, null, async form =>
        {
            Rectangle normal = form.Bounds;
            form.WindowState = FormWindowState.Maximized;
            await WaitUntil(() => form.WindowState == FormWindowState.Maximized, "maximized");

            WindowPlacement window = form.CaptureWorkspace().Window!;

            Assert.True(window.Maximized);
            Assert.Equal(normal.Size, new Size(window.Width, window.Height));
            Assert.Equal(normal.Location, new Point(window.X, window.Y));
        });
    }

    private static AppProfile CreateProfile(TempDirectory temp) =>
        new(AppPaths.Resolve(appDirectory: temp.Path, roamingAppData: temp.Path), new AppSettings());

    /// <summary>Like a normal launch: no folder argument when a workspace is restored (a folder would override pane 1).</summary>
    private static void RunMainForm(Workspace? workspace, AppProfile? profile, Func<MainForm, Task> body) =>
        Run(() =>
        {
            var form = new MainForm(workspace is null ? ShellLocation.Parse(WindowsDir) : null, workspace, null, profile);
            if (workspace is null)
                form.Size = new Size(1200, 800);
            return form;
        }, body);
}
