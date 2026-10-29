using System.Security.Cryptography;
using MDir.Features;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>The stage 10 tools (export, sizes, multi-rename, compare, hash, hard link) through the real main window.</summary>
public class ToolCommandTests(ITestOutputHelper output)
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

    private static Workspace TwoPanes(TempDirectory root)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(root.File("src"))!);
        PaneLayout layout = PaneLayout.FromId(2)!;
        return new Workspace
        {
            Layout = layout,
            Ratios = layout.DefaultRatios,
            Panes = [basis.Panes[0], basis.Panes[0] with { Tabs = [new TabState(ShellLocation.Parse(root.File("dest"))!, FolderViewMode.Details, 16)] }, .. basis.Panes.Skip(2)],
        };
    }

    private static Workspace SinglePane(TempDirectory root)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(root.File("src"))!);
        PaneLayout layout = PaneLayout.FromId(1)!;
        return new Workspace { Layout = layout, Ratios = layout.DefaultRatios, Panes = basis.Panes };
    }

    private static MainForm CreateForm(Workspace workspace, List<string>? errors = null) =>
        new(null, workspace, null, null)
        {
            Size = new Size(1300, 800),
            SilentFileOperations = true,
            FileClipboard = new FakeClipboard(),
            ProcessLauncher = new FakeLauncher(),
            ReportError = message => errors?.Add(message),
        };

    private static async Task SelectAll(MainForm form, int expected)
    {
        form.ActivePane.Browser.SelectAll();
        await WaitUntil(() => form.ActivePane.Browser.SelectedCount == expected, $"{expected} items selected");
    }

    /// <summary>Lets the window's message loop run while a task completes (the dialogs' work resumes on the UI thread).</summary>
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

    [Fact]
    public void ExportList_WritesTheWholeListOrTheSelection()
    {
        using TempDirectory root = CreateRoot();
        Directory.CreateDirectory(root.File(@"src\folder"));
        Run(() => CreateForm(TwoPanes(root)), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 4, "src listed");
            string csv = root.File("all.csv");

            Assert.Equal(4, form.ExportList(csv, ExportFormat.Csv));
            string[] lines = File.ReadAllLines(csv);
            Assert.Equal(5, lines.Length);
            Assert.Contains(lines, line => line.Contains("a.txt") && line.Contains("TXT 파일"));
            Assert.Contains(lines, line => line.Contains("folder") && line.Contains("파일 폴더"));

            form.ActivePane.Browser.SelectItem(1);
            await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 1, "one selected");
            Assert.Equal(1, form.ExportList(root.File("one.txt"), ExportFormat.Text));
            Assert.Single(File.ReadAllLines(root.File("one.txt")), line => line.Contains(".txt") || line.Contains("folder"));

            Assert.Equal(1, form.ExportList(root.File("sheet.xlsx"), ExportFormat.Xlsx));
            Assert.True(new FileInfo(root.File("sheet.xlsx")).Length > 0);
        });
    }

    [Fact]
    public void ExportListCommand_UsesThePickedFile_AndDoesNothingWhenCancelled()
    {
        using TempDirectory root = CreateRoot();
        var errors = new List<string>();
        Run(() => CreateForm(TwoPanes(root), errors), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            string html = root.File("list.html");

            form.ExportTargetPicker = _ => null;
            Assert.True(form.RunCommand("file.exportList"));
            Assert.False(File.Exists(html));

            form.ExportTargetPicker = _ => (html, ExportFormat.Html);
            Assert.True(form.RunCommand("file.exportList"));
            string text = File.ReadAllText(html);
            Assert.Contains("a.txt", text);
            Assert.Contains("c.txt", text);

            form.ExportTargetPicker = _ => (root.File(@"no-such-folder\list.csv"), ExportFormat.Csv);
            form.RunCommand("file.exportList");
            Assert.Single(errors);
        });
    }

    [Fact]
    public void FolderSize_ShowsTheSelectionTotalInTheStatusBar()
    {
        using TempDirectory root = CreateRoot();
        Directory.CreateDirectory(root.File(@"src\big\inner"));
        File.WriteAllBytes(root.File(@"src\big\one.bin"), new byte[100_000]);
        File.WriteAllBytes(root.File(@"src\big\inner\two.bin"), new byte[50_000]);
        Run(() => CreateForm(TwoPanes(root)), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 4, "src listed");
            Assert.Equal(string.Empty, form.ActivePane.SizeText);

            // Nothing selected: the folder itself is measured (the three text files and big\).
            await form.MeasureSelectionAsync();
            Assert.Contains("5개 파일", form.ActivePane.SizeText);

            // The folder "big" is listed first; selecting it measures its tree.
            form.ActivePane.Browser.SelectItem(0);
            await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 1, "folder selected");
            await form.MeasureSelectionAsync();
            Assert.Contains("2개 파일", form.ActivePane.SizeText);
            Assert.Contains("146", form.ActivePane.SizeText);   // 150,000 bytes = 146.5 KB
            output.WriteLine(form.ActivePane.SizeText);

            // The command measures afresh (it forgets what was remembered).
            File.WriteAllBytes(root.File(@"src\big\three.bin"), new byte[1000]);
            Assert.True(form.RunCommand("tools.folderSize"));
            await WaitUntil(() => form.ActivePane.SizeText.Contains("3개 파일"), "the new file is counted");
        });
    }

    [Fact]
    public void AutoFolderSize_MeasuresOnSelection_AndClearsWhenSelectionChanges()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(TwoPanes(root)), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            Assert.False(form.Commands.Get("view.autoFolderSize").IsChecked!());
            Assert.True(form.RunCommand("view.autoFolderSize"));
            Assert.True(form.Commands.Get("view.autoFolderSize").IsChecked!());

            await SelectAll(form, 3);
            await WaitUntil(() => form.ActivePane.SizeText.Contains("3개 파일"), "the selection is measured automatically");

            form.ActivePane.Browser.SelectItem(0);
            await WaitUntil(() => form.ActivePane.SizeText.Contains("1개 파일"), "the new selection is measured");

            form.RunCommand("view.autoFolderSize");
            form.ActivePane.Browser.SelectItem(1);
            await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 1 && form.ActivePane.SizeText.Length == 0, "the old total is cleared and nothing new is measured");
        });
    }

    [Fact]
    public void MultiRename_RenamesTheSelection_AndCanBeUndone()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(TwoPanes(root)), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            Assert.False(form.RunCommand("tools.multiRename"), "nothing is selected");

            await SelectAll(form, 3);
            form.RunDialog = dialog =>
            {
                var rename = (MultiRenameDialog)dialog;
                rename.Options = rename.Options with { NameMask = "photo_[C:2]", ExtensionMask = "[E]" };
                Assert.Equal(3, rename.Plan.Count(item => item.WillRename));
                Assert.Equal(3, rename.Apply().Applied.Count);
                Assert.True(rename.CanUndo);
                return DialogResult.OK;
            };
            Assert.True(form.TryShortcut(Keys.Control | Keys.R));

            Assert.Equal(["photo_01.txt", "photo_02.txt", "photo_03.txt"], Directory.GetFiles(root.File("src")).Select(Path.GetFileName).Order());
            Assert.Equal("a.txt", File.ReadAllText(root.File(@"src\photo_01.txt")));

            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "list refreshed");
            await SelectAll(form, 3);
            form.RunDialog = dialog =>
            {
                var rename = (MultiRenameDialog)dialog;
                rename.Options = rename.Options with { NameMask = "img_[C:2]" };
                Assert.Equal(3, rename.Apply().Applied.Count);
                Assert.Equal(3, rename.UndoLast()!.Applied.Count);
                return DialogResult.OK;
            };
            form.RunCommand("tools.multiRename");
            Assert.True(File.Exists(root.File(@"src\photo_01.txt")), "the undo went back to the names before the second rename");
        });
    }

    [Fact]
    public void CompareFolders_ListsDifferences_AndCopiesInBothDirections()
    {
        using TempDirectory root = CreateRoot();
        File.WriteAllText(root.File(@"dest\only-right.txt"), "right");
        Run(() => CreateForm(TwoPanes(root)), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3 && form.Panes[1].Browser.ItemCount == 1, "both folders listed");
            Assert.NotNull(form.ComparePair());
            Assert.True(form.Commands.Get("tools.compareFolders").IsEnabled);

            CompareResult? seen = null;
            form.RunDialog = dialog =>
            {
                var compare = (CompareDialog)dialog;
                Pump(compare.CompareAsync());
                seen = compare.Result;
                Assert.Equal(4, compare.ResultList.Items.Count);

                Pump(compare.CopySelectedAsync(SyncDirection.LeftToRight));
                return DialogResult.OK;
            };
            Assert.True(form.RunCommand("tools.compareFolders"));

            Assert.Equal(3, seen!.Entries.Count(entry => entry.Status == CompareStatus.OnlyLeft));
            Assert.Equal(1, seen.Entries.Count(entry => entry.Status == CompareStatus.OnlyRight));
            Assert.True(File.Exists(root.File(@"dest\a.txt")));
            Assert.True(File.Exists(root.File(@"dest\c.txt")));
            Assert.False(File.Exists(root.File(@"src\only-right.txt")), "copying left to right does not touch the left");

            form.RunDialog = dialog =>
            {
                var compare = (CompareDialog)dialog;
                Pump(compare.CompareAsync());
                Pump(compare.SynchronizeAsync());
                Pump(compare.CompareAsync());
                Assert.Empty(compare.Result.Entries);
                return DialogResult.OK;
            };
            form.RunCommand("tools.compareFolders");
            Assert.Equal("right", File.ReadAllText(root.File(@"src\only-right.txt")));
        });
    }

    [Fact]
    public void CompareFolders_NeedsASecondFolderPane()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(SinglePane(root)), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            Assert.Null(form.ComparePair());
            Assert.False(form.RunCommand("tools.compareFolders"));
        });
    }

    [Fact]
    public void Hash_ComputesChecksumsOfTheSelectedFiles_AndRefusesFolders()
    {
        using TempDirectory root = CreateRoot();
        Directory.CreateDirectory(root.File(@"src\0folder"));
        var errors = new List<string>();
        Run(() => CreateForm(TwoPanes(root), errors), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 4, "src listed");

            // Only the folder is selected: there is nothing to hash.
            form.ActivePane.Browser.SelectItem(0);
            await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 1, "folder selected");
            form.RunCommand("tools.hash");
            Assert.Single(errors);

            await SelectAll(form, 4);
            string[]? hashes = null;
            string? listing = null;
            string? matched = null;
            form.RunDialog = dialog =>
            {
                var hash = (HashDialog)dialog;
                hash.SetAlgorithm(HashAlgorithmKind.Sha256);
                hash.Restart();   // a shown dialog starts by itself
                Pump(hash.Completion);
                hashes = [.. hash.Hashes.OfType<string>()];
                listing = hash.AllText();

                hash.ExpectedText = "SHA256: " + hashes[1];
                matched = string.Join(",", hash.States);
                return DialogResult.OK;
            };
            Assert.True(form.RunCommand("tools.hash"));

            Assert.Equal(3, hashes!.Length);
            string expectedA = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(root.File(@"src\a.txt")))).ToLowerInvariant();
            Assert.Equal(expectedA, hashes[0], ignoreCase: true);
            Assert.Contains($"{hashes[0]} *a.txt", listing);
            Assert.Equal("✘ 불일치,✔ 일치,✘ 불일치", matched);   // only b.txt has the pasted checksum
        });
    }

    [Fact]
    public void CreateHardLink_LinksTheSelectedFile()
    {
        using TempDirectory root = CreateRoot();
        var errors = new List<string>();
        Run(() => CreateForm(TwoPanes(root), errors), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            string link = root.File(@"src\a - link.txt");

            Assert.False(form.RunCommand("tools.createHardLink"), "nothing is selected");
            await SelectAll(form, 3);
            form.RunCommand("tools.createHardLink");
            Assert.Single(errors);   // several files selected: a link needs exactly one

            form.ActivePane.Browser.SelectItem(0);
            await WaitUntil(() => form.ActivePane.Browser.SelectedCount == 1, "a.txt selected");
            string? suggested = null;
            form.LinkPathPicker = suggestion =>
            {
                suggested = suggestion;
                return link;
            };
            Assert.True(form.RunCommand("tools.createHardLink"));

            Assert.Equal(root.File(@"src\a - 링크.txt"), suggested);
            Assert.True(File.Exists(link));
            File.AppendAllText(link, "+more");
            Assert.Equal("a.txt+more", File.ReadAllText(root.File(@"src\a.txt")));

            Assert.False(form.CreateHardLink(root.File(@"src\a.txt"), link), "the link name is taken");
            Assert.Equal(2, errors.Count);
        });
    }

    [Fact]
    public void ToolCommands_AreEnabledOnlyWhenTheyCanWork()
    {
        using TempDirectory root = CreateRoot();
        Run(() => CreateForm(TwoPanes(root)), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "src listed");
            Assert.True(form.Commands.Get("file.exportList").IsEnabled);
            Assert.True(form.Commands.Get("tools.folderSize").IsEnabled, "the folder itself can be measured");
            Assert.False(form.Commands.Get("tools.multiRename").IsEnabled);
            Assert.False(form.Commands.Get("tools.hash").IsEnabled);
            Assert.False(form.Commands.Get("tools.createHardLink").IsEnabled);

            await SelectAll(form, 3);
            Assert.True(form.Commands.Get("tools.multiRename").IsEnabled);
            Assert.True(form.Commands.Get("tools.hash").IsEnabled);
            Assert.True(form.Commands.Get("tools.createHardLink").IsEnabled);
        });
    }
}
