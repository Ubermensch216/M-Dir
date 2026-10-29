using System.Diagnostics;
using MDir.Features;

namespace MDir.Tests;

public class FolderSizeTests
{
    private static void Write(TempDirectory dir, string relative, int bytes)
    {
        string path = dir.File(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public async Task Calculate_AddsUpFilesAtEveryDepth()
    {
        using var dir = new TempDirectory();
        Write(dir, "a.bin", 1000);
        Write(dir, @"sub\b.bin", 2000);
        Write(dir, @"sub\deeper\c.bin", 3000);
        Directory.CreateDirectory(dir.File(@"empty\also-empty"));

        FolderSizeResult result = await FolderSizeCalculator.CalculateAsync(dir.Path);

        Assert.Equal(6000, result.Bytes);
        Assert.Equal(3, result.Files);
        Assert.Equal(4, result.Folders);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task Calculate_EmptyFolder_IsZero()
    {
        using var dir = new TempDirectory();

        FolderSizeResult result = await FolderSizeCalculator.CalculateAsync(dir.Path);

        Assert.Equal(new FolderSizeResult(0, 0, 0, true), result);
    }

    [Fact]
    public async Task Calculate_CountsHiddenFilesToo()
    {
        using var dir = new TempDirectory();
        Write(dir, "visible.bin", 100);
        Write(dir, "hidden.bin", 50);
        File.SetAttributes(dir.File("hidden.bin"), FileAttributes.Hidden);

        Assert.Equal(150, (await FolderSizeCalculator.CalculateAsync(dir.Path)).Bytes);
    }

    [Fact]
    public async Task Calculate_DoesNotFollowJunctions()
    {
        using var dir = new TempDirectory();
        Write(dir, @"real\data.bin", 4000);
        Directory.CreateDirectory(dir.File("tree"));
        Write(dir, @"tree\own.bin", 10);

        // A junction to a big folder and one pointing back at its own parent: neither adds size nor loops.
        foreach ((string link, string target) in new[] { (dir.File(@"tree\toReal"), dir.File("real")), (dir.File(@"tree\toParent"), dir.File("tree")) })
        {
            var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            process!.WaitForExit();
            Assert.True(Directory.Exists(link), "the junction could not be created");
        }

        FolderSizeResult result = await FolderSizeCalculator.CalculateAsync(dir.File("tree")).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(10, result.Bytes);
        Assert.Equal(1, result.Files);
    }

    [Fact]
    public async Task Calculate_ReportsProgressAndCanBeCancelled()
    {
        using var dir = new TempDirectory();
        for (int i = 0; i < 4500; i++)
            File.WriteAllBytes(dir.File($"f{i}.bin"), [1, 2, 3]);
        var reports = new List<long>();

        FolderSizeResult result = await FolderSizeCalculator.CalculateAsync(dir.Path, new Progress<long>(reports.Add));
        await Task.Delay(100);

        Assert.Equal(13_500, result.Bytes);
        Assert.Contains(13_500L, reports);
        Assert.True(reports.Count >= 3, "intermediate totals were reported");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FolderSizeCalculator.CalculateAsync(dir.Path, null, cancel.Token));
    }

    [Fact]
    public async Task Calculate_AMissingFolderIsIncompleteNotAnError()
    {
        using var dir = new TempDirectory();

        FolderSizeResult result = await FolderSizeCalculator.CalculateAsync(dir.File("gone"));

        Assert.False(result.Complete);
        Assert.Equal(0, result.Bytes);
    }

    [Fact]
    public async Task Cache_ReusesFreshResults_ExpiresThemAndInvalidatesRelatedFolders()
    {
        using var dir = new TempDirectory();
        Write(dir, @"a\one.bin", 100);
        Write(dir, @"b\two.bin", 200);
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new FolderSizeCache(TimeSpan.FromMinutes(5), () => now);

        FolderSizeResult first = await cache.GetAsync(dir.File("a"));
        Write(dir, @"a\added.bin", 900);
        FolderSizeResult cached = await cache.GetAsync(dir.File("A"));

        Assert.Equal(100, first.Bytes);
        Assert.Equal(100, cached.Bytes);

        now += TimeSpan.FromMinutes(6);
        Assert.Equal(1000, (await cache.GetAsync(dir.File("a"))).Bytes);

        await cache.GetAsync(dir.File("b"));
        await cache.GetAsync(dir.Path);
        Assert.Equal(3, cache.Count);
        cache.Invalidate(dir.File(@"a\sub-folder-that-changed"));
        Assert.True(cache.TryGet(dir.File("b"), out _));
        Assert.False(cache.TryGet(dir.File("a"), out _), "a parent of a changed place is stale too");
        Assert.False(cache.TryGet(dir.Path, out _), "and so is the folder above it");

        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Cache_DoesNotRememberIncompleteResults()
    {
        using var dir = new TempDirectory();
        var cache = new FolderSizeCache();

        await cache.GetAsync(dir.File("missing"));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task SelectionSizer_MixesFilesAndFolders()
    {
        using var dir = new TempDirectory();
        Write(dir, "file.bin", 500);
        Write(dir, @"folder\a.bin", 1000);
        Write(dir, @"folder\sub\b.bin", 2000);
        var cache = new FolderSizeCache();

        SelectionSize size = await SelectionSizer.MeasureAsync([dir.File("file.bin"), dir.File("folder"), dir.File("vanished.bin")], cache);

        Assert.Equal(3500, size.Bytes);
        Assert.Equal(3, size.Files);
        Assert.Equal(2, size.Folders);
        Assert.True(size.Complete);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task SelectionSizer_CanBeCancelled_AndEmptySelectionIsZero()
    {
        using var dir = new TempDirectory();
        Write(dir, "a.bin", 10);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SelectionSizer.MeasureAsync([dir.File("a.bin")], new FolderSizeCache(), null, cancel.Token));
        Assert.Equal(new SelectionSize(0, 0, 0, true), await SelectionSizer.MeasureAsync([], new FolderSizeCache()));
    }
}
