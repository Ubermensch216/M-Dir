using MDir.Layout;
using MDir.Settings;
using MDir.Shell;

namespace MDir.Tests;

public class WorkspaceTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static ShellLocation Loc(string path) => ShellLocation.Parse(path) ?? throw new InvalidOperationException(path);

    [Fact]
    public void SaveAndLoad_RoundTripsEverything()
    {
        using var dir = new TempDirectory();
        var workspace = new Workspace
        {
            Layout = PaneLayout.FromId(6)!,
            Ratios = [3000, 7000],
            ActivePane = 2,
            MultilineTabs = true,
            Window = new WindowPlacement(-8, 20, 900, 700, Maximized: true),
            Panes =
            [
                new PaneState([new TabState(Loc(WindowsDir), FolderViewMode.Details, 16), new TabState(Loc(Path.Combine(WindowsDir, "System32")), FolderViewMode.Icon, 96)], 1),
                new PaneState([new TabState(Loc("shell:MyComputerFolder"), FolderViewMode.Tile, 48)], 0),
                new PaneState([new TabState(Loc(Path.GetTempPath()), FolderViewMode.List, 16)], 0),
                new PaneState([new TabState(Loc(WindowsDir), FolderViewMode.Content, 32)], 0),
            ],
        };

        workspace.Save(dir.File("w.mdr"));
        Workspace loaded = Workspace.Load(dir.File("w.mdr"))!;

        Assert.Equal(6, loaded.Layout.Id);
        Assert.Equal([3000, 7000], loaded.Ratios);
        Assert.Equal(2, loaded.ActivePane);
        Assert.True(loaded.MultilineTabs);
        Assert.Equal(workspace.Window, loaded.Window);
        Assert.Equal(4, loaded.Panes.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(workspace.Panes[i].SelectedTab, loaded.Panes[i].SelectedTab);
            Assert.Equal(workspace.Panes[i].Tabs, loaded.Panes[i].Tabs);
        }
    }

    [Fact]
    public void SavedFile_UsesDocumentedKeys()
    {
        Workspace workspace = Workspace.CreateDefault(Loc(WindowsDir));

        string text = workspace.ToIni().ToText();

        Assert.Contains("[Workspace]\r\nVersion=1\r\nLayout=10\r\nRatios=5000,5000,5000\r\n", text);
        Assert.Contains("[Pane4]\r\nSelected=0\r\nTree=0\r\nTreeSplit=2500\r\nFilterBar=0\r\nTabs=1\r\nTab1=%", text);
        Assert.Contains("Tab1.ViewMode=4", text);
        Assert.Contains("TreeMode=0\r\nSharedTree=0\r\nSharedTreeSplit=2500\r\n", text);
    }

    [Fact]
    public void TreeState_RoundTripsAndOutOfRangeSplitsAreClamped()
    {
        using var dir = new TempDirectory();
        Workspace basis = Workspace.CreateDefault(Loc(WindowsDir));
        var workspace = new Workspace
        {
            Panes = [basis.Panes[0] with { TreeVisible = true, TreeSplit = 3300 }, .. basis.Panes.Skip(1)],
            SharedTree = true,
            SharedTreeVisible = true,
            SharedTreeSplit = 1800,
        };

        workspace.Save(dir.File("w.mdr"));
        Workspace loaded = Workspace.Load(dir.File("w.mdr"))!;

        Assert.True(loaded.SharedTree);
        Assert.True(loaded.SharedTreeVisible);
        Assert.Equal(1800, loaded.SharedTreeSplit);
        Assert.True(loaded.Panes[0].TreeVisible);
        Assert.Equal(3300, loaded.Panes[0].TreeSplit);
        Assert.False(loaded.Panes[1].TreeVisible);

        Workspace clamped = Workspace.FromIni(IniFile.Parse("[Workspace]\nSharedTreeSplit=99999\n[Pane1]\nTreeSplit=-5\nTree=1\n"))!;
        Assert.Equal(9500, clamped.SharedTreeSplit);
        Assert.Equal(500, clamped.Panes[0].TreeSplit);
        Assert.True(clamped.Panes[0].TreeVisible);
    }

    [Fact]
    public void CreateDefault_HasFourPanesAtCommonFolders()
    {
        Workspace workspace = Workspace.CreateDefault(Loc(WindowsDir));

        Assert.Equal(PaneLayout.Default, workspace.Layout);
        Assert.Equal(4, workspace.Panes.Count);
        Assert.Equal(WindowsDir, workspace.Panes[0].Tabs[0].Location.FileSystemPath, ignoreCase: true);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), workspace.Panes[1].Tabs[0].Location.FileSystemPath, ignoreCase: true);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), workspace.Panes[3].Tabs[0].Location.FileSystemPath, ignoreCase: true);
    }

    [Fact]
    public void FromIni_ToleratesDamagedValues()
    {
        var ini = IniFile.Parse("""
            [Workspace]
            Layout=999
            Ratios=abc
            Active=9
            Window=1;2;3;4
            [Pane1]
            Tabs=3
            Selected=5
            Tab1=%00%00
            Tab1.ViewMode=77
            Tab1.IconSize=100000
            Tab2=garbage
            Tab3=%05%00%01
            """);

        Workspace workspace = Workspace.FromIni(ini)!;

        Assert.Equal(PaneLayout.Default, workspace.Layout);
        Assert.Equal(PaneLayout.Default.DefaultRatios, workspace.Ratios);
        Assert.Equal(3, workspace.ActivePane);
        Assert.Null(workspace.Window);
        TabState tab = Assert.Single(workspace.Panes[0].Tabs);
        Assert.True(tab.Location.IsDesktop);
        Assert.Equal(FolderViewMode.Details, tab.ViewMode);
        Assert.Equal(256, tab.IconSize);
        Assert.Equal(0, workspace.Panes[0].SelectedTab);

        // Panes with nothing usable still get one tab so the window never starts empty.
        Assert.All(workspace.Panes.Skip(1), pane => Assert.Single(pane.Tabs));
    }

    [Fact]
    public void FromIni_RatiosOfTheWrongCountFallBackToDefaults()
    {
        var ini = IniFile.Parse("[Workspace]\nLayout=2\nRatios=1000,2000\n");

        Assert.Equal([5000], Workspace.FromIni(ini)!.Ratios);
    }

    [Fact]
    public void Load_ReturnsNullForMissingOrForeignFiles()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("other.mdr"), "[Something]\nelse=1\n");

        Assert.Null(Workspace.Load(dir.File("absent.mdr")));
        Assert.Null(Workspace.Load(dir.File("other.mdr")));
    }

    [Theory]
    [InlineData("10;20;800;600|sw=1", 10, 20, 800, 600, false)]
    [InlineData("-1500;40;1200;900|sw=3", -1500, 40, 1200, 900, true)]
    [InlineData("0;0;800;600", 0, 0, 800, 600, false)]
    public void WindowPlacement_ParsesQDirNotation(string text, int x, int y, int w, int h, bool maximized)
    {
        WindowPlacement placement = WindowPlacement.TryParse(text)!;

        Assert.Equal(new WindowPlacement(x, y, w, h, maximized), placement);
        Assert.Equal(WindowPlacement.TryParse(placement.ToString()), placement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1;2;3")]
    [InlineData("a;b;c;d")]
    [InlineData("0;0;10;10|sw=1")]
    public void WindowPlacement_RejectsBadValues(string? text) => Assert.Null(WindowPlacement.TryParse(text));

    [Fact]
    public void TryFromIdList_RequiresATerminatedWellFormedList()
    {
        ShellLocation windows = Loc(WindowsDir);
        byte[] good = windows.IdList.ToArray();

        Assert.NotNull(ShellLocation.TryFromIdList(good));
        Assert.True(ShellLocation.TryFromIdList([0, 0])!.IsDesktop);
        Assert.Null(ShellLocation.TryFromIdList([]));
        Assert.Null(ShellLocation.TryFromIdList([0]));
        Assert.Null(ShellLocation.TryFromIdList(good[..^2]));
        Assert.Null(ShellLocation.TryFromIdList([.. good, 0xFF]));
        Assert.Null(ShellLocation.TryFromIdList([1, 0, 0, 0]));
        Assert.Null(ShellLocation.TryFromIdList([0xFF, 0x7F, 0, 0]));
    }

    [Fact]
    public void ExistingOrParent_WalksUpPastDeletedFolders()
    {
        using var dir = new TempDirectory();
        string child = Path.Combine(dir.Path, "a", "b");
        Directory.CreateDirectory(child);
        ShellLocation location = Loc(child);
        Assert.Equal(location, location.ExistingOrParent());

        Directory.Delete(Path.Combine(dir.Path, "a"), recursive: true);

        Assert.Equal(dir.Path, location.ExistingOrParent().FileSystemPath, ignoreCase: true);
        Assert.Equal(Loc("shell:MyComputerFolder"), Loc("shell:MyComputerFolder").ExistingOrParent());
    }
}
