using MDir.Features;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class FolderSizeColumnServiceTests
{
    private static TempDirectory CreateTree()
    {
        var root = new TempDirectory();
        Directory.CreateDirectory(root.File(@"a\sub"));
        Directory.CreateDirectory(root.File("b"));
        File.WriteAllBytes(root.File(@"a\one.bin"), new byte[3000]);
        File.WriteAllBytes(root.File(@"a\sub\two.bin"), new byte[2000]);
        return root;
    }

    private static async Task<FolderSizeResult> WaitFor(FolderSizeColumnService service, string path)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (service.TryGet(path, out FolderSizeResult? result) && result is not null)
                return result;

            await Task.Delay(20);
        }

        throw new TimeoutException("size never arrived");
    }

    [Fact]
    public async Task Request_WorksTheSizeOutInTheBackground_AndRaisesChanged()
    {
        using TempDirectory root = CreateTree();
        using var service = new FolderSizeColumnService(new FolderSizeCache());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => changed.TrySetResult();

        Assert.False(service.TryGet(root.File("a"), out _));
        service.Request(root.File("a"));

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        FolderSizeResult result = await WaitFor(service, root.File("a"));
        Assert.Equal(5000, result.Bytes);
        Assert.True(result.Complete);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task SizesAreSharedWithTheCache_SoAClearForgetsThem()
    {
        using TempDirectory root = CreateTree();
        var cache = new FolderSizeCache();
        using var service = new FolderSizeColumnService(cache);
        service.Request(root.File("a"));
        await WaitFor(service, root.File("a"));

        Assert.True(cache.TryGet(root.File("a"), out FolderSizeResult? cached));
        Assert.Equal(5000, cached!.Bytes);

        cache.Clear();
        Assert.False(service.TryGet(root.File("a"), out _));
    }

    [Fact]
    public async Task RequestingTheSameFolderTwice_RunsItOnce()
    {
        using TempDirectory root = CreateTree();
        using var service = new FolderSizeColumnService(new FolderSizeCache());
        int changes = 0;
        service.Changed += () => Interlocked.Increment(ref changes);

        service.Request(root.File("a"));
        service.Request(root.File("a"));
        service.Request(root.File("a") + @"\");
        await WaitFor(service, root.File("a"));
        await Task.Delay(200);
        service.Request(root.File("a"));
        await Task.Delay(200);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task CancelPending_DropsQueuedFolders_AndTheServiceKeepsWorkingAfterwards()
    {
        using TempDirectory root = CreateTree();
        using var service = new FolderSizeColumnService(new FolderSizeCache(), parallelFolders: 1);
        service.Request(root.File("a"));
        service.Request(root.File("b"));

        service.CancelPending();
        await Task.Delay(300);
        service.Request(root.File("b"));

        FolderSizeResult empty = await WaitFor(service, root.File("b"));
        Assert.Equal(0, empty.Bytes);
        Assert.Equal(0, service.PendingCount);
    }

    [Theory]
    [InlineData(0L, true, "0바이트")]
    [InlineData(352L * 1024, true, "352KB")]
    [InlineData(5L * 1024 * 1024, false, "5.0MB+")]
    public void Format_IsCompact_AndMarksPartialResults(long bytes, bool complete, string expected) =>
        Assert.Equal(expected, FolderSizeColumnService.Format(new FolderSizeResult(bytes, 1, 1, complete)).Replace(" ", string.Empty));
}

/// <summary>The Size column of a real shell list gets the sizes of folders.</summary>
public class FolderSizeColumnListTests
{
    private static MainForm CreateForm(string folder, AppProfile? profile = null)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(folder)!);
        PaneLayout layout = PaneLayout.FromId(1)!;
        return new MainForm(null, new Workspace { Layout = layout, Ratios = layout.DefaultRatios, Panes = basis.Panes }, null, profile)
        {
            Size = new Size(1300, 800),
            SilentFileOperations = true,
        };
    }

    [Fact]
    public void SizeColumnLabel_IsReadFromThePropertySystem()
    {
        Assert.False(string.IsNullOrWhiteSpace(PropertyDescriptions.SizeLabel));
    }

    [Fact]
    public void FolderSizes_AreDrawnIntoTheListWhenSwitchedOn_AndNotWhenOff()
    {
        using var root = new TempDirectory();
        Directory.CreateDirectory(root.File(@"big\inner"));
        Directory.CreateDirectory(root.File("small"));
        File.WriteAllBytes(root.File(@"big\inner\data.bin"), new byte[300 * 1024]);
        File.WriteAllBytes(root.File("file.bin"), new byte[10]);
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(appDirectory: temp.Path, roamingAppData: temp.Path), new AppSettings());

        Run(() => CreateForm(root.Path, profile), async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount == 3, "three items listed");
            await Task.Delay(500);
            Assert.Equal(0, browser.FolderSizeDraws);

            Assert.True(form.RunCommand("view.folderSizeColumn"));
            Assert.True(AppSettings.Load(profile.Paths.SettingsFile).FolderSizeColumn);

            await WaitUntil(() => browser.FolderSizeDraws >= 2, "sizes drawn for the two folders");
            var folderSizes = (FolderSizeColumnService)typeof(MainForm).GetProperty("FolderSizeSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form)!;
            await WaitUntil(() => folderSizes.TryGet(root.File("big"), out FolderSizeResult? big) && big!.Bytes == 300 * 1024, "the big folder was measured");

            int before = browser.FolderSizeDraws;
            form.RunCommand("view.folderSizeColumn");
            await Task.Delay(500);
            browser.RefreshView();
            await Task.Delay(500);
            Assert.Equal(before, browser.FolderSizeDraws);
        });
    }

    [Fact]
    public void TheDrawnSize_IsVisibleInTheSizeColumn()
    {
        using var root = new TempDirectory();
        Directory.CreateDirectory(root.File("bigfolder"));
        File.WriteAllBytes(root.File(@"bigfolder\data.bin"), new byte[700 * 1024]);
        File.WriteAllBytes(root.File("onlyfile.txt"), new byte[10]);

        Run(() => CreateForm(root.Path), async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount == 2, "two items listed");
            await Task.Delay(500);
            string before = SaveScreenshot(form, "folder-size-off.png");

            form.SetFolderSizeColumn(true);
            await WaitUntil(() => browser.FolderSizeDraws >= 1, "a size was drawn");
            await Task.Delay(600);
            string after = SaveScreenshot(form, "folder-size-on.png");

            Assert.NotEqual(File.ReadAllBytes(before).Length, File.ReadAllBytes(after).Length);
        });
    }
}
