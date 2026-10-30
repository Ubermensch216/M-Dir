using MDir.Features;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class FavoritesExtrasTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System32Dir = Path.Combine(WindowsDir, "System32");
    private static readonly string TempFolder = Path.GetTempPath().TrimEnd('\\');

    /// <summary>A "Q-Dir style" favorites folder: shortcuts at the top, a Quick-Link folder and a subfolder with more.</summary>
    private static string CreateSource(TempDirectory temp)
    {
        string source = temp.File("Favoriten");
        var maker = new FavoritesStore(source);
        maker.EnsureCreated();
        maker.AddLink(source, ShellLocation.Parse(WindowsDir)!);
        maker.AddLink(maker.QuickLinkFolder, ShellLocation.Parse(System32Dir)!);
        string work = maker.CreateFolder(source, "Work");
        maker.AddLink(work, ShellLocation.Parse(TempFolder)!);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "not a shortcut");
        return source;
    }

    [Fact]
    public void ImportFrom_CopiesShortcutsAndFolders_MergesQuickLink_AndSkipsOtherFiles()
    {
        using var temp = new TempDirectory();
        string source = CreateSource(temp);
        var store = new FavoritesStore(temp.File("mine"));
        store.EnsureCreated();
        store.AddLink(store.QuickLinkFolder, ShellLocation.Parse(@"C:\")!);

        int copied = store.ImportFrom(source, store.Root);

        Assert.Equal(3, copied);
        Assert.Equal(["Windows"], store.List(store.Root).Where(entry => !entry.IsFolder).Select(entry => entry.Name));
        Assert.Equal(2, store.List(store.QuickLinkFolder).Count);
        FavoriteEntry work = store.List(store.Root).Single(entry => entry.IsFolder && entry.Name == "Work");
        Assert.Single(store.List(work.Path));
        Assert.False(File.Exists(Path.Combine(store.Root, "readme.txt")));
        Assert.Equal(WindowsDir, store.Read(store.List(store.Root).Single(entry => !entry.IsFolder).Path)!.Target!.FileSystemPath, ignoreCase: true);
    }

    [Fact]
    public void ImportFrom_Twice_DoesNotOverwriteAndKeepsBothCopies()
    {
        using var temp = new TempDirectory();
        string source = CreateSource(temp);
        var store = new FavoritesStore(temp.File("mine"));
        store.EnsureCreated();

        store.ImportFrom(source, store.Root);
        store.ImportFrom(source, store.Root);

        Assert.Equal(2, store.List(store.Root).Count(entry => !entry.IsFolder));
        Assert.Equal(2, store.List(store.Root).Count(entry => entry.IsFolder && entry.Name.StartsWith("Work")));
    }

    [Fact]
    public void ImportFrom_MissingOrSameFolder_CopiesNothing()
    {
        using var temp = new TempDirectory();
        var store = new FavoritesStore(temp.File("mine"));
        store.EnsureCreated();

        Assert.Equal(0, store.ImportFrom(temp.File("nothing"), store.Root));
        Assert.Equal(0, store.ImportFrom(store.Root, store.Root));
        Assert.Throws<ArgumentException>(() => store.ImportFrom(temp.Path, temp.File("outside")));
    }

    [Fact]
    public void OpenAllInTabs_OpensEveryShortcutOfAFolderInANewTab()
    {
        using var temp = new TempDirectory();
        var store = new FavoritesStore(temp.File("mine"));
        store.EnsureCreated();
        string group = store.CreateFolder(store.Root, "Group");
        store.AddLink(group, ShellLocation.Parse(WindowsDir)!);
        store.AddLink(group, ShellLocation.Parse(System32Dir)!);
        store.CreateFolder(group, "Nested");
        var opened = new List<(string?, bool)>();
        var menu = new FavoritesMenu(store, () => null, (location, newTab) => opened.Add((location.FileSystemPath, newTab)), () => { });

        RunSta(() => Assert.Equal(2, menu.OpenAllInTabs(group)));

        Assert.Equal(2, opened.Count);
        Assert.All(opened, entry => Assert.True(entry.Item2));
        Assert.Contains(opened, entry => string.Equals(entry.Item1, System32Dir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportCommand_UsesThePickedFolder_AndTellsTheResult()
    {
        using var temp = new TempDirectory();
        string source = CreateSource(temp);
        var profile = new AppProfile(AppPaths.Resolve(appDirectory: temp.Path, roamingAppData: temp.File("data")), new AppSettings());
        var notices = new List<string>();
        string? picked = null;

        Run(() => new MainForm(ShellLocation.Parse(WindowsDir), null, null, profile)
        {
            Size = new Size(1200, 800),
            FavoritesFolderPicker = () => picked,
            ShowNotice = notices.Add,
        }, form =>
        {
            Assert.True(form.RunCommand("fav.importQdir"));
            Assert.Empty(notices);

            picked = source;
            Assert.True(form.RunCommand("fav.importQdir"));

            Assert.Single(notices);
            Assert.Contains("3개를 가져왔습니다", notices[0]);
            Assert.True(Directory.Exists(Path.Combine(profile.Paths.FavoritesDirectory, "Work")));

            picked = temp.File("empty");
            Directory.CreateDirectory(picked);
            Assert.True(form.RunCommand("fav.importQdir"));
            Assert.Contains("가져올 바로 가기", notices[1]);
            return Task.CompletedTask;
        });
    }
}
