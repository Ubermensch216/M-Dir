using System.Drawing.Printing;
using System.Text;
using MDir.Features;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class FileSearcherTests
{
    private static TempDirectory CreateTree()
    {
        var root = new TempDirectory();
        Directory.CreateDirectory(root.File(@"docs\deep\deeper"));
        Directory.CreateDirectory(root.File("empty"));
        File.WriteAllText(root.File("readme.txt"), "hello world\r\nsecond line with NEEDLE inside\r\n");
        File.WriteAllText(root.File(@"docs\notes.md"), "no match here\r\nneedle at line two");
        File.WriteAllText(root.File(@"docs\deep\report.txt"), "NeEdLe first line");
        File.WriteAllBytes(root.File(@"docs\deep\deeper\data.bin"), [0x00, 0x01, 0x02, 0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65]);
        File.WriteAllText(root.File(@"docs\deep\deeper\한글.txt"), "한글 내용 검색어", new UnicodeEncoding(false, true));
        return root;
    }

    private static List<SearchHit> Collect(SearchOptions options, out SearchSummary summary)
    {
        var hits = new List<SearchHit>();
        summary = FileSearcher.Search(options, new Inline<SearchHit>(hits.Add));
        return hits;
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public void ByName_FindsFilesAndFoldersInAllLevels_WithWildcardsOrAPlainWord()
    {
        using TempDirectory root = CreateTree();

        string[] txt = [.. Collect(new SearchOptions(root.Path, "*.txt"), out _).Select(hit => Path.GetFileName(hit.Path)).Order(StringComparer.Ordinal)];
        Assert.Equal(["readme.txt", "report.txt", "한글.txt"], txt);

        string[] word = [.. Collect(new SearchOptions(root.Path, "deep"), out _).Select(hit => Path.GetFileName(hit.Path)).Order(StringComparer.Ordinal)];
        Assert.Equal(["deep", "deeper"], word);

        string[] both = [.. Collect(new SearchOptions(root.Path, "*.md;readme*"), out _).Select(hit => Path.GetFileName(hit.Path)).Order(StringComparer.Ordinal)];
        Assert.Equal(["notes.md", "readme.txt"], both);
    }

    [Fact]
    public void Options_ControlSubfoldersAndFolderResults()
    {
        using TempDirectory root = CreateTree();

        Assert.Equal(["readme.txt"], Collect(new SearchOptions(root.Path, "*.txt", IncludeSubfolders: false), out _).Select(hit => Path.GetFileName(hit.Path)));

        List<SearchHit> noFolders = Collect(new SearchOptions(root.Path, "*", IncludeFolders: false), out _);
        Assert.DoesNotContain(noFolders, hit => hit.IsFolder);
        Assert.Equal(5, noFolders.Count);

        List<SearchHit> everything = Collect(new SearchOptions(root.Path), out SearchSummary summary);
        Assert.Equal(9, everything.Count);
        Assert.Equal(5, summary.FoldersScanned);
        Assert.False(summary.LimitReached);
    }

    [Fact]
    public void ByContent_FindsTextInAnyEncoding_ReportsTheLine_AndSkipsBinaryFiles()
    {
        using TempDirectory root = CreateTree();

        List<SearchHit> hits = Collect(new SearchOptions(root.Path, ContentText: "needle"), out _);

        Assert.Equal(["notes.md", "readme.txt", "report.txt"], hits.Select(hit => Path.GetFileName(hit.Path)).Order(StringComparer.Ordinal));
        Assert.Equal(2, hits.Single(hit => hit.Path.EndsWith("readme.txt")).Line);
        Assert.Equal(1, hits.Single(hit => hit.Path.EndsWith("report.txt")).Line);
        Assert.DoesNotContain(hits, hit => hit.IsFolder);
        Assert.DoesNotContain(hits, hit => hit.Path.EndsWith("data.bin"));

        Assert.Single(Collect(new SearchOptions(root.Path, ContentText: "검색어"), out _));
    }

    [Fact]
    public void ByContent_MatchCaseNarrowsTheResult_AndNameAndContentCombine()
    {
        using TempDirectory root = CreateTree();

        List<SearchHit> exact = Collect(new SearchOptions(root.Path, ContentText: "NEEDLE", MatchCase: true), out _);
        Assert.Equal(["readme.txt"], exact.Select(hit => Path.GetFileName(hit.Path)));

        List<SearchHit> combined = Collect(new SearchOptions(root.Path, "*.md", "needle"), out _);
        Assert.Equal(["notes.md"], combined.Select(hit => Path.GetFileName(hit.Path)));
    }

    [Fact]
    public void ResultLimit_StopsTheSearchAndSaysSo()
    {
        using TempDirectory root = CreateTree();

        List<SearchHit> hits = Collect(new SearchOptions(root.Path, MaxResults: 3), out SearchSummary summary);

        Assert.Equal(3, hits.Count);
        Assert.True(summary.LimitReached);
    }

    [Fact]
    public async Task Cancel_StopsTheSearch()
    {
        using TempDirectory root = CreateTree();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileSearcher.SearchAsync(new SearchOptions(root.Path), cancel: cancel.Token));
    }

    [Fact]
    public void LinksToFolders_AreNotFollowed()
    {
        using TempDirectory root = CreateTree();
        string link = root.File(@"docs\loop");
        try
        {
            Directory.CreateSymbolicLink(link, root.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating symbolic links needs a privilege; without it there is no cycle to test.
            return;
        }

        List<SearchHit> hits = Collect(new SearchOptions(root.Path, "readme.txt"), out SearchSummary summary);

        Assert.Single(hits);
        Assert.True(summary.FoldersScanned < 20);
    }

    [Fact]
    public void SavedSearch_HoldsTheQueryAndTheScope_EscapedAsXml()
    {
        string xml = WindowsSearch.BuildXml(@"C:\내 문서\a&b", "보고서 <2026> \"x\"");

        var document = System.Xml.Linq.XDocument.Parse(xml);
        var condition = document.Descendants("condition").Single();
        Assert.Equal("보고서 <2026> \"x\"", (string?)condition.Attribute("value"));
        Assert.Equal("wordmatch", (string?)condition.Attribute("operator"));
        Assert.Equal(@"C:\내 문서\a&b", (string?)document.Descendants("include").Single().Attribute("path"));
        Assert.Empty(System.Xml.Linq.XDocument.Parse(WindowsSearch.BuildXml(string.Empty, "x")).Descendants("scope"));
    }

    [Fact]
    public void SavedSearch_IsWrittenWithASafeNameAndOldOnesAreRemoved()
    {
        using var temp = new TempDirectory();
        string stale = temp.File("검색 - 오래된.search-ms");
        File.WriteAllText(stale, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));
        string other = temp.File("notes.txt");
        File.WriteAllText(other, "x");
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-30));

        string path = WindowsSearch.CreateSavedSearch(temp.Path, @"C:\", "a/b:c?*");

        Assert.True(File.Exists(path));
        Assert.EndsWith(".search-ms", path);
        Assert.DoesNotContain("/", Path.GetFileName(path));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(other), "other files are never touched");
        Assert.Contains("a/b:c?*", File.ReadAllText(path));
    }
}

public class ListPrinterTests
{
    private static List<ExportRow> Rows(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new ExportRow($"file{i}.txt", "TXT 파일", i * 1000, new DateTime(2026, 10, 30, 9, 0, 0), $@"C:\x\file{i}.txt", false))];

    [Theory]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(0, 10, 1)]
    [InlineData(95, 10, 10)]
    public void PageCount_RoundsUp_AndNeverDropsBelowOne(int rows, int perPage, int expected) =>
        Assert.Equal(expected, ListPrinter.PageCountFor(rows, perPage));

    [Fact]
    public void RowsPerPage_ReservesRoomForTitleHeaderAndFooter()
    {
        Assert.Equal(46, ListPrinter.RowsPerPage(500f, 10f));
        Assert.Equal(1, ListPrinter.RowsPerPage(5f, 10f));
    }

    [Fact]
    public void PageRows_SplitsTheListWithoutLosingOrRepeatingRows()
    {
        var printer = new ListPrinter("제목", Rows(25));

        IReadOnlyList<ExportRow> first = printer.PageRows(0, 10);
        IReadOnlyList<ExportRow> last = printer.PageRows(2, 10);

        Assert.Equal(10, first.Count);
        Assert.Equal("file1.txt", first[0].Name);
        Assert.Equal(5, last.Count);
        Assert.Equal("file21.txt", last[0].Name);
        Assert.Equal(25, Enumerable.Range(0, 3).Sum(page => printer.PageRows(page, 10).Count));
    }

    [Fact]
    public void DrawPage_PaintsTextAndTellsWhetherAnotherPageFollows()
    {
        var printer = new ListPrinter("내 폴더", Rows(200));
        using var bitmap = new Bitmap(800, 600);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        using var font = new Font("Segoe UI", 9f);

        bool more = printer.DrawPage(g, new RectangleF(20, 20, 760, 560), 0, font);

        Assert.True(more);
        Assert.True(CountDark(bitmap) > 300, "the page has text on it");

        int perPage = ListPrinter.RowsPerPage(560f, font.GetHeight(g) * 1.25f);
        int lastPage = ListPrinter.PageCountFor(200, perPage) - 1;
        using Graphics g2 = Graphics.FromImage(bitmap);
        g2.Clear(Color.White);
        Assert.False(printer.DrawPage(g2, new RectangleF(20, 20, 760, 560), lastPage, font));
    }

    [Fact]
    public void Document_ProducesAllPagesInThePreviewController()
    {
        var printer = new ListPrinter("미리보기", Rows(300));
        using PrintDocument document = printer.CreateDocument();
        var controller = new PreviewPrintController();
        document.PrintController = controller;

        try
        {
            document.Print();
        }
        catch (InvalidPrinterException)
        {
            // A PC without any printer cannot create page settings; nothing to verify there.
            return;
        }

        Assert.True(controller.GetPreviewPageInfo().Length > 1);
    }

    private static int CountDark(Bitmap bitmap)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).GetBrightness() < 0.4f)
                    count++;
            }
        }

        return count;
    }
}

/// <summary>Search, print and print preview through the real main window.</summary>
public class SearchAndPrintWindowTests
{
    private static TempDirectory CreateRoot()
    {
        var root = new TempDirectory();
        Directory.CreateDirectory(root.File(@"src\sub"));
        File.WriteAllText(root.File(@"src\a.txt"), "alpha");
        File.WriteAllText(root.File(@"src\sub\b.txt"), "beta needle");
        File.WriteAllText(root.File(@"src\sub\c.log"), "gamma");
        return root;
    }

    private static MainForm CreateForm(TempDirectory root, List<string>? errors = null)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(root.File("src"))!);
        PaneLayout layout = PaneLayout.FromId(1)!;
        return new MainForm(null, new Workspace { Layout = layout, Ratios = layout.DefaultRatios, Panes = basis.Panes }, null, null)
        {
            Size = new Size(1200, 800),
            SilentFileOperations = true,
            ReportError = message => errors?.Add(message),
        };
    }

    [Fact]
    public void SearchDialog_FindsResultsAndOpensOneInThePane()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.ActivePane.Browser.ItemCount == 2, "src listed");
            Task? opened = null;

            form.RunDialog = dialog =>
            {
                var search = (SearchDialog)dialog;
                Assert.Equal(root.File("src"), search.Root);

                search.NamePattern = "*.txt";
                Pump(search.SearchAsync());

                Assert.Equal(["a.txt", "b.txt"], search.Hits.Select(hit => Path.GetFileName(hit.Path)).Order(StringComparer.Ordinal));
                Assert.Equal(2, search.ResultList.Items.Count);
                Assert.Contains("2개 찾음", search.StatusText);

                search.ContentText = "needle";
                Pump(search.SearchAsync());
                Assert.Equal(["b.txt"], search.Hits.Select(hit => Path.GetFileName(hit.Path)));

                search.OpenResult(0);
                return DialogResult.OK;
            };

            Assert.True(form.RunCommand("tools.search"));

            await WaitUntil(() => string.Equals(form.ActivePane.Browser.CurrentLocation?.FileSystemPath, root.File(@"src\sub"), StringComparison.OrdinalIgnoreCase),
                "navigated to the result's folder");
            await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 1, "the result is selected");
            Assert.Equal(root.File(@"src\sub\b.txt"), form.ActivePane.Browser.GetSelectedPaths().Single(), ignoreCase: true);
            GC.KeepAlive(opened);
        });
    }

    [Fact]
    public void SearchDialog_ReportsAMissingFolder()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), form =>
        {
            using var dialog = new SearchDialog(root.File("nothing-here"));
            Pump(dialog.SearchAsync());

            Assert.Empty(dialog.Hits);
            Assert.Contains("찾을 수 없습니다", dialog.StatusText);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Print_HandsThePreparedDocumentToThePrintRunner()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.ActivePane.Browser.ItemCount == 2, "src listed");
            PrintDocument? received = null;
            string? title = null;
            form.PrintRunner = document =>
            {
                received = document;
                title = document.DocumentName;
                return true;
            };

            Assert.True(form.RunCommand("file.print"));

            Assert.NotNull(received);
            Assert.Equal(root.File("src"), title, ignoreCase: true);
            Assert.Equal(2, form.CreateListPrinter()!.Rows.Count);
        });
    }

    [Fact]
    public void PrintPreview_OpensThePreviewDialog()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.ActivePane.Browser.ItemCount == 2, "src listed");
            Form? shown = null;
            form.RunDialog = dialog =>
            {
                shown = dialog;
                Assert.IsType<PrintPreviewDialog>(dialog);
                return DialogResult.OK;
            };

            Assert.True(form.RunCommand("file.printPreview"));

            Assert.NotNull(shown);
        });
    }

    [Fact]
    public void Print_WithNothingToPrint_SaysSo()
    {
        using var empty = new TempDirectory();
        Directory.CreateDirectory(empty.File("src"));
        var errors = new List<string>();
        Run(() => CreateForm(empty, errors), async form =>
        {
            await Task.Delay(300);
            form.PrintList(preview: false);

            Assert.Single(errors);
            Assert.Contains("인쇄할 항목이 없습니다", errors[0]);
        });
    }

    [Fact]
    public void WindowsSearch_ShowsTheResultsInANewTab()
    {
        using TempDirectory root = CreateRoot();
        var errors = new List<string>();
        Run(() => CreateForm(root, errors), async form =>
        {
            await WaitUntil(() => form.ActivePane.Browser.ItemCount == 2, "src listed");
            int tabs = form.ActivePane.Tabs.Count;
            var failed = new List<string>();
            form.ActivePane.Browser.NavigationFailed += (_, e) => failed.Add(e.Location.DisplayName);

            form.OpenWindowsSearch(root.File("src"), "alpha");

            Assert.Empty(errors);
            Assert.Equal(tabs + 1, form.ActivePane.Tabs.Count);
            await WaitUntil(() => form.ActivePane.Browser.CurrentLocation?.DisplayName.EndsWith(".search-ms", StringComparison.OrdinalIgnoreCase) == true, "saved search shown");
            Assert.Empty(failed);
        });
    }

    [Fact]
    public void WindowsSearch_WithoutAQuery_SaysSo()
    {
        using TempDirectory root = CreateRoot();
        var errors = new List<string>();
        Run(() => CreateForm(root, errors), form =>
        {
            form.OpenWindowsSearch(root.File("src"), "  ");

            Assert.Single(errors);
            return Task.CompletedTask;
        });
    }

    /// <summary>Lets the window's message loop run while a task completes (progress reports are posted to the UI thread).</summary>
    private static void Pump(Task task)
    {
        DateTime deadline = DateTime.UtcNow + StepTimeout;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("a dialog task did not finish");

            Application.DoEvents();
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
    }
}
