using System.Diagnostics;
using MDir.Commands;
using MDir.Features;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

internal sealed class FakeClipboard : IClipboardService
{
    public (IReadOnlyList<string> Paths, bool Cut)? Files { get; set; }

    public string? Text { get; private set; }

    public bool HasFiles() => Files is not null;

    public (IReadOnlyList<string> Paths, bool Cut)? GetFiles() => Files;

    public void SetFiles(IReadOnlyList<string> paths, bool cut) => Files = (paths, cut);

    public void ClearFiles() => Files = null;

    public void SetText(string text) => Text = text;
}

internal sealed class FakeLauncher : IProcessLauncher
{
    public List<ProcessStartInfo> Started { get; } = [];

    public void Start(ProcessStartInfo info) => Started.Add(info);
}

/// <summary>The command system through the real main window: menus, toolbar, shortcuts and file commands.</summary>
public class CommandIntegrationTests(ITestOutputHelper output)
{
    private static TempDirectory CreateRoot()
    {
        var root = new TempDirectory();
        Directory.CreateDirectory(root.File("src"));
        Directory.CreateDirectory(root.File("dest"));
        foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(root.File(@"src\" + name), name);
        return root;
    }

    /// <summary>Two panes side by side: pane 1 shows src, pane 2 shows dest.</summary>
    private static Workspace TwoPanes(TempDirectory root, int layoutId = 2)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(root.File("src"))!);
        return new Workspace
        {
            Layout = PaneLayout.FromId(layoutId)!,
            Ratios = PaneLayout.FromId(layoutId)!.DefaultRatios,
            Panes = [basis.Panes[0], basis.Panes[0] with { Tabs = [new TabState(ShellLocation.Parse(root.File("dest"))!, FolderViewMode.Details, 16)] }, .. basis.Panes.Skip(2)],
        };
    }

    private static MainForm CreateForm(TempDirectory root, Workspace? workspace = null, AppProfile? profile = null)
    {
        var form = new MainForm(null, workspace ?? TwoPanes(root), null, profile)
        {
            Size = new Size(1300, 800),
            SilentFileOperations = true,
            FileClipboard = new FakeClipboard(),
            ProcessLauncher = new FakeLauncher(),
        };
        return form;
    }

    private static async Task SelectInActivePane(MainForm form, int index)
    {
        form.ActivePane.Browser.SelectItem(index);
        await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 1, "one item selected");
    }

    private static IEnumerable<ToolStripItem> AllItems(ToolStripItemCollection items) =>
        items.Cast<ToolStripItem>().SelectMany(item => new[] { item }.Concat(item is ToolStripMenuItem { HasDropDownItems: true } parent ? AllItems(parent.DropDownItems) : []));

    [Fact]
    public void EveryCommand_IsInAMenu_AndDefaultShortcutsDoNotCollide()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), form =>
        {
            Command[] inMenus = [.. form.MainMenuStrip!.Items.Cast<ToolStripItem>().SelectMany(top => AllItems(((ToolStripMenuItem)top).DropDownItems))
                .Select(item => item.Tag).OfType<Command>()];

            string[] missing = [.. form.Commands.All.Select(c => c.Id).Except(inMenus.Select(c => c.Id))];
            Assert.Empty(missing);

            var used = new Dictionary<Keys, string>();
            foreach (Command command in form.Commands.All)
            {
                foreach (Keys key in command.DefaultShortcuts)
                    Assert.True(used.TryAdd(key, command.Id), $"{CommandRegistry.FormatKeys(key)} is the default of both {used.GetValueOrDefault(key)} and {command.Id}");
            }

            Assert.All(form.Toolbar!.Items.OfType<ToolStripButton>(), button => Assert.IsType<Command>(button.Tag));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void MenuItems_ShowShortcutsAndFollowEnabledAndCheckedStates()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            ToolStripMenuItem Top(string text) => form.MainMenuStrip!.Items.Cast<ToolStripMenuItem>().Single(item => item.Text == text);
            ToolStripMenuItem Find(ToolStripMenuItem top, string id) => AllItems(top.DropDownItems).OfType<ToolStripMenuItem>().Single(item => (item.Tag as Command)?.Id == id);

            ToolStripMenuItem edit = Top("편집(&E)");
            edit.ShowDropDown();
            Assert.False(Find(edit, "edit.copy").Enabled, "nothing is selected");
            Assert.Equal("Ctrl+C", Find(edit, "edit.copy").ShortcutKeyDisplayString);
            Assert.False(Find(edit, "edit.paste").Enabled, "the clipboard is empty");
            edit.HideDropDown();

            await SelectInActivePane(form, 0);
            edit.ShowDropDown();
            Assert.True(Find(edit, "edit.copy").Enabled);
            Assert.True(Find(edit, "edit.delete").Enabled);
            edit.HideDropDown();

            ToolStripMenuItem tabs = Top("탭(&T)");
            Assert.Equal("Ctrl+T", Find(tabs, "tab.new").ShortcutKeyDisplayString);

            ToolStripMenuItem layout = Top("레이아웃(&L)");
            layout.ShowDropDown();
            Assert.True(Find(layout, "layout.2").Checked);
            Assert.False(Find(layout, "layout.10").Checked);
            Assert.True(Find(layout, "pane.2").Enabled);
            Assert.False(Find(layout, "pane.4").Enabled, "pane 4 is not part of a two pane layout");
            layout.HideDropDown();
        });
    }

    [Fact]
    public void Shortcuts_RunCommands_ButNativeKeysAreLeftToTheFocusedControl()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");

            Assert.True(form.TryShortcut(Keys.F6));
            Assert.Equal(1, form.ActivePane.Index);
            Assert.True(form.TryShortcut(Keys.Shift | Keys.F6));
            Assert.Equal(0, form.ActivePane.Index);

            int tabs = form.ActivePane.Tabs.Count;
            Assert.True(form.TryShortcut(Keys.Control | Keys.T));
            Assert.Equal(tabs + 1, form.ActivePane.Tabs.Count);
            Assert.True(form.TryShortcut(Keys.Control | Keys.Alt | Keys.A), "an alternative shortcut of the same command");
            Assert.Equal(tabs + 2, form.ActivePane.Tabs.Count);

            await SelectInActivePane(form, 0);
            Assert.False(form.TryShortcut(Keys.Control | Keys.C), "Ctrl+C belongs to the shell view and text boxes");
            Assert.False(form.TryShortcut(Keys.Delete));
            Assert.False(form.TryShortcut(Keys.F2));

            Assert.False(form.TryShortcut(Keys.Alt | Keys.Left), "back is disabled without history, so the key is not swallowed");
            Assert.False(form.TryShortcut(Keys.Control | Keys.Alt | Keys.Z), "unbound keys are ignored");
        });
    }

    [Fact]
    public void ARebindingIsInterceptedSavedAndLoadedAgain()
    {
        using TempDirectory root = CreateRoot();
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());

        Run(() => CreateForm(root, profile: profile), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            await SelectInActivePane(form, 0);

            form.ApplyShortcuts(new Dictionary<string, Keys[]> { ["edit.copy"] = [Keys.Control | Keys.Alt | Keys.K], ["tab.new"] = [] });

            Assert.True(form.TryShortcut(Keys.Control | Keys.Alt | Keys.K));
            Assert.Equal(["a.txt"], ((FakeClipboard)form.FileClipboard).Files!.Value.Paths.Select(Path.GetFileName));
            int tabs = form.ActivePane.Tabs.Count;
            Assert.False(form.TryShortcut(Keys.Control | Keys.T), "removed shortcut");
            Assert.Equal(tabs, form.ActivePane.Tabs.Count);
        });

        AppSettings saved = AppSettings.Load(profile.Paths.SettingsFile);
        Assert.Equal("Ctrl+Alt+K", saved.Shortcuts["edit.copy"]);
        Assert.Equal(string.Empty, saved.Shortcuts["tab.new"]);
        Assert.Equal(2, saved.Shortcuts.Count);

        // A new window with the saved settings uses them (and shows them in the menu).
        var next = new AppProfile(profile.Paths, saved);
        Run(() => CreateForm(root, profile: next), form =>
        {
            Assert.Equal([Keys.Control | Keys.Alt | Keys.K], form.Commands.GetShortcuts(form.Commands.Get("edit.copy")));
            Assert.Empty(form.Commands.GetShortcuts(form.Commands.Get("tab.new")));
            Assert.Equal("Ctrl+Alt+K", form.Commands.ShortcutText(form.Commands.Get("edit.copy")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void CopyThenPaste_CopiesIntoTheOtherPanesFolder_CutThenPasteMoves()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3 && form.Panes[1].Browser.CurrentLocation is not null, "both panes ready");
            var clipboard = (FakeClipboard)form.FileClipboard;

            Assert.False(form.RunCommand("edit.paste"), "nothing to paste");
            await SelectInActivePane(form, 0);
            Assert.True(form.RunCommand("edit.copy"));
            Assert.False(clipboard.Files!.Value.Cut);

            form.ActivatePane(1);
            await WaitUntil(() => form.ActivePane.Index == 1, "pane 2 active");
            Assert.True(form.RunCommand("edit.paste"));
            await WaitUntil(() => File.Exists(root.File(@"dest\a.txt")) && form.RunningFileOperations == 0, "a.txt was pasted");
            Assert.Equal("a.txt", File.ReadAllText(root.File(@"dest\a.txt")));
            Assert.True(File.Exists(root.File(@"src\a.txt")));

            form.ActivatePane(0);
            await WaitUntil(() => form.ActivePane.Index == 0, "pane 1 active");
            await SelectInActivePane(form, 1);
            Assert.True(form.RunCommand("edit.cut"));
            Assert.True(clipboard.Files!.Value.Cut);
            form.ActivatePane(1);
            Assert.True(form.RunCommand("edit.paste"));
            await WaitUntil(() => File.Exists(root.File(@"dest\b.txt")) && form.RunningFileOperations == 0, "b.txt was moved");

            Assert.False(File.Exists(root.File(@"src\b.txt")), "a cut moves the file");
            Assert.Null(clipboard.Files);
        });
    }

    [Fact]
    public void TransferCommands_CopyAndMoveToTheOtherPane()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3 && form.Panes[1].Browser.CurrentLocation is not null, "both panes ready");

            await SelectInActivePane(form, 0);
            Assert.True(form.RunCommand("edit.copyToNext"));
            await WaitUntil(() => File.Exists(root.File(@"dest\a.txt")) && form.RunningFileOperations == 0, "a.txt was copied");
            Assert.True(File.Exists(root.File(@"src\a.txt")));

            await SelectInActivePane(form, 1);
            Assert.True(form.RunCommand("edit.moveToPane2"));
            await WaitUntil(() => File.Exists(root.File(@"dest\b.txt")) && form.RunningFileOperations == 0, "b.txt was moved");
            Assert.False(File.Exists(root.File(@"src\b.txt")));

            // Not available into the active pane itself or into a pane the layout does not show.
            await SelectInActivePane(form, 0);
            Assert.False(form.RunCommand("edit.copyToPane1"));
            Assert.False(form.RunCommand("edit.copyToPane4"));
        });
    }

    [Fact]
    public void Delete_RemovesTheSelection_AndNewFolderCreatesAndSelectsOne()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");

            await form.NewFolderAsync();
            Assert.True(Directory.Exists(root.File(@"src\새 폴더")));
            await WaitUntil(() => form.Panes[0].Browser.SelectedCount == 1, "the new folder is selected");
            Assert.EndsWith("새 폴더", form.Panes[0].Browser.GetFirstSelected()!.FileSystemPath);

            await form.NewFolderAsync();
            Assert.True(Directory.Exists(root.File(@"src\새 폴더 (2)")));

            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 5, "five items");
            form.Panes[0].Browser.SelectAll();
            await WaitUntil(() => form.Panes[0].Browser.SelectedCount == 5, "all selected");
            Task deleting = form.DeleteSelectionAsync(permanent: true);

            await WaitUntil(() => Directory.GetFileSystemEntries(root.File("src")).Length == 0, "everything was deleted");
            await deleting;
            Assert.Equal(0, form.RunningFileOperations);
        });
    }

    [Fact]
    public void Delete_RunsOffTheWindowThread_SoTheWindowStaysUsableMeanwhile()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            form.Panes[0].Browser.SelectAll();
            await WaitUntil(() => form.Panes[0].Browser.SelectedCount == 3, "all selected");

            int windowThread = Environment.CurrentManagedThreadId;
            int? operationThread = null;
            Task<FileOperationResult> running = new FileOperations(IntPtr.Zero, silent: true).InBackground(operations =>
            {
                operationThread = Environment.CurrentManagedThreadId;
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                return operations.Delete([root.File(@"src\a.txt")], permanent: true);
            });

            // The window thread is free to do other work (here: run a command) while the operation is in flight.
            Assert.True(form.RunCommand("edit.copyPath"));
            Assert.True((await running).Success);
            Assert.NotEqual(windowThread, operationThread);
            Assert.False(File.Exists(root.File(@"src\a.txt")));
        });
    }

    [Fact]
    public void TheDeleteKeyInTheFileList_IsTakenOver_ButNotInATextBox()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");

            Assert.False(form.TryDeleteKey(Keys.Delete, listFocused: true), "nothing is selected");

            await SelectInActivePane(form, 0);
            Assert.False(form.TryDeleteKey(Keys.Delete, listFocused: false), "a text box or the rename box keeps its Delete key");
            Assert.False(form.TryDeleteKey(Keys.F2, listFocused: true));

            Assert.True(form.TryDeleteKey(Keys.Shift | Keys.Delete, listFocused: true));
            await WaitUntil(() => !File.Exists(root.File(@"src\a.txt")) && form.RunningFileOperations == 0, "a.txt was deleted");
        });
    }

    [Fact]
    public void PathCommands_PutTheRightTextOnTheClipboard()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            var clipboard = (FakeClipboard)form.FileClipboard;

            // No selection: the folder itself.
            Assert.True(form.RunCommand("edit.copyPath"));
            Assert.Equal(root.File("src"), clipboard.Text, ignoreCase: true);

            form.Panes[0].Browser.SelectAll();
            await WaitUntil(() => form.Panes[0].Browser.SelectedCount == 3, "all selected");
            form.RunCommand("edit.copyName");
            Assert.Equal("a.txt\r\nb.txt\r\nc.txt", clipboard.Text);

            form.RunCommand("edit.copyPath");
            Assert.Equal($"{root.File(@"src\a.txt")}\r\n{root.File(@"src\b.txt")}\r\n{root.File(@"src\c.txt")}", clipboard.Text, ignoreCase: true);

            form.RunCommand("edit.copyFolderPath");
            Assert.Equal(root.File("src"), clipboard.Text, ignoreCase: true);
        });
    }

    [Fact]
    public void OpenHere_StartsTheChosenProgramInTheActiveFolder()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.CurrentLocation is not null, "pane ready");
            var launcher = (FakeLauncher)form.ProcessLauncher;

            Assert.True(form.RunCommand("tools.openCmd"));
            Assert.True(form.RunCommand("tools.openPowerShell"));
            Assert.True(form.RunCommand("tools.openExplorer"));

            Assert.Equal(["cmd.exe", "powershell.exe", "explorer.exe"], launcher.Started.Select(info => info.FileName));
            Assert.Equal(root.File("src"), launcher.Started[0].WorkingDirectory, ignoreCase: true);
            Assert.Equal(root.File("src"), launcher.Started[2].ArgumentList.Single(), ignoreCase: true);

            // A virtual folder has no path to open.
            form.ActivePane.Browser.Navigate("shell:MyComputerFolder");
            await WaitUntil(() => form.ActivePane.Browser.CurrentLocation?.FileSystemPath is null, "This PC");
            Assert.False(form.RunCommand("tools.openCmd"));
            Assert.Equal(3, launcher.Started.Count);
        });
    }

    [Fact]
    public void SelectionCommands_AndRename()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");

            Assert.True(form.RunCommand("edit.selectAll"));
            await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 3, "all selected");
            Assert.True(form.RunCommand("edit.invertSelection"));
            await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 0, "none selected");

            Assert.False(form.RunCommand("edit.rename"), "rename needs a selection");
            await SelectInActivePane(form, 1);
            Assert.True(form.RunCommand("edit.rename"));
            await WaitUntil(() => NativeMethods.FindDescendant(form.ActivePane.Browser.Handle, "Edit") != IntPtr.Zero, "the edit box opened");
        });
    }

    [Fact]
    public void ToolbarButtons_FollowTheCommandStates()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            ToolStripButton Button(string id) => form.Toolbar!.Items.OfType<ToolStripButton>().Single(b => (b.Tag as Command)?.Id == id);

            await WaitUntil(() => !Button("edit.copy").Enabled, "copy is off without a selection");
            await SelectInActivePane(form, 0);
            await WaitUntil(() => Button("edit.copy").Enabled && Button("edit.delete").Enabled, "copy and delete turn on");

            Assert.False(Button("go.back").Enabled);
            Assert.False(Button("view.preview").Checked);
            form.TogglePreview();
            await WaitUntil(() => Button("view.preview").Checked, "the preview button shows it is on");

            form.ToggleTree();
            await WaitUntil(() => Button("view.tree").Checked, "and the tree button too");
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "toolbar.png")}");
        });
    }

    [Fact]
    public void FullScreenToolbarAndMenuBarToggles()
    {
        using TempDirectory root = CreateRoot();
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());
        Run(() => CreateForm(root, profile: profile), form =>
        {
            Assert.True(form.Toolbar!.Visible);
            Assert.True(form.RunCommand("window.toolbar"));
            Assert.False(form.Toolbar.Visible);
            Assert.False(AppSettings.Load(profile.Paths.SettingsFile).ShowToolbar);
            form.RunCommand("window.toolbar");
            Assert.True(form.Toolbar.Visible);

            Assert.True(form.MainMenuStrip!.Visible);
            Assert.True(form.TryShortcut(Keys.Alt | Keys.F11));
            Assert.False(form.MainMenuStrip.Visible);
            Assert.True(form.TryShortcut(Keys.Alt | Keys.F11));
            Assert.True(form.MainMenuStrip.Visible);

            FormBorderStyle border = form.FormBorderStyle;
            Assert.True(form.TryShortcut(Keys.F11));
            Assert.True(form.IsFullScreen);
            Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);
            Assert.Equal(FormWindowState.Maximized, form.WindowState);

            Assert.True(form.TryShortcut(Keys.F11));
            Assert.False(form.IsFullScreen);
            Assert.Equal(border, form.FormBorderStyle);
            Assert.Equal(FormWindowState.Normal, form.WindowState);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ErrorsFromFileCommands_AreReportedNotThrown()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3 && form.Panes[1].Browser.CurrentLocation is not null, "ready");
            var errors = new List<string>();
            form.ReportError = errors.Add;
            ((FakeClipboard)form.FileClipboard).Files = ([root.File(@"src\gone.txt")], false);

            form.ActivatePane(1);
            await WaitUntil(() => form.ActivePane.Index == 1, "pane 2 active");
            form.RunCommand("edit.paste");
            await WaitUntil(() => errors.Count > 0, "the error was reported");

            Assert.Single(errors);
            Assert.Contains("gone.txt", errors[0]);
        });
    }
}

public class ShortcutDialogTests
{
    private static CommandRegistry CreateRegistry()
    {
        var registry = new CommandRegistry();
        registry.Add(new Command("a", "&Alpha", () => { }) { Category = "G", DefaultShortcuts = [Keys.F1] });
        registry.Add(new Command("b", "&Beta", () => { }) { Category = "G", DefaultShortcuts = [Keys.F2, Keys.Control | Keys.B] });
        registry.Add(new Command("c", "&Gamma", () => { }) { Category = "H", NativeShortcut = true, DefaultShortcuts = [Keys.Control | Keys.C] });
        return registry;
    }

    [Fact]
    public void Assign_ReplacesTheShortcut_AndTakingAKeyFromAnotherCommandNeedsConfirmation()
    {
        RunSta(() =>
        {
            using var dialog = new ShortcutDialog(CreateRegistry());
            _ = dialog.Handle;
            var asked = new List<string>();
            dialog.ConfirmTakeOver = message =>
            {
                asked.Add(message);
                return false;
            };

            Assert.True(dialog.Assign("a", Keys.Control | Keys.Alt | Keys.X));
            Assert.Equal([Keys.Control | Keys.Alt | Keys.X], dialog.ShortcutsOf("a"));

            Assert.False(dialog.Assign("a", Keys.F2), "refused: Beta uses F2");
            Assert.Single(asked);
            Assert.Contains("Beta", asked[0]);
            Assert.Equal([Keys.Control | Keys.Alt | Keys.X], dialog.ShortcutsOf("a"));
            Assert.Contains(Keys.F2, dialog.ShortcutsOf("b"));

            dialog.ConfirmTakeOver = _ => true;
            Assert.True(dialog.Assign("a", Keys.F2));
            Assert.Equal([Keys.F2], dialog.ShortcutsOf("a"));
            Assert.Equal([Keys.Control | Keys.B], dialog.ShortcutsOf("b"));

            // Native defaults count as taken too.
            Assert.True(dialog.Assign("b", Keys.Control | Keys.C));
            Assert.Empty(dialog.ShortcutsOf("c"));
        });
    }

    [Fact]
    public void RemoveResetAndResetAll()
    {
        RunSta(() =>
        {
            using var dialog = new ShortcutDialog(CreateRegistry());
            _ = dialog.Handle;
            dialog.ConfirmTakeOver = _ => true;

            dialog.RemoveShortcuts("b");
            Assert.Empty(dialog.ShortcutsOf("b"));

            dialog.Assign("a", Keys.F5);
            dialog.ResetShortcuts("a");
            Assert.Equal([Keys.F1], dialog.ShortcutsOf("a"));

            dialog.Assign("b", Keys.F1);
            Assert.Empty(dialog.ShortcutsOf("a"));
            dialog.ResetShortcuts("a");
            Assert.Equal([Keys.F1], dialog.ShortcutsOf("a"));
            Assert.DoesNotContain(Keys.F1, dialog.ShortcutsOf("b"));

            dialog.RemoveShortcuts("c");
            dialog.ResetAll();
            Assert.Equal([Keys.F2, Keys.Control | Keys.B], dialog.ShortcutsOf("b"));
            Assert.Equal([Keys.Control | Keys.C], dialog.ShortcutsOf("c"));
        });
    }

    [Fact]
    public void List_ShowsEveryCommandWithItsShortcuts_AndChangesAreBold()
    {
        RunSta(() =>
        {
            using var dialog = new ShortcutDialog(CreateRegistry());
            _ = dialog.Handle;
            dialog.ConfirmTakeOver = _ => true;

            Assert.Equal(3, dialog.List.Items.Count);
            Assert.Equal(["G", "Alpha", "F1"], dialog.List.Items[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text));
            Assert.Equal("F2, Ctrl+B", dialog.List.Items[1].SubItems[2].Text);

            dialog.Assign("a", Keys.F9);
            Assert.True(dialog.List.Items[0].Font.Bold);
            Assert.False(dialog.List.Items[1].Font.Bold);
            Assert.Equal("F9", dialog.List.Items[0].SubItems[2].Text);
        });
    }

    [Fact]
    public void Result_IsTheWholeTable()
    {
        RunSta(() =>
        {
            using var dialog = new ShortcutDialog(CreateRegistry());
            _ = dialog.Handle;

            IReadOnlyDictionary<string, Keys[]> result = dialog.Result;

            Assert.Equal(["a", "b", "c"], result.Keys.Order());
            Assert.Equal([Keys.F2, Keys.Control | Keys.B], result["b"]);
        });
    }
}
