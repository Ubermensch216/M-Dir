using MDir.Features;
using MDir.Shell;

namespace MDir.Tests;

public class FavoritesStoreTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static ShellLocation Loc(string path) => ShellLocation.Parse(path) ?? throw new InvalidOperationException(path);

    private static FavoritesStore CreateStore(TempDirectory dir)
    {
        var store = new FavoritesStore(dir.File("Favorites"));
        store.EnsureCreated();
        return store;
    }

    private static string[] Names(FavoritesStore store, string folder) => [.. store.List(folder).Select(entry => entry.Name)];

    [Fact]
    public void LinkRoundTrip_PreservesRealAndVirtualFolders()
    {
        using var dir = new TempDirectory();
        FavoritesStore store = CreateStore(dir);
        ShellLocation windows = Loc(WindowsDir);
        ShellLocation thisPc = Loc("shell:MyComputerFolder");

        string windowsLink = store.AddLink(store.Root, windows);
        string pcLink = store.AddLink(store.Root, thisPc, "내 컴퓨터");

        Assert.EndsWith(".lnk", windowsLink);
        Assert.Equal(windows, store.Read(windowsLink)!.Target);
        Assert.Equal(thisPc, store.Read(pcLink)!.Target);
        Assert.Equal("내 컴퓨터", Path.GetFileNameWithoutExtension(pcLink));
        Assert.Equal(WindowsDir, store.Read(windowsLink)!.Target!.FileSystemPath, ignoreCase: true);
    }

    [Fact]
    public void AddLink_DoesNotDuplicateTheSameTargetAndNumbersSameNames()
    {
        using var dir = new TempDirectory();
        FavoritesStore store = CreateStore(dir);

        string first = store.AddLink(store.Root, Loc(WindowsDir));
        string again = store.AddLink(store.Root, Loc(WindowsDir));
        string sameNameOtherTarget = store.AddLink(store.Root, Loc(Path.Combine(WindowsDir, "System32")), "Windows");

        Assert.Equal(first, again);
        Assert.Single(store.List(store.Root).Where(entry => !entry.IsFolder).Take(1));
        Assert.Equal("Windows (2)", Path.GetFileNameWithoutExtension(sameNameOtherTarget));
    }

    [Fact]
    public void QuickLinkFolderComesFirst_ThenFoldersThenLinks()
    {
        using var dir = new TempDirectory();
        var store = new FavoritesStore(dir.File("Favorites"));
        store.EnsureCreated();
        Directory.CreateDirectory(Path.Combine(store.Root, "Aaa"));
        File.Delete(Path.Combine(store.Root, ".order"));
        ShellLinks.Create(Path.Combine(store.Root, "Zed.lnk"), Loc(WindowsDir));
        ShellLinks.Create(Path.Combine(store.Root, "Alpha.lnk"), Loc(WindowsDir));

        Assert.Equal(["Quick-Link", "Aaa", "Alpha", "Zed"], Names(store, store.Root));
    }

    [Fact]
    public void IgnoresFilesThatAreNotShortcuts()
    {
        using var dir = new TempDirectory();
        FavoritesStore store = CreateStore(dir);
        File.WriteAllText(Path.Combine(store.Root, "readme.txt"), "x");
        File.WriteAllText(Path.Combine(store.Root, "desktop.ini"), "x");

        Assert.Equal(["Quick-Link"], Names(store, store.Root));
    }

    [Fact]
    public void Folders_RenameDeleteAndKeepTheirOrder()
    {
        using var dir = new TempDirectory();
        FavoritesStore store = CreateStore(dir);
        string a = store.AddLink(store.Root, Loc(WindowsDir), "A");
        string b = store.AddLink(store.Root, Loc(Path.Combine(WindowsDir, "System32")), "B");
        string folder = store.CreateFolder(store.Root, "Work");
        string inFolder = store.AddLink(folder, Loc(WindowsDir), "Inside");

        Assert.Equal(["Quick-Link", "A", "B", "Work"], Names(store, store.Root));

        string renamed = store.Rename(b, "Bee");
        Assert.Equal(["Quick-Link", "A", "Bee", "Work"], Names(store, store.Root));
        Assert.Equal(Loc(Path.Combine(WindowsDir, "System32")), store.Read(renamed)!.Target);

        string renamedFolder = store.Rename(folder, "Projects");
        Assert.Equal(Path.Combine(renamedFolder, "Inside.lnk"), store.List(renamedFolder).Single().Path);
        Assert.Throws<IOException>(() => store.Rename(a, "Bee"));
        Assert.Equal(a, store.Rename(a, "A"));

        store.Delete(renamedFolder);
        store.Delete(a);
        Assert.Equal(["Quick-Link", "Bee"], Names(store, store.Root));
        Assert.False(File.Exists(inFolder));
    }

    [Fact]
    public void Move_ReordersWithinAFolderAndMovesBetweenFolders()
    {
        using var dir = new TempDirectory();
        FavoritesStore store = CreateStore(dir);
        string a = store.AddLink(store.Root, Loc(WindowsDir), "A");
        string b = store.AddLink(store.Root, Loc(Path.Combine(WindowsDir, "System32")), "B");
        string c = store.AddLink(store.Root, Loc(Path.GetTempPath()), "C");
        string work = store.CreateFolder(store.Root, "Work");

        store.Move(c, store.Root, 0);
        Assert.Equal(["C", "Quick-Link", "A", "B", "Work"], Names(store, store.Root));

        store.Move(c, store.Root, 3);
        Assert.Equal(["Quick-Link", "A", "B", "C", "Work"], Names(store, store.Root));

        string moved = store.Move(a, work);
        Assert.Equal(Path.Combine(work, "A.lnk"), moved);
        Assert.Equal(["Quick-Link", "B", "C", "Work"], Names(store, store.Root));
        Assert.Equal(["A"], Names(store, work));

        // Same name in the target: the moved entry is numbered, not overwritten.
        string clash = store.AddLink(store.Root, Loc(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "A");
        Assert.Equal(Path.Combine(work, "A (2).lnk"), store.Move(clash, work, 0));
        Assert.Equal(["A (2)", "A"], Names(store, work));
    }

    [Fact]
    public void Move_RefusesToPutAFolderInsideItself()
    {
        using var dir = new TempDirectory();
        FavoritesStore store = CreateStore(dir);
        string outer = store.CreateFolder(store.Root, "Outer");
        string inner = store.CreateFolder(outer, "Inner");

        Assert.Throws<ArgumentException>(() => store.Move(outer, outer));
        Assert.Throws<ArgumentException>(() => store.Move(outer, inner));
        Assert.Equal(["Inner"], Names(store, outer));
    }

    [Fact]
    public void OperationsOutsideTheFavoritesFolder_AreRejected()
    {
        using var dir = new TempDirectory();
        FavoritesStore store = CreateStore(dir);
        string outside = Path.Combine(dir.Path, "elsewhere.lnk");
        File.WriteAllText(outside, "x");

        Assert.Throws<ArgumentException>(() => store.Delete(outside));
        Assert.Throws<ArgumentException>(() => store.Rename(outside, "x"));
        Assert.Throws<ArgumentException>(() => store.Move(outside, store.Root));
        Assert.Throws<ArgumentException>(() => store.List(dir.Path));
        Assert.Throws<ArgumentException>(() => store.Delete(store.Root));
        Assert.Throws<ArgumentException>(() => store.Delete(Path.Combine(store.Root, "..", "elsewhere.lnk")));
        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData("a/b:c*d", "a_b_c_d")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("dots...", "dots")]
    [InlineData("", "Favorite")]
    [InlineData("???", "___")]
    public void SanitizeName_MakesAValidFileName(string name, string expected) => Assert.Equal(expected, FavoritesStore.SanitizeName(name));

    [Fact]
    public void SetIcon_IsStoredInTheShortcutAndCanBeCleared()
    {
        using var dir = new TempDirectory();
        FavoritesStore store = CreateStore(dir);
        string link = store.AddLink(store.Root, Loc(WindowsDir));
        Assert.Null(store.Read(link)!.IconPath);

        store.SetIcon(link, @"%SystemRoot%\system32\shell32.dll", 4);

        ShellLinkInfo info = store.Read(link)!;
        Assert.Equal(4, info.IconIndex);
        Assert.Equal(@"%SystemRoot%\system32\shell32.dll", info.IconPath, ignoreCase: true);
        Assert.Equal(Loc(WindowsDir), info.Target);
        using Bitmap? icon = ShellLinks.LoadIcon(info.IconPath!, info.IconIndex);
        Assert.NotNull(icon);

        store.SetIcon(link, null, 0);
        Assert.Null(store.Read(link)!.IconPath);
    }

    [Fact]
    public void ReadingAFileThatIsNotAShortcut_GivesNull()
    {
        using var dir = new TempDirectory();
        string fake = dir.File("fake.lnk");
        File.WriteAllText(fake, "not a shortcut");

        Assert.Null(ShellLinks.Read(fake));
        Assert.Null(ShellLinks.Read(dir.File("missing.lnk")));
    }
}
