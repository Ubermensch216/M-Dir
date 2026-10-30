using Microsoft.Win32;
using MDir.Commands;
using MDir.Integration;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class StartupArgumentsTests
{
    [Fact]
    public void Parse_SeparatesFolderWorkspaceAndOptions()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\work\day.mdr" };
        StartupArguments arguments = StartupArguments.Parse(["-explore", @"D:\data", @"C:\work\day.mdr", "-new", "-minimized"], existing.Contains);

        Assert.Equal(@"D:\data", arguments.Folder);
        Assert.Equal(@"C:\work\day.mdr", arguments.WorkspaceFile);
        Assert.True(arguments.Explore);
        Assert.True(arguments.NewInstance);
        Assert.True(arguments.Minimized);
        Assert.True(arguments.HasTarget);
    }

    [Fact]
    public void Parse_AcceptsSlashAndDoubleDashOptions_AndIgnoresUnknownOnes()
    {
        StartupArguments arguments = StartupArguments.Parse(["/e", "--minimized", "-whatever", "/unknown"]);

        Assert.True(arguments.Explore);
        Assert.True(arguments.Minimized);
        Assert.False(arguments.NewInstance);
        Assert.False(arguments.HasTarget);
    }

    [Fact]
    public void Parse_FirstFolderWins_AndAMissingWorkspaceFileIsTakenAsAFolderArgument()
    {
        StartupArguments arguments = StartupArguments.Parse([@"C:\a", @"C:\b", @"C:\nothing.mdr"], _ => false);

        Assert.Equal(@"C:\a", arguments.Folder);
        Assert.Null(arguments.WorkspaceFile);
    }

    [Fact]
    public void Parse_WindowsPathsStartingWithASlashAreNotOptions()
    {
        // "/data/sub" and "/c:" contain more slashes or a colon: they are paths, not option words.
        StartupArguments arguments = StartupArguments.Parse(["/data/sub"]);

        Assert.Equal("/data/sub", arguments.Folder);
    }

    [Fact]
    public void Absolutize_MakesExistingRelativePathsAbsoluteAndKeepsOptions()
    {
        using var temp = new TempDirectory();
        string previous = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(temp.File("sub"));
        Directory.SetCurrentDirectory(temp.Path);
        try
        {
            string[] result = [.. StartupArguments.Absolutize(["sub", "-explore", "missing"])];

            Assert.Equal(Path.GetFullPath("sub"), result[0]);
            Assert.True(Path.IsPathRooted(result[0]));
            Assert.Equal("-explore", result[1]);
            Assert.Equal("missing", result[2]);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }
}

public class SingleInstanceTests
{
    private static string UniqueName() => "M-Dir-Test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SecondInstance_HandsItsArgumentsToTheFirst()
    {
        string name = UniqueName();
        using var first = new SingleInstance(name);
        Assert.True(first.IsFirst);

        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args => received.TrySetResult(args);
        first.StartListening();

        using var second = new SingleInstance(name);
        Assert.False(second.IsFirst);
        Assert.True(SingleInstance.TrySend(name, [@"C:\my folder", "-explore"], TimeSpan.FromSeconds(5)));

        string[] args = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([@"C:\my folder", "-explore"], args);
    }

    [Fact]
    public async Task TheFirstInstanceKeepsListeningForMoreThanOneMessage()
    {
        string name = UniqueName();
        using var first = new SingleInstance(name);
        var all = new List<string[]>();
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args =>
        {
            lock (all)
            {
                all.Add(args);
                if (all.Count == 2)
                    two.TrySetResult();
            }
        };
        first.StartListening();

        Assert.True(SingleInstance.TrySend(name, ["one"], TimeSpan.FromSeconds(5)));
        Assert.True(SingleInstance.TrySend(name, [], TimeSpan.FromSeconds(5)));
        await two.Task.WaitAsync(TimeSpan.FromSeconds(10));

        lock (all)
        {
            Assert.Equal(["one"], all[0]);
            Assert.Empty(all[1]);
        }
    }

    [Fact]
    public void Send_ReturnsFalseWhenNobodyListens()
    {
        Assert.False(SingleInstance.TrySend(UniqueName(), ["x"], TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void AfterTheFirstInstanceEnds_TheNextOneIsFirstAgain()
    {
        string name = UniqueName();
        var first = new SingleInstance(name);
        first.Dispose();

        using var next = new SingleInstance(name);
        Assert.True(next.IsFirst);
    }
}

public sealed class ShellRegistrationTests : IDisposable
{
    private const string TestRoot = @"Software\MDirTests";
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly ShellRegistration _registration;

    public ShellRegistrationTests() =>
        _registration = new ShellRegistration(@"C:\Program Files\M-Dir\M-Dir.exe", $@"{TestRoot}\{_id}\Classes", $@"{TestRoot}\{_id}\Run");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree($@"{TestRoot}\{_id}", throwOnMissingSubKey: false);

    [Fact]
    public void ContextMenu_RegistersFolderBackgroundAndDriveEntries_AndRemovesThem()
    {
        Assert.False(_registration.IsContextMenuRegistered);

        _registration.RegisterContextMenu();

        Assert.True(_registration.IsContextMenuRegistered);
        Assert.Equal("\"C:\\Program Files\\M-Dir\\M-Dir.exe\" \"%1\"", Command(@"Directory\shell"));
        Assert.Equal("\"C:\\Program Files\\M-Dir\\M-Dir.exe\" \"%V\"", Command(@"Directory\Background\shell"));
        Assert.Equal("\"C:\\Program Files\\M-Dir\\M-Dir.exe\" \"%1\"", Command(@"Drive\shell"));
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\{_id}\Classes\Directory\shell\M-Dir")!)
            Assert.Equal(ShellRegistration.MenuText, key.GetValue(null));

        _registration.UnregisterContextMenu();

        Assert.False(_registration.IsContextMenuRegistered);
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{TestRoot}\{_id}\Classes\Directory\Background\shell\M-Dir"));
    }

    [Fact]
    public void ContextMenu_RemovingWhatIsNotThereIsHarmless()
    {
        _registration.UnregisterContextMenu();
        _registration.UnregisterContextMenu();

        Assert.False(_registration.IsContextMenuRegistered);
    }

    [Fact]
    public void ContextMenu_ForAnotherExeCountsAsNotRegistered()
    {
        _registration.RegisterContextMenu();
        var moved = new ShellRegistration(@"D:\moved\M-Dir.exe", $@"{TestRoot}\{_id}\Classes", $@"{TestRoot}\{_id}\Run");

        Assert.False(moved.IsContextMenuRegistered);
    }

    [Fact]
    public void AutoStart_AddsAndRemovesTheRunValue()
    {
        Assert.False(_registration.IsAutoStart);

        _registration.SetAutoStart(true);
        Assert.True(_registration.IsAutoStart);
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\{_id}\Run")!)
            Assert.Equal("\"C:\\Program Files\\M-Dir\\M-Dir.exe\" -minimized", key.GetValue("M-Dir"));

        _registration.SetAutoStart(false);
        Assert.False(_registration.IsAutoStart);
        _registration.SetAutoStart(false);
    }

    private string? Command(string path)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\{_id}\Classes\{path}\M-Dir\command");
        return key?.GetValue(null) as string;
    }
}

public class ToolbarLayoutTests
{
    private static readonly HashSet<string> Known = ["a", "b", "c"];

    [Fact]
    public void Resolve_DropsUnknownDuplicatesAndStraySeparators()
    {
        IReadOnlyList<string> result = ToolbarLayout.Resolve(["-", "a", "-", "-", "zzz", "b", "a", "-"], Known.Contains);

        Assert.Equal(["a", "-", "b"], result);
    }

    [Fact]
    public void Resolve_KeepsTheLayoutDropDown_AndFallsBackToTheDefaultWhenNothingIsLeft()
    {
        Assert.Equal(["a", ToolbarLayout.Layouts], ToolbarLayout.Resolve(["a", ToolbarLayout.Layouts], Known.Contains));
        Assert.Equal(ToolbarLayout.Default, ToolbarLayout.Resolve(["nope", "-"], Known.Contains));
        Assert.Equal(ToolbarLayout.Default, ToolbarLayout.Resolve([], Known.Contains));
    }

    [Fact]
    public void ToSettings_StoresNothingForTheDefaultToolbar()
    {
        Assert.Empty(ToolbarLayout.ToSettings([.. ToolbarLayout.Default]));
        Assert.Equal(["a"], ToolbarLayout.ToSettings(["a"]));
    }
}

public class SystemSettingsTests
{
    [Fact]
    public void NewOptions_RoundTripThroughTheSettingsFile()
    {
        using var temp = new TempDirectory();
        string path = temp.File("M-Dir.ini");
        var settings = new AppSettings
        {
            SingleInstance = true,
            MinimizeToTray = true,
            ColorMode = AppColorMode.Dark,
            UiScale = 150,
        };
        settings.ToolbarItems.AddRange(["go.back", "-", "tab.new"]);
        settings.Save(path);

        AppSettings loaded = AppSettings.Load(path);

        Assert.True(loaded.SingleInstance);
        Assert.True(loaded.MinimizeToTray);
        Assert.Equal(AppColorMode.Dark, loaded.ColorMode);
        Assert.Equal(150, loaded.UiScale);
        Assert.Equal(["go.back", "-", "tab.new"], loaded.ToolbarItems);
    }

    [Fact]
    public void Defaults_AndBadValues_AreSafe()
    {
        using var temp = new TempDirectory();
        string path = temp.File("M-Dir.ini");
        File.WriteAllText(path, "[Appearance]\r\nColorMode=purple\r\nUiScale=133\r\n");

        AppSettings loaded = AppSettings.Load(path);

        Assert.False(loaded.SingleInstance);
        Assert.Equal(AppColorMode.System, loaded.ColorMode);
        Assert.Equal(125, loaded.UiScale);
        Assert.Empty(loaded.ToolbarItems);
    }

    [Theory]
    [InlineData(10, 100)]
    [InlineData(100, 100)]
    [InlineData(140, 150)]
    [InlineData(999, 200)]
    public void NormalizeScale_PicksTheNearestAllowedValue(int value, int expected) =>
        Assert.Equal(expected, AppAppearance.NormalizeScale(value));
}

/// <summary>The stage 11 commands through the real main window.</summary>
public class SystemIntegrationWindowTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static AppProfile CreateProfile(TempDirectory temp, bool portable = false)
    {
        if (portable)
            File.WriteAllText(temp.File(AppPaths.SettingsFileName), string.Empty);

        return new AppProfile(AppPaths.Resolve(appDirectory: temp.Path, roamingAppData: temp.Path), new AppSettings());
    }

    private static MainForm CreateForm(AppProfile? profile, List<string>? errors = null, string? registryId = null)
    {
        var form = new MainForm(ShellLocation.Parse(WindowsDir), null, null, profile)
        {
            Size = new Size(1200, 800),
            SilentFileOperations = true,
            ReportError = message => errors?.Add(message),
        };
        if (registryId is not null)
            form.Registration = new ShellRegistration(@"C:\x\M-Dir.exe", $@"Software\MDirTests\{registryId}\Classes", $@"Software\MDirTests\{registryId}\Run");
        return form;
    }

    private static void CleanRegistry(string id) => Registry.CurrentUser.DeleteSubKeyTree($@"Software\MDirTests\{id}", throwOnMissingSubKey: false);

    [Fact]
    public void EditedToolbar_IsRebuiltSavedAndComesBackAfterARestart()
    {
        using var temp = new TempDirectory();
        AppProfile profile = CreateProfile(temp);

        Run(() => CreateForm(profile), form =>
        {
            Assert.Equal(ToolbarLayout.Default.Count, form.Toolbar!.Items.Count);

            form.ApplyToolbar(["tab.new", "-", "view.refresh", "unknown.command"]);

            Assert.Equal(["tab.new", "-", "view.refresh"], form.ToolbarTokens);
            Assert.Equal(3, form.Toolbar!.Items.Count);
            Assert.Equal("새 탭", (form.Toolbar!.Items[0].ToolTipText ?? string.Empty).Split(" (")[0]);
            return Task.CompletedTask;
        });

        Assert.Equal(["tab.new", "-", "view.refresh"], AppSettings.Load(profile.Paths.SettingsFile).ToolbarItems);

        Run(() => CreateForm(new AppProfile(profile.Paths, AppSettings.Load(profile.Paths.SettingsFile))), form =>
        {
            Assert.Equal(3, form.Toolbar!.Items.Count);

            form.ApplyToolbar(ToolbarLayout.Default);

            Assert.Equal(ToolbarLayout.Default.Count, form.Toolbar!.Items.Count);
            Assert.Empty(AppSettings.Load(profile.Paths.SettingsFile).ToolbarItems);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ToolbarCommand_OpensTheEditorAndAppliesItsResult()
    {
        using var temp = new TempDirectory();
        AppProfile profile = CreateProfile(temp);

        Run(() => CreateForm(profile), form =>
        {
            form.RunDialog = dialog =>
            {
                var editor = (MDir.Features.ToolbarDialog)dialog;
                editor.Reset();
                editor.AddToken(ToolbarLayout.Separator);
                editor.RemoveSelected();
                while (editor.Result.Count > 2)
                {
                    editor.RemoveSelected();
                }

                return DialogResult.OK;
            };

            Assert.True(form.RunCommand("tools.toolbar"));

            Assert.Equal(2, form.ToolbarTokens.Count);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Tray_HidesTheWindowWhenMinimizedAndRestoresIt()
    {
        using var temp = new TempDirectory();
        AppProfile profile = CreateProfile(temp);
        profile.Settings.MinimizeToTray = true;

        Run(() => CreateForm(profile), async form =>
        {
            form.WindowState = FormWindowState.Minimized;
            await WaitUntil(() => form.IsHiddenInTray, "hidden in the tray");

            Assert.False(form.Visible);
            Assert.True(form.TrayIcon!.Visible);

            form.ShowAndActivate();

            Assert.True(form.Visible);
            Assert.False(form.IsHiddenInTray);
            Assert.False(form.TrayIcon.Visible);
            Assert.NotEqual(FormWindowState.Minimized, form.WindowState);
        });
    }

    [Fact]
    public void WithoutTheOption_MinimizingStaysOnTheTaskbar()
    {
        Run(() => CreateForm(null), async form =>
        {
            form.WindowState = FormWindowState.Minimized;
            await Task.Delay(200);

            Assert.False(form.IsHiddenInTray);
            Assert.Null(form.TrayIcon);
            form.WindowState = FormWindowState.Normal;
        });
    }

    [Fact]
    public void OpenArguments_OpensAFolderInANewTab_AndExploreShowsTheTree()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("incoming"));

        Run(() => CreateForm(null), async form =>
        {
            await WaitUntil(() => form.ActivePane.Browser.CurrentLocation is not null, "first folder");
            int tabs = form.ActivePane.Tabs.Count;

            form.OpenArguments(StartupArguments.Parse([temp.File("incoming"), "-explore"]));

            await WaitUntil(() => form.ActivePane.Browser.CurrentLocation?.FileSystemPath is { } path
                && string.Equals(path, temp.File("incoming"), StringComparison.OrdinalIgnoreCase), "incoming opened");
            Assert.Equal(tabs + 1, form.ActivePane.Tabs.Count);
            Assert.True(form.ActivePane.TreeEnabled);
        });
    }

    [Fact]
    public void OpenArguments_WithAWorkspaceFile_ReplacesTheArrangement()
    {
        using var temp = new TempDirectory();
        Workspace single = Workspace.CreateDefault(ShellLocation.Parse(temp.Path)!);
        single = new Workspace { Layout = MDir.Layout.PaneLayout.FromId(1)!, Ratios = MDir.Layout.PaneLayout.FromId(1)!.DefaultRatios, Panes = single.Panes };
        single.Save(temp.File("one.mdr"));

        Run(() => CreateForm(null), async form =>
        {
            form.OpenArguments(StartupArguments.Parse([temp.File("one.mdr")]));

            await WaitUntil(() => form.LayoutPanel.CurrentLayout.Id == 1, "layout of the workspace");
            Assert.Equal(temp.File("one.mdr"), form.WorkspacePath);
        });
    }

    [Fact]
    public void RegistryCommands_WriteThroughTheRegistration_AndAreOffInPortableMode()
    {
        string id = Guid.NewGuid().ToString("N");
        using var temp = new TempDirectory();
        try
        {
            Run(() => CreateForm(CreateProfile(temp), registryId: id), form =>
            {
                Assert.True(form.Commands.Get("window.autoStart").IsEnabled);
                Assert.True(form.RunCommand("window.autoStart"));
                Assert.True(form.Registration.IsAutoStart);
                Assert.True(form.Commands.Get("window.autoStart").IsChecked!());

                Assert.True(form.RunCommand("window.explorerMenu"));
                Assert.True(form.Registration.IsContextMenuRegistered);

                Assert.True(form.RunCommand("window.autoStart"));
                Assert.True(form.RunCommand("window.explorerMenu"));
                Assert.False(form.Registration.IsAutoStart);
                Assert.False(form.Registration.IsContextMenuRegistered);
                return Task.CompletedTask;
            });
        }
        finally
        {
            CleanRegistry(id);
        }

        using var portable = new TempDirectory();
        Run(() => CreateForm(CreateProfile(portable, portable: true)), form =>
        {
            Assert.False(form.Commands.Get("window.autoStart").IsEnabled);
            Assert.False(form.Commands.Get("window.explorerMenu").IsEnabled);
            Assert.False(form.RunCommand("window.autoStart"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void UiScale_ChangesTheWindowFontAndIsSaved()
    {
        using var temp = new TempDirectory();
        AppProfile profile = CreateProfile(temp);

        Run(() => CreateForm(profile), form =>
        {
            float normal = form.Font.Size;

            Assert.True(form.RunCommand("window.scale.150"));
            Assert.Equal(normal * 1.5f, form.Font.Size, 1);
            Assert.True(form.Panes[0].Font.Size > normal, "children follow the window font");

            Assert.True(form.RunCommand("window.scale.100"));
            Assert.Equal(normal, form.Font.Size, 1);
            return Task.CompletedTask;
        });

        Assert.Equal(100, AppSettings.Load(profile.Paths.SettingsFile).UiScale);
    }

    [Fact]
    public void DarkMode_TurnsTheListBackgroundDark_AndLightRestoresIt()
    {
        using var temp = new TempDirectory();
        AppProfile profile = CreateProfile(temp);

        Run(() => CreateForm(profile), async form =>
        {
            await WaitUntil(() => form.ActivePane.Browser.ItemCount > 0, "folder listed");
            IntPtr list = ListOf(form);

            form.SetColorMode(AppColorMode.Dark);
            await WaitUntil(() => Background(list) == 0x202020, "dark list background");
            Assert.True(form.IsDarkMode);

            form.SetColorMode(AppColorMode.Light);
            await WaitUntil(() => Background(list) != 0x202020, "list background back to the system colour");
            Assert.False(form.IsDarkMode);
        });

        Assert.Equal(AppColorMode.Light, AppSettings.Load(profile.Paths.SettingsFile).ColorMode);
    }

    private static IntPtr ListOf(MainForm form) => NativeMethods.FindDescendant(form.ActivePane.Browser.Handle, "SysListView32");

    private static uint Background(IntPtr list) => (uint)NativeMethods.SendMessage(list, NativeMethods.LVM_GETBKCOLOR, IntPtr.Zero, IntPtr.Zero);
}
