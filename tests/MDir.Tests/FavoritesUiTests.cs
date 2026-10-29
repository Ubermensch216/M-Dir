using System.Reflection;
using MDir.Features;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class FavoritesMenuTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static ShellLocation Loc(string path) => ShellLocation.Parse(path)!;

    private static FavoritesStore CreateStore(TempDirectory dir)
    {
        var store = new FavoritesStore(dir.File("Favorites"));
        store.EnsureCreated();
        return store;
    }

    /// <summary>Raises the item's MouseUp event (WinForms only does that for real mouse input).</summary>
    private static void RaiseMouseUp(ToolStripItem item, MouseButtons button)
    {
        var events = (System.ComponentModel.EventHandlerList)typeof(System.ComponentModel.Component)
            .GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!;
        FieldInfo key = typeof(ToolStripItem).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(field => field.Name.Contains("MouseUp", StringComparison.OrdinalIgnoreCase));
        ((MouseEventHandler?)events[key.GetValue(null)!])?.Invoke(item, new MouseEventArgs(button, 1, 1, 1, 0));
    }

    [Fact]
    public void Entries_ListQuickLinkFirstThenFavoritesAndFolders()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            FavoritesStore store = CreateStore(dir);
            store.AddLink(store.Root, Loc(WindowsDir), "Windows");
            string work = store.CreateFolder(store.Root, "Work");
            store.AddLink(work, Loc(Path.GetTempPath()), "Temp");
            var menu = new FavoritesMenu(store, () => null, (_, _) => { }, () => { });
            using var strip = new ContextMenuStrip();

            menu.AddEntries(strip.Items);

            Assert.Equal(["퀵링크", "Windows", "Work"], strip.Items.Cast<ToolStripItem>().Select(item => item.Text));
            Assert.All(strip.Items.Cast<ToolStripItem>(), item => Assert.NotNull(item.Image));

            // Folder items start with a placeholder and are filled when the sub-menu opens (see the integration test).
            var workItem = (ToolStripMenuItem)strip.Items[2];
            Assert.Equal(["…"], workItem.DropDownItems.Cast<ToolStripItem>().Select(item => item.Text));

            FavoritesMenu.Clear(strip.Items);
            Assert.Empty(strip.Items);
        });
    }

    [Fact]
    public void ClickingOpensInTheSameTab_MiddleClickInANewTab()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            FavoritesStore store = CreateStore(dir);
            store.AddLink(store.Root, Loc(WindowsDir), "Windows");
            var opened = new List<(ShellLocation Location, bool NewTab)>();
            var menu = new FavoritesMenu(store, () => null, (location, newTab) => opened.Add((location, newTab)), () => { });
            using var strip = new ContextMenuStrip();
            menu.AddEntries(strip.Items);
            var item = (ToolStripMenuItem)strip.Items.Cast<ToolStripItem>().Single(i => i.Text == "Windows");

            item.PerformClick();
            RaiseMouseUp(item, MouseButtons.Middle);
            RaiseMouseUp(item, MouseButtons.Left);

            Assert.Equal([(Loc(WindowsDir), false), (Loc(WindowsDir), true)], opened);
            Assert.Equal(WindowsDir, item.ToolTipText, ignoreCase: true);
        });
    }

    [Fact]
    public void ShortcutWhoseTargetIsUnreadable_IsShownDisabled()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            FavoritesStore store = CreateStore(dir);
            File.WriteAllText(Path.Combine(store.Root, "Broken.lnk"), "not a shortcut");
            var menu = new FavoritesMenu(store, () => null, (_, _) => Assert.Fail("must not open"), () => { });
            using var strip = new ContextMenuStrip();
            menu.AddEntries(strip.Items);

            var item = strip.Items.Cast<ToolStripItem>().Single(i => i.Text == "Broken");
            Assert.False(item.Enabled);
        });
    }

    [Fact]
    public void Commands_AddTheCurrentFolderToFavoritesOrQuickLink_AndAskToManage()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            FavoritesStore store = CreateStore(dir);
            ShellLocation? current = Loc(WindowsDir);
            int managed = 0;
            var menu = new FavoritesMenu(store, () => current, (_, _) => { }, () => managed++);
            ToolStripItem[] commands = menu.CreateCommands();
            Assert.Equal(3, commands.Length);

            ((ToolStripMenuItem)commands[0]).PerformClick();
            ((ToolStripMenuItem)commands[1]).PerformClick();
            ((ToolStripMenuItem)commands[2]).PerformClick();

            Assert.Equal(["Windows"], store.List(store.Root).Where(e => !e.IsFolder).Select(e => e.Name));
            Assert.Equal(["Windows"], store.List(store.QuickLinkFolder).Select(e => e.Name));
            Assert.Equal(1, managed);

            current = null;
            ((ToolStripMenuItem)commands[0]).PerformClick();
            Assert.Single(store.List(store.Root), e => !e.IsFolder);
        });
    }
}

public class FavoritesDialogTests(ITestOutputHelper output)
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static ShellLocation Loc(string path) => ShellLocation.Parse(path)!;

    private static (FavoritesStore Store, string A, string B, string Work) CreateSample(TempDirectory dir)
    {
        var store = new FavoritesStore(dir.File("Favorites"));
        store.EnsureCreated();
        string a = store.AddLink(store.Root, Loc(WindowsDir), "A");
        string b = store.AddLink(store.Root, Loc(Path.GetTempPath()), "B");
        string work = store.CreateFolder(store.Root, "Work");
        return (store, a, b, work);
    }

    private static string[] TopLevel(FavoritesDialog dialog) => [.. dialog.Tree.Nodes.Cast<TreeNode>().Select(node => node.Text)];

    private static TreeNode Node(FavoritesDialog dialog, string text) =>
        dialog.Tree.Nodes.Cast<TreeNode>().Concat(dialog.Tree.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>())).First(node => node.Text == text);

    [Fact]
    public void Tree_ShowsFoldersAndShortcutsWithIcons()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var (store, _, _, work) = CreateSample(dir);
            store.AddLink(work, Loc(WindowsDir), "Inside");
            using var dialog = new FavoritesDialog(store);

            Assert.Equal(["Quick-Link", "A", "B", "Work"], TopLevel(dialog));
            Assert.Equal(["Inside"], Node(dialog, "Work").Nodes.Cast<TreeNode>().Select(n => n.Text));
            Assert.All(dialog.Tree.Nodes.Cast<TreeNode>(), node => Assert.True(node.ImageIndex >= 0, node.Text));
        });
    }

    [Fact]
    public void CreateRenameDelete_ChangeTheStoreAndKeepTheSelection()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var (store, a, _, _) = CreateSample(dir);
            using var dialog = new FavoritesDialog(store);
            dialog.Tree.SelectedNode = Node(dialog, "A");

            string renamed = dialog.RenameSelected("Windows dir");
            Assert.Equal(Path.Combine(store.Root, "Windows dir.lnk"), renamed);
            Assert.Equal(renamed, dialog.SelectedPath);
            Assert.False(File.Exists(a));

            string folder = dialog.CreateFolder("Tools");
            Assert.Equal(folder, dialog.SelectedPath);
            Assert.Equal(Path.GetDirectoryName(folder), Path.GetDirectoryName(Path.Combine(store.Root, "x")));

            dialog.Tree.SelectedNode = Node(dialog, "B");
            dialog.DeleteSelected();
            Assert.DoesNotContain("B", TopLevel(dialog));
            Assert.NotNull(dialog.SelectedPath);
        });
    }

    [Fact]
    public void NewFolderGoesIntoTheSelectedFolderOrBesideTheSelectedShortcut()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var (store, _, _, work) = CreateSample(dir);
            using var dialog = new FavoritesDialog(store);

            dialog.Tree.SelectedNode = Node(dialog, "Work");
            string inside = dialog.CreateFolder("Sub");
            Assert.Equal(work, Path.GetDirectoryName(inside));

            dialog.Tree.SelectedNode = Node(dialog, "A");
            string beside = dialog.CreateFolder("Next");
            Assert.Equal(store.Root, Path.GetDirectoryName(beside));
        });
    }

    [Fact]
    public void MoveSelected_ReordersAmongSiblings()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var (store, _, _, _) = CreateSample(dir);
            using var dialog = new FavoritesDialog(store);

            dialog.Tree.SelectedNode = Node(dialog, "B");
            dialog.MoveSelected(-1);
            Assert.Equal(["Quick-Link", "B", "A", "Work"], TopLevel(dialog));
            Assert.Equal("B", dialog.Tree.SelectedNode!.Text);

            dialog.MoveSelected(-1);
            dialog.MoveSelected(-1);
            Assert.Equal(["B", "Quick-Link", "A", "Work"], TopLevel(dialog));

            dialog.Tree.SelectedNode = Node(dialog, "Work");
            dialog.MoveSelected(1);
            Assert.Equal(["B", "Quick-Link", "A", "Work"], TopLevel(dialog));
        });
    }

    [Fact]
    public void DropPosition_MiddleOfAFolderMovesInside_EdgesPlaceBeforeOrAfter()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var (store, _, _, work) = CreateSample(dir);
            using var dialog = new FavoritesDialog(store);
            TreeNode a = Node(dialog, "A");
            TreeNode workNode = Node(dialog, "Work");

            Assert.Equal((work, -1), dialog.DropPosition(workNode, 0.5f));
            Assert.Equal((store.Root, a.Index), dialog.DropPosition(a, 0.2f));
            Assert.Equal((store.Root, a.Index + 1), dialog.DropPosition(a, 0.8f));
            Assert.Equal((store.Root, a.Index), dialog.DropPosition(a, 0.5f - 0.3f));
            Assert.Equal((store.Root, workNode.Index + 1), dialog.DropPosition(workNode, 0.9f));
            Assert.Equal((store.Root, -1), dialog.DropPosition(null, 1f));
        });
    }

    [Fact]
    public void Drop_MovesIntoFoldersAndReorders_AndRefusesIllegalMoves()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var (store, _, _, work) = CreateSample(dir);
            using var dialog = new FavoritesDialog(store);

            // A onto the middle of Work: moves into it.
            dialog.Drop(Node(dialog, "A"), Node(dialog, "Work"), 0.5f);
            Assert.Equal(["Quick-Link", "B", "Work"], TopLevel(dialog));
            Assert.Equal(["A"], store.List(work).Select(e => e.Name));

            // B below Work (lower half): reordered to the end, with the removed slot accounted for.
            dialog.Drop(Node(dialog, "B"), Node(dialog, "Work"), 0.9f);
            Assert.Equal(["Quick-Link", "Work", "B"], TopLevel(dialog));

            // B above Quick-Link.
            dialog.Drop(Node(dialog, "B"), Node(dialog, "Quick-Link"), 0.1f);
            Assert.Equal(["B", "Quick-Link", "Work"], TopLevel(dialog));

            // A folder into itself, and the protected Quick-Link folder: nothing changes.
            dialog.Drop(Node(dialog, "Work"), Node(dialog, "Work"), 0.5f);
            dialog.Drop(Node(dialog, "Quick-Link"), Node(dialog, "Work"), 0.5f);
            Assert.Equal(["B", "Quick-Link", "Work"], TopLevel(dialog));

            // Dropped on empty space: end of the root.
            dialog.Drop(Node(dialog, "A"), null, 1f);
            Assert.Equal(["B", "Quick-Link", "Work", "A"], TopLevel(dialog));
        });
    }

    [Fact]
    public void QuickLinkFolder_CannotBeRenamedDeletedOrMoved()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var (store, _, _, _) = CreateSample(dir);
            using var dialog = new FavoritesDialog(store);
            dialog.Tree.SelectedNode = Node(dialog, "Quick-Link");

            Assert.Throws<ArgumentException>(() => dialog.RenameSelected("Fast"));
            Assert.Throws<ArgumentException>(() => dialog.DeleteSelected());
            Assert.Throws<ArgumentException>(() => store.Move(store.QuickLinkFolder, store.Root, 2));
            Assert.True(Directory.Exists(store.QuickLinkFolder));
        });
    }

    [Fact]
    public void ShortcutIcon_CanBeChangedAndReset_AndShowsInTheTree()
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var (store, a, _, _) = CreateSample(dir);
            using var dialog = new FavoritesDialog(store);
            dialog.Tree.SelectedNode = Node(dialog, "A");

            dialog.SetSelectedIcon(@"%SystemRoot%\system32\shell32.dll", 4);
            Assert.Equal(4, store.Read(a)!.IconIndex);
            Assert.Equal(a, dialog.SelectedPath);

            dialog.SetSelectedIcon(null, 0);
            Assert.Null(store.Read(a)!.IconPath);

            // Folders have no icon of their own to change.
            dialog.Tree.SelectedNode = Node(dialog, "Work");
            dialog.SetSelectedIcon(@"%SystemRoot%\system32\shell32.dll", 4);
        });
    }

    [Fact]
    public void Dialog_RendersTheFavoritesTree()
    {
        using var dir = new TempDirectory();
        var (store, _, _, work) = CreateSample(dir);
        store.AddLink(work, Loc(Path.Combine(WindowsDir, "System32")), "System32");
        store.AddLink(store.QuickLinkFolder, Loc("shell:MyComputerFolder"), "내 PC");
        store.SetIcon(Path.Combine(store.Root, "B.lnk"), @"%SystemRoot%\system32\shell32.dll", 43);

        Run(() => new FavoritesDialog(store), async dialog =>
        {
            await Task.Delay(500);
            output.WriteLine($"Screenshot: {SaveScreenshot(dialog, "favorites-dialog.png")}");
        });
    }
}

/// <summary>Favorites through the real main window and a pane's star button. Shows a window briefly.</summary>
public class FavoritesIntegrationTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System32Dir = Path.Combine(WindowsDir, "System32");

    private static ToolStripMenuItem FavoritesMenuItem(MainForm form) =>
        form.MainMenuStrip!.Items.Cast<ToolStripItem>().OfType<ToolStripMenuItem>().Single(item => item.Text == "즐겨찾기(&A)");

    private static ToolStripDropDownButton StarButton(PaneControl pane)
    {
        IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
        return All(pane).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripDropDownButton>()).Single(button => button.ToolTipText == "즐겨찾기");
    }

    [Fact]
    public void AddFromTheMenu_ThenOpenFromTheMenu_NavigatesTheActivePane()
    {
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());
        Run(() => new MainForm(Workspace_Start(), null, null, profile), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount > 0, "pane 1 listed");
            ToolStripMenuItem menu = FavoritesMenuItem(form);

            menu.ShowDropDown();
            ((ToolStripMenuItem)menu.DropDownItems[0]).PerformClick();
            menu.HideDropDown();
            string link = Path.Combine(profile.Paths.FavoritesDirectory, "Windows.lnk");
            Assert.True(File.Exists(link), "the current folder became a shortcut file");

            form.Panes[0].Browser.Navigate(System32Dir);
            await WaitUntil(() => form.Panes[0].Browser.CurrentLocation?.FileSystemPath?.EndsWith("System32", StringComparison.OrdinalIgnoreCase) == true, "navigated away");

            menu.ShowDropDown();
            ToolStripMenuItem item = menu.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Windows");
            item.PerformClick();
            menu.HideDropDown();

            await WaitUntil(() => string.Equals(form.Panes[0].Browser.CurrentLocation?.FileSystemPath, WindowsDir, StringComparison.OrdinalIgnoreCase),
                "the favorite navigated the active pane back to Windows");
            Assert.Single(form.Panes[0].Tabs);

            // A folder of favorites becomes a sub-menu that is filled when it opens, and its entries navigate too.
            var store = new FavoritesStore(profile.Paths.FavoritesDirectory);
            string work = store.CreateFolder(store.Root, "Work");
            store.AddLink(work, ShellLocation.Parse(System32Dir)!, "Inner");
            menu.ShowDropDown();
            ToolStripMenuItem workItem = menu.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Work");
            Assert.Equal(["…"], workItem.DropDownItems.OfType<ToolStripItem>().Select(i => i.Text));
            workItem.ShowDropDown();
            ToolStripMenuItem inner = workItem.DropDownItems.OfType<ToolStripMenuItem>().Single();
            Assert.Equal("Inner", inner.Text);
            inner.PerformClick();
            menu.HideDropDown();

            await WaitUntil(() => form.Panes[0].Browser.CurrentLocation?.FileSystemPath?.EndsWith("System32", StringComparison.OrdinalIgnoreCase) == true,
                "the entry inside the folder navigated too");
        });
    }

    [Fact]
    public void StarButtonInAPane_AddsToQuickLinkAndOpensInANewTab()
    {
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());
        Run(() => new MainForm(Workspace_Start(), null, null, profile), async form =>
        {
            await WaitUntil(() => form.Panes[1].Browser.ItemCount > 0, "pane 2 listed");
            PaneControl pane = form.Panes[1];
            ToolStripDropDownButton star = StarButton(pane);

            star.ShowDropDown();
            ((ToolStripMenuItem)star.DropDownItems[1]).PerformClick();
            star.HideDropDown();
            string quickLink = Path.Combine(profile.Paths.FavoritesDirectory, "Quick-Link");
            Assert.Single(Directory.GetFiles(quickLink, "*.lnk"));

            // Open it in a new tab of pane 1 through the main menu path (Ctrl+click behaviour is the newTab flag).
            ShellLocation target = pane.Browser.CurrentLocation!;
            form.Panes[0].OpenFavorite(target, newTab: true);
            Assert.Equal(2, form.Panes[0].Tabs.Count);
            Assert.Equal(target, form.Panes[0].SelectedTab.Location);

            star.ShowDropDown();
            string[] texts = [.. star.DropDownItems.OfType<ToolStripItem>().Select(i => i.Text ?? string.Empty)];
            star.HideDropDown();
            Assert.Contains("퀵링크", texts);
        });
    }

    [Fact]
    public void WithoutAProfile_TheFavoritesMenuIsDisabled()
    {
        Run(() => new MainForm(Workspace_Start()), form =>
        {
            ToolStripMenuItem menu = FavoritesMenuItem(form);
            Assert.All(menu.DropDownItems.OfType<ToolStripMenuItem>(), item => Assert.False(item.Enabled));
            Assert.Null(form.Panes[0].Favorites);
            return Task.CompletedTask;
        });
    }

    private static ShellLocation Workspace_Start() => ShellLocation.Parse(WindowsDir)!;
}
