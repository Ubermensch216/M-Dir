using System.Diagnostics;
using MDir.Features;

namespace MDir.Tests;

public class FolderComparerTests
{
    private static readonly DateTime Base = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string Make(string root, string relative, string content, DateTime? time = null)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, time ?? Base);
        return path;
    }

    private static async Task<CompareResult> Compare(TempDirectory dir, CompareOptions? options = null) =>
        await FolderComparer.CompareAsync(dir.File("L"), dir.File("R"), options ?? new CompareOptions());

    private static Dictionary<string, CompareStatus> Statuses(CompareResult result) =>
        result.Entries.ToDictionary(entry => entry.RelativePath, entry => entry.Status, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task EqualFolders_HaveNoDifferences()
    {
        using var dir = new TempDirectory();
        foreach (string side in new[] { "L", "R" })
        {
            Make(dir.File(side), "a.txt", "same");
            Make(dir.File(side), @"sub\b.txt", "same too");
        }

        CompareResult result = await Compare(dir);

        Assert.Empty(result.Entries);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task EveryKindOfDifferenceIsReported()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), "only-left.txt", "x");
        Make(dir.File("R"), "only-right.txt", "x");
        Make(dir.File("L"), "same.txt", "same");
        Make(dir.File("R"), "same.txt", "same");
        Make(dir.File("L"), "left-newer.txt", "v2", Base.AddHours(2));
        Make(dir.File("R"), "left-newer.txt", "v1", Base);
        Make(dir.File("L"), "right-newer.txt", "v1", Base);
        Make(dir.File("R"), "right-newer.txt", "v2", Base.AddHours(2));
        Make(dir.File("L"), "size-differs.txt", "short");
        Make(dir.File("R"), "size-differs.txt", "much longer content");
        Make(dir.File("L"), "thing", "i am a file");
        Directory.CreateDirectory(dir.File(@"R\thing"));

        Dictionary<string, CompareStatus> statuses = Statuses(await Compare(dir));

        Assert.Equal(
            new Dictionary<string, CompareStatus>
            {
                ["only-left.txt"] = CompareStatus.OnlyLeft,
                ["only-right.txt"] = CompareStatus.OnlyRight,
                ["left-newer.txt"] = CompareStatus.LeftNewer,
                ["right-newer.txt"] = CompareStatus.RightNewer,
                ["size-differs.txt"] = CompareStatus.Different,
                ["thing"] = CompareStatus.TypeMismatch,
            }.OrderBy(pair => pair.Key),
            statuses.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task TimesWithinTheTolerance_CountAsEqual()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), "a.txt", "same", Base);
        Make(dir.File("R"), "a.txt", "same", Base.AddSeconds(1.5));
        Make(dir.File("L"), "b.txt", "same", Base);
        Make(dir.File("R"), "b.txt", "same", Base.AddSeconds(3));

        Dictionary<string, CompareStatus> statuses = Statuses(await Compare(dir));

        Assert.DoesNotContain("a.txt", statuses.Keys);
        Assert.Equal(CompareStatus.RightNewer, statuses["b.txt"]);
    }

    [Fact]
    public async Task ContentMode_TreatsSameBytesWithDifferentTimesAsEqual()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), "touched.txt", "identical", Base);
        Make(dir.File("R"), "touched.txt", "identical", Base.AddDays(3));
        Make(dir.File("L"), "edited.txt", "aaaa", Base);
        Make(dir.File("R"), "edited.txt", "bbbb", Base.AddDays(3));

        Dictionary<string, CompareStatus> quick = Statuses(await Compare(dir));
        Dictionary<string, CompareStatus> content = Statuses(await Compare(dir, new CompareOptions(Mode: CompareMode.Content)));

        Assert.Equal(CompareStatus.RightNewer, quick["touched.txt"]);
        Assert.DoesNotContain("touched.txt", content.Keys);
        Assert.Equal(CompareStatus.RightNewer, content["edited.txt"]);
    }

    [Fact]
    public async Task ContentMode_SameTimeButDifferentBytesIsDifferent()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), "x.txt", "aaaa", Base);
        Make(dir.File("R"), "x.txt", "bbbb", Base);

        Assert.Empty((await Compare(dir)).Entries);
        Assert.Equal(CompareStatus.Different, Statuses(await Compare(dir, new CompareOptions(Mode: CompareMode.Content)))["x.txt"]);
    }

    [Fact]
    public async Task FoldersOnOneSide_AreOneEntryNotTheirContents()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), @"deep\a.txt", "1");
        Make(dir.File("L"), @"deep\more\b.txt", "2");
        Directory.CreateDirectory(dir.File("R"));

        CompareResult result = await Compare(dir);

        CompareEntry entry = Assert.Single(result.Entries);
        Assert.Equal("deep", entry.RelativePath);
        Assert.Equal(CompareStatus.OnlyLeft, entry.Status);
        Assert.True(entry.IsFolder);
    }

    [Fact]
    public async Task Recursion_ReportsRelativePathsAndCanBeSwitchedOff()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), @"a\b\c.txt", "x");
        Directory.CreateDirectory(dir.File(@"R\a\b"));
        Make(dir.File("L"), "top.txt", "x");
        Make(dir.File("R"), "top.txt", "y", Base.AddHours(1));

        Dictionary<string, CompareStatus> deep = Statuses(await Compare(dir));
        Dictionary<string, CompareStatus> shallow = Statuses(await Compare(dir, new CompareOptions(Recursive: false)));

        Assert.Equal(CompareStatus.OnlyLeft, deep[@"a\b\c.txt"]);
        Assert.Equal(CompareStatus.RightNewer, deep["top.txt"]);
        Assert.Equal(["top.txt"], shallow.Keys);
    }

    [Fact]
    public async Task NamesAreComparedIgnoringCase_AndIncludeSameListsEqualItems()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), "Readme.TXT", "same");
        Make(dir.File("R"), "readme.txt", "same");

        Assert.Empty((await Compare(dir)).Entries);
        CompareEntry entry = Assert.Single((await Compare(dir, new CompareOptions(IncludeSame: true))).Entries);
        Assert.Equal(CompareStatus.Same, entry.Status);
    }

    [Fact]
    public async Task JunctionsAreNotFollowed_SoALinkBackUpTheTreeCannotLoop()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), "a.txt", "x");
        Make(dir.File("R"), "a.txt", "x");

        // A junction in the left folder pointing at its parent would make a naive walk endless.
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dir.File(@"L\loop")}\" \"{dir.File("L")}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        mklink!.WaitForExit();
        Assert.True(Directory.Exists(dir.File(@"L\loop")), "the junction could not be created");

        CompareResult result = await Compare(dir).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Cancellation_StopsTheWalk()
    {
        using var dir = new TempDirectory();
        for (int i = 0; i < 20; i++)
            Make(dir.File("L"), $"f{i}.txt", "x");
        Directory.CreateDirectory(dir.File("R"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FolderComparer.CompareAsync(dir.File("L"), dir.File("R"), new CompareOptions(), null, cancel.Token));
    }

    [Fact]
    public async Task MissingFolders_DoNotThrowTheyAreReported()
    {
        using var dir = new TempDirectory();
        Make(dir.File("L"), "a.txt", "x");

        CompareResult result = await FolderComparer.CompareAsync(dir.File("L"), dir.File("nowhere"), new CompareOptions());

        Assert.Equal(CompareStatus.OnlyLeft, Assert.Single(result.Entries).Status);
        Assert.NotEmpty(result.Errors);
    }
}

public class SyncPlannerTests
{
    private static readonly DateTime Base = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<(CompareResult Result, string Left, string Right, TempDirectory Dir)> Setup()
    {
        var dir = new TempDirectory();
        void Make(string side, string relative, string content, DateTime time)
        {
            string path = dir.File(Path.Combine(side, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, time);
        }

        Make("L", "only-left.txt", "x", Base);
        Make("R", "only-right.txt", "x", Base);
        Make("L", @"sub\left-newer.txt", "new", Base.AddHours(1));
        Make("R", @"sub\left-newer.txt", "old", Base);
        Make("L", "right-newer.txt", "old", Base);
        Make("R", "right-newer.txt", "new", Base.AddHours(1));
        Make("L", "conflict.txt", "aaaa", Base);
        Make("R", "conflict.txt", "bbbbbbbb", Base);
        Make("L", @"newdir\inside.txt", "x", Base);
        CompareResult result = await FolderComparer.CompareAsync(dir.File("L"), dir.File("R"), new CompareOptions());
        return (result, dir.File("L"), dir.File("R"), dir);
    }

    [Fact]
    public async Task LeftToRight_CopiesNewAndNewerFromTheLeftOnly()
    {
        (CompareResult result, string left, string right, TempDirectory dir) = await Setup();
        using (dir)
        {
            IReadOnlyList<SyncCopy> copies = SyncPlanner.Plan(result.Entries, left, right, SyncDirection.LeftToRight);

            Assert.Equal(
                [
                    (Path.Combine(left, "newdir"), right),
                    (Path.Combine(left, "only-left.txt"), right),
                    (Path.Combine(left, @"sub\left-newer.txt"), Path.Combine(right, "sub")),
                ],
                copies.Select(copy => (copy.Source, copy.DestinationFolder)).OrderBy(pair => pair.Source));
        }
    }

    [Fact]
    public async Task RightToLeft_IsTheMirrorImage()
    {
        (CompareResult result, string left, string right, TempDirectory dir) = await Setup();
        using (dir)
        {
            IReadOnlyList<SyncCopy> copies = SyncPlanner.Plan(result.Entries, left, right, SyncDirection.RightToLeft);

            Assert.Equal(
                [(Path.Combine(right, "only-right.txt"), left), (Path.Combine(right, "right-newer.txt"), left)],
                copies.Select(copy => (copy.Source, copy.DestinationFolder)).OrderBy(pair => pair.Source));
        }
    }

    [Fact]
    public async Task TwoWay_SendsEachItemTheRightWay_AndLeavesConflictsAlone()
    {
        (CompareResult result, string left, string right, TempDirectory dir) = await Setup();
        using (dir)
        {
            IReadOnlyList<SyncCopy> copies = SyncPlanner.Plan(result.Entries, left, right, SyncDirection.TwoWay);

            Assert.Equal(5, copies.Count);
            Assert.DoesNotContain(copies, copy => copy.Source.EndsWith("conflict.txt"));
            Assert.Contains(copies, copy => copy.Source == Path.Combine(right, "only-right.txt") && copy.DestinationFolder == left);
            Assert.Contains(copies, copy => copy.Source == Path.Combine(left, "only-left.txt") && copy.DestinationFolder == right);
        }
    }

    [Fact]
    public async Task ExecutingThePlanWithTheShellEngine_MakesTheSidesMatch()
    {
        (CompareResult result, string left, string right, TempDirectory dir) = await Setup();
        using (dir)
        {
            var operations = new FileOperations(IntPtr.Zero, silent: true, replaceExisting: true);
            foreach (SyncCopy copy in SyncPlanner.Plan(result.Entries, left, right, SyncDirection.TwoWay))
            {
                FileOperationResult copied = operations.Copy([copy.Source], copy.DestinationFolder);
                Assert.True(copied.Success, copied.Error);
            }

            // Only the unresolved conflict remains, and nothing was deleted.
            CompareResult after = await FolderComparer.CompareAsync(left, right, new CompareOptions());

            Assert.Equal("conflict.txt", Assert.Single(after.Entries).RelativePath);
            Assert.Equal("aaaa", File.ReadAllText(Path.Combine(left, "conflict.txt")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(right, @"sub\left-newer.txt")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(left, "right-newer.txt")));
            Assert.True(File.Exists(Path.Combine(right, @"newdir\inside.txt")));
        }
    }
}
