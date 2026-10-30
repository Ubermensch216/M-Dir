using MDir.Features;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class FolderViewStateModelTests
{
    private static readonly ViewKey Name = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10);
    private static readonly ViewKey Size = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 12);

    private static FolderViewState Sample() => new([new ColumnState(Name, 250), new ColumnState(Size, 90)], [new SortState(Size, -1), new SortState(Name, 1)]);

    [Fact]
    public void Serialize_ThenParse_GivesTheSameState()
    {
        FolderViewState state = Sample();

        FolderViewState? parsed = FolderViewState.Parse(state.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal(state, parsed);
        Assert.Equal(state.GetHashCode(), parsed.GetHashCode());
        Assert.Equal(2, parsed.Columns.Count);
        Assert.Equal(-1, parsed.Sort[0].Direction);
    }

    [Fact]
    public void Equals_ComparesOrderWidthAndDirection()
    {
        Assert.NotEqual(Sample(), new FolderViewState([new ColumnState(Size, 90), new ColumnState(Name, 250)], Sample().Sort));
        Assert.NotEqual(Sample(), new FolderViewState([new ColumnState(Name, 251), new ColumnState(Size, 90)], Sample().Sort));
        Assert.NotEqual(Sample(), new FolderViewState(Sample().Columns, [new SortState(Size, 1), new SortState(Name, 1)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("c=|s=")]
    [InlineData("c=not-a-guid:1@10|s=")]
    [InlineData("c=b725f130-47ef-101a-a5f1-02608c9eebac:10@-5|s=")]
    [InlineData("c=b725f130-47ef-101a-a5f1-02608c9eebac:10@9999999|s=")]
    [InlineData("c=|s=b725f130-47ef-101a-a5f1-02608c9eebac:10@0")]
    [InlineData("c=b725f130-47ef-101a-a5f1-02608c9eebac:x@10|s=")]
    public void Parse_RejectsDamagedText(string? text) => Assert.Null(FolderViewState.Parse(text));

    [Fact]
    public void Parse_AllowsColumnsWithoutSortAndTheOtherWayRound()
    {
        Assert.NotNull(FolderViewState.Parse("c=b725f130-47ef-101a-a5f1-02608c9eebac:10@100|s="));
        Assert.NotNull(FolderViewState.Parse("c=|s=b725f130-47ef-101a-a5f1-02608c9eebac:10@1"));
    }

    [Fact]
    public void Workspace_KeepsTheViewStatesPerPane()
    {
        Guid kind = new("F3364BA0-65B9-11CE-A9BA-00AA004AE837");
        Workspace basis = Workspace.CreateDefault(ShellLocation.Desktop);
        PaneState first = basis.Panes[0] with { ViewStates = new Dictionary<Guid, FolderViewState> { [kind] = Sample() } };
        var workspace = new Workspace { Panes = [first, .. basis.Panes.Skip(1)] };

        Workspace? loaded = Workspace.FromIni(IniFile.Parse(workspace.ToIni().ToText()));

        Assert.NotNull(loaded);
        Assert.Equal(Sample(), loaded.Panes[0].ViewStates![kind]);
        Assert.Null(loaded.Panes[1].ViewStates);
    }

    [Fact]
    public void Workspace_IgnoresDamagedViewStates()
    {
        var ini = IniFile.Parse("[Workspace]\nLayout=1\n[Pane1]\nViews=2\nView1=nope\nView1.State=c=|s=\nView2=F3364BA0-65B9-11CE-A9BA-00AA004AE837\nView2.State=broken\n");

        Workspace? loaded = Workspace.FromIni(ini);

        Assert.NotNull(loaded);
        Assert.Null(loaded.Panes[0].ViewStates);
    }
}

/// <summary>Columns and sort order through the real shell view.</summary>
public class ViewStateIntegrationTests
{
    private static readonly ViewKey SizeKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 12);

    private static MainForm CreateForm(string folder)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(folder)!);
        PaneLayout layout = PaneLayout.FromId(1)!;
        return new MainForm(null, new Workspace { Layout = layout, Ratios = layout.DefaultRatios, Panes = basis.Panes }, null, null)
        {
            Size = new Size(1300, 800),
            SilentFileOperations = true,
        };
    }

    private static TempDirectory CreateRoot()
    {
        var root = new TempDirectory();
        foreach (string folder in new[] { "a", "b", "c" })
        {
            Directory.CreateDirectory(root.File(folder));
            File.WriteAllText(root.File(folder + @"\small.txt"), "x");
            File.WriteAllText(root.File(folder + @"\big.txt"), new string('x', 5000));
        }

        return root;
    }

    private static string ListedNames(MainForm form) =>
        string.Join(",", form.ActivePane.Browser.GetAllItemPaths().Select(Path.GetFileName));

    [Fact]
    public void ChangedSort_IsAppliedToTheNextFolderOfTheSameKind_AndSurvivesInTheWorkspace()
    {
        using TempDirectory root = CreateRoot();
        Workspace? saved = null;
        Run(() => CreateForm(root.File("a")), async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount == 2, "a listed");
            FolderViewState before = browser.CaptureCurrentViewState()!;
            Assert.NotEmpty(before.Columns);
            Assert.Equal("big.txt,small.txt", ListedNames(form));

            browser.ApplyViewState(new FolderViewState([], [new SortState(SizeKey, 1)]));
            await WaitUntil(() => ListedNames(form) == "small.txt,big.txt", "sorted by size ascending");

            browser.Navigate(root.File("b"));
            await WaitUntil(() => browser.CurrentLocation?.FileSystemPath == root.File("b") && browser.ItemCount == 2, "b listed");
            await WaitUntil(() => ListedNames(form) == "small.txt,big.txt", "b shows the remembered sort");

            browser.Navigate(root.File("c"));
            await WaitUntil(() => browser.CurrentLocation?.FileSystemPath == root.File("c") && browser.ItemCount == 2, "c listed");
            await WaitUntil(() => ListedNames(form) == "small.txt,big.txt", "c shows the remembered sort");

            saved = form.CaptureWorkspace();
        });

        Assert.NotNull(saved);
        string text = saved.ToIni().ToText();
        Workspace restored = Workspace.FromIni(IniFile.Parse(text))!;
        Assert.NotNull(restored.Panes[0].ViewStates);

        Run(() => new MainForm(null, restored, null, null) { Size = new Size(1300, 800) }, async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount == 2, "c listed again");
            await WaitUntil(() => ListedNames(form) == "small.txt,big.txt", "sort restored after a restart");
        });
    }

    [Fact]
    public void AFolderLeftAsTheShellOpenedIt_StoresNothing()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root.File("a")), async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount == 2, "a listed");

            browser.Navigate(root.File("b"));
            await WaitUntil(() => browser.CurrentLocation?.FileSystemPath == root.File("b") && browser.ItemCount == 2, "b listed");

            Assert.Empty(browser.CaptureViewStates());
        });
    }
}
