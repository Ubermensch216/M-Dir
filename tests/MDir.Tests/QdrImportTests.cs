using System.Text;
using MDir.Features;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class QdrImporterTests
{
    private const string ThisPc = "%14%00%1F%50%E0%4F%D0%20%EA%3A%69%10%A2%D8%08%00%2B%30%30%9D%00%00";

    // The shape of a real start.qdr (Q-Dir 12.77): one line per setting, a pane's folder and view mode joined by '&'.
    private static string Sample(string layout = "0") => $"""
        Q-Dir Version=12.77
        ViewMode={layout}
        Folder1={ThisPc}&ViewModeFlags1=1075838981&ViewMode1=4&UNICODE=1



        Folder2={ThisPc}&ViewModeFlags2=1075838981&ViewMode2=1&UNICODE=1



        Folder3=%00&ViewModeFlags3=1075838981&ViewMode3=4&UNICODE=1
        Folder4={ThisPc}&ViewModeFlags4=1075838981&ViewMode4=99&UNICODE=1
        SP0=4000
        SP1=5003
        SP2=6000
        SPT=2000
        tree_mode=1
        Tree1=1
        Tree2=0
        Tree3=1
        Tree4=0
        Tree1split=3000
        Tree2split=2500
        Tree3split=2500
        Tree4split=2500
        Vorschau=1
        VorschauPos=7000
        Focus=2
        colorthema=0
        windowplacement=10;20;900;700|sw=3
        """;

    [Fact]
    public void Parse_ReadsFoldersViewModesSplittersTreeAndPreview()
    {
        QdrImportResult? result = QdrImporter.Parse(Sample());

        Assert.NotNull(result);
        Workspace workspace = result.Workspace;
        Assert.Equal(PaneLayout.Default.Id, workspace.Layout.Id);
        Assert.Equal([4000, 5003, 6000], workspace.Ratios);
        Assert.Equal(2, workspace.ActivePane);
        Assert.True(workspace.SharedTree);
        Assert.True(workspace.PreviewVisible);
        Assert.Equal(3000, workspace.PreviewSplit);
        Assert.Equal(new WindowPlacement(10, 20, 900, 700, true), workspace.Window);

        Assert.Equal(4, workspace.Panes.Count);
        Assert.Equal(FolderViewMode.Details, workspace.Panes[0].Tabs[0].ViewMode);
        Assert.Equal(ShellLocation.Parse("shell:MyComputerFolder"), workspace.Panes[0].Tabs[0].Location);
        Assert.True(workspace.Panes[0].TreeVisible);
        Assert.Equal(3000, workspace.Panes[0].TreeSplit);
        Assert.False(workspace.Panes[1].TreeVisible);
        Assert.Equal((FolderViewMode)1, workspace.Panes[1].Tabs[0].ViewMode);
    }

    [Fact]
    public void Parse_FallsBackForDamagedFolders_AndUnknownViewModes_AndSaysSo()
    {
        QdrImportResult result = QdrImporter.Parse(Sample())!;

        Assert.Equal(Workspace.DefaultLocation, result.Workspace.Panes[2].Tabs[0].Location);
        Assert.Equal(FolderViewMode.Details, result.Workspace.Panes[3].Tabs[0].ViewMode);
        Assert.Contains(result.Notes, note => note.Contains("패널 3"));
        Assert.Contains(result.Notes, note => note.Contains("열 구성"));
    }

    [Fact]
    public void Parse_AnUnknownLayoutNumberGivesTheDefaultLayoutWithANote()
    {
        QdrImportResult result = QdrImporter.Parse(Sample(layout: "7"))!;

        Assert.Equal(PaneLayout.Default.Id, result.Workspace.Layout.Id);
        Assert.Contains(result.Notes, note => note.Contains("레이아웃 번호 7"));
    }

    [Fact]
    public void Parse_RejectsFilesThatAreNotQDirWorkspaces()
    {
        Assert.Null(QdrImporter.Parse("[Workspace]\nLayout=1\n"));
        Assert.Null(QdrImporter.Parse(string.Empty));
        Assert.Null(QdrImporter.Parse("just some text"));
    }

    [Fact]
    public void Parse_ToleratesMissingSettings()
    {
        QdrImportResult? result = QdrImporter.Parse($"Q-Dir Version=12.77\r\nFolder1={ThisPc}\r\n");

        Assert.NotNull(result);
        Assert.Equal(PaneLayout.Default.DefaultRatios, result.Workspace.Ratios);
        Assert.False(result.Workspace.PreviewVisible);
        Assert.Null(result.Workspace.Window);
        Assert.Equal(4, result.Workspace.Panes.Count);
    }

    [Fact]
    public void Import_ReadsAUtf16FileWithABom_AndTheRealFileOfThisPc()
    {
        using var temp = new TempDirectory();
        string path = temp.File("start.qdr");
        File.WriteAllText(path, Sample(), new UnicodeEncoding(false, true));

        QdrImportResult? result = QdrImporter.Import(path);

        Assert.NotNull(result);
        Assert.Equal(2, result.Workspace.ActivePane);
        Assert.Null(QdrImporter.Import(temp.File("missing.qdr")));

        const string real = @"D:\Q-Dir\start.qdr";
        if (File.Exists(real))
        {
            QdrImportResult? imported = QdrImporter.Import(real);
            Assert.NotNull(imported);
            Assert.Equal(4, imported.Workspace.Panes.Count);
        }
    }
}

public class QdrImportWindowTests
{
    [Fact]
    public void ImportCommand_OpensTheWorkspace_AndTellsWhatWasLeftOut()
    {
        using var temp = new TempDirectory();
        string path = temp.File("start.qdr");
        File.WriteAllText(path, "Q-Dir Version=12.77\r\nViewMode=0\r\nFolder1=%14%00%1F%50%E0%4F%D0%20%EA%3A%69%10%A2%D8%08%00%2B%30%30%9D%00%00&ViewMode1=4\r\nFocus=1\r\nSP0=3000\r\n");
        string? notice = null;
        var errors = new List<string>();

        Run(() => new MainForm(null, null, null, null)
        {
            Size = new Size(1200, 800),
            QdrPicker = () => path,
            ShowNotice = message => notice = message,
            ReportError = errors.Add,
        }, form =>
        {
            Assert.True(form.RunCommand("file.importQdir"));

            Assert.Empty(errors);
            Assert.NotNull(notice);
            Assert.Contains("가져왔습니다", notice);
            Assert.Equal(1, form.ActivePane.Index);
            Assert.Equal(3000, form.LayoutPanel.Ratios[0]);
            Assert.Null(form.WorkspacePath);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ImportCommand_CancelledOrWrongFile_ChangesNothing()
    {
        using var temp = new TempDirectory();
        string wrong = temp.File("notes.qdr");
        File.WriteAllText(wrong, "hello");
        string? picked = null;
        var errors = new List<string>();

        Run(() => new MainForm(null, null, null, null)
        {
            Size = new Size(1200, 800),
            QdrPicker = () => picked,
            ShowNotice = _ => Assert.Fail("no notice for a failed import"),
            ReportError = errors.Add,
        }, form =>
        {
            Assert.True(form.RunCommand("file.importQdir"));
            Assert.Empty(errors);

            picked = wrong;
            Assert.True(form.RunCommand("file.importQdir"));
            Assert.Single(errors);
            Assert.Contains("Q-Dir 작업 공간이 아니거나", errors[0]);
            return Task.CompletedTask;
        });
    }
}
