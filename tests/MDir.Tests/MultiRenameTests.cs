using MDir.Features;

namespace MDir.Tests;

public class MultiRenameTests
{
    private static string Make(TempDirectory dir, string relative, string content = "x")
    {
        string path = dir.File(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string[] Names(IReadOnlyList<RenamePlanItem> plan) => [.. plan.Select(item => item.NewName)];

    [Fact]
    public void DefaultOptions_ChangeNothing()
    {
        using var dir = new TempDirectory();
        string[] paths = [Make(dir, "a.txt"), Make(dir, "b.TXT")];

        IReadOnlyList<RenamePlanItem> plan = RenamePlanner.Plan(paths, new RenameOptions());

        Assert.Equal(["a.txt", "b.TXT"], Names(plan));
        Assert.All(plan, item => Assert.Equal(RenameStatus.Unchanged, item.Status));
    }

    [Fact]
    public void Masks_ExpandNameExtensionCounterAndParent()
    {
        using var dir = new TempDirectory();
        string[] paths = [Make(dir, @"Trip\img_a.JPG"), Make(dir, @"Trip\img_b.JPG"), Make(dir, @"Trip\img_c.png")];
        var options = new RenameOptions(NameMask: "[P]_[C:3]_[N]", ExtensionMask: "[E]", CounterStart: 7, CounterStep: 2);

        IReadOnlyList<RenamePlanItem> plan = RenamePlanner.Plan(paths, options);

        Assert.Equal(["Trip_007_img_a.JPG", "Trip_009_img_b.JPG", "Trip_011_img_c.png"], Names(plan));
        Assert.All(plan, item => Assert.Equal(RenameStatus.Ready, item.Status));
    }

    [Fact]
    public void CounterDigitsOption_AndLiteralBrackets_AndUnknownTokens()
    {
        using var dir = new TempDirectory();
        string[] paths = [Make(dir, "a.txt"), Make(dir, "b.txt")];

        Assert.Equal(["file01.txt", "file02.txt"], Names(RenamePlanner.Plan(paths, new RenameOptions("file[C]", CounterDigits: 2))));
        Assert.Equal(["[x]a.txt", "[x]b.txt"], Names(RenamePlanner.Plan(paths, new RenameOptions("[[x][N]"))));
        Assert.Equal(["[Z]a.txt", "[Z]b.txt"], Names(RenamePlanner.Plan(paths, new RenameOptions("[Z][N]"))));
    }

    [Fact]
    public void DateToken_UsesTheModifiedTimeAndAFormat()
    {
        using var dir = new TempDirectory();
        string path = Make(dir, "a.txt");
        File.SetLastWriteTime(path, new DateTime(2026, 3, 9, 14, 5, 6));

        Assert.Equal("20260309_a.txt", Names(RenamePlanner.Plan([path], new RenameOptions("[D]_[N]")))[0]);
        Assert.Equal("2026-03-09 14.05.06.txt", Names(RenamePlanner.Plan([path], new RenameOptions("[D:yyyy-MM-dd HH.mm.ss]")))[0]);
    }

    [Fact]
    public void Extensions_CanBeReplacedOrDropped_FoldersHaveNone()
    {
        using var dir = new TempDirectory();
        string file = Make(dir, "photo.jpeg");
        Directory.CreateDirectory(dir.File("My.Folder"));

        Assert.Equal("photo.jpg", RenamePlanner.NewNameFor(file, 0, new RenameOptions(ExtensionMask: "jpg")));
        Assert.Equal("photo", RenamePlanner.NewNameFor(file, 0, new RenameOptions(ExtensionMask: "")));
        Assert.Equal("PHOTO.JPEG", RenamePlanner.NewNameFor(file, 0, new RenameOptions(NameCase: CaseMode.Upper, ExtensionCase: CaseMode.Upper)));
        Assert.Equal("My.Folder - copy", RenamePlanner.NewNameFor(dir.File("My.Folder"), 0, new RenameOptions("[N] - copy", ExtensionMask: "zzz")));
    }

    [Fact]
    public void FindReplace_PlainAndRegex_WithCaseSensitivity()
    {
        using var dir = new TempDirectory();
        string path = Make(dir, "Holiday_2025_HOLIDAY.txt");

        Assert.Equal("Trip_2025_Trip.txt", RenamePlanner.NewNameFor(path, 0, new RenameOptions(Find: "holiday", Replace: "Trip")));
        Assert.Equal("Holiday_2025_Trip.txt", RenamePlanner.NewNameFor(path, 0, new RenameOptions(Find: "HOLIDAY", Replace: "Trip", IgnoreCase: false)));
        Assert.Equal("Holiday_YEAR_HOLIDAY.txt", RenamePlanner.NewNameFor(path, 0, new RenameOptions(Find: @"\d{4}", Replace: "YEAR", UseRegex: true)));
        Assert.Equal("Holiday_2025_HOLIDAY.txt", RenamePlanner.NewNameFor(path, 0, new RenameOptions(Find: "(", Replace: "x", UseRegex: true))); // a broken pattern changes nothing
        Assert.Equal("2025-Holiday.txt", RenamePlanner.NewNameFor(Make(dir, "Holiday-2025.txt"), 0, new RenameOptions(Find: @"(\w+)-(\d+)", Replace: "$2-$1", UseRegex: true)));
    }

    [Theory]
    [InlineData(CaseMode.Lower, "hello big world")]
    [InlineData(CaseMode.Upper, "HELLO BIG WORLD")]
    [InlineData(CaseMode.Title, "Hello Big World")]
    [InlineData(CaseMode.Sentence, "Hello big world")]
    [InlineData(CaseMode.None, "hELLO bIG wORLD")]
    public void CaseModes(CaseMode mode, string expected) => Assert.Equal(expected, RenamePlanner.ApplyCase("hELLO bIG wORLD", mode));

    [Theory]
    [InlineData("ok.txt", true)]
    [InlineData("한글 이름.docx", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a:b.txt", false)]
    [InlineData("a/b", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("COM1.log", false)]
    [InlineData("console.txt", true)]
    public void NameValidity(string name, bool valid) => Assert.Equal(valid, RenamePlanner.IsValidName(name));

    [Fact]
    public void InvalidDuplicateAndExistingTargets_AreFlagged()
    {
        using var dir = new TempDirectory();
        string[] paths = [Make(dir, "a.txt"), Make(dir, "b.txt"), Make(dir, "c.txt")];
        Make(dir, "taken.txt");

        // All three get the same name.
        IReadOnlyList<RenamePlanItem> duplicates = RenamePlanner.Plan(paths, new RenameOptions("same"));
        Assert.All(duplicates, item => Assert.Equal(RenameStatus.DuplicateName, item.Status));

        // The name of a file that stays put blocks the rename.
        IReadOnlyList<RenamePlanItem> exists = RenamePlanner.Plan([paths[0]], new RenameOptions("taken"));
        Assert.Equal(RenameStatus.TargetExists, exists[0].Status);

        IReadOnlyList<RenamePlanItem> invalid = RenamePlanner.Plan([paths[0]], new RenameOptions("a:b"));
        Assert.Equal(RenameStatus.InvalidName, invalid[0].Status);
        Assert.False(invalid[0].WillRename);
    }

    [Fact]
    public void ANameFreedByAnotherRename_IsNotBlocked_ButANameHeldByAFileThatStaysIs()
    {
        using var dir = new TempDirectory();
        string[] series = [Make(dir, "1.txt"), Make(dir, "2.txt"), Make(dir, "3.txt")];

        IReadOnlyList<RenamePlanItem> shifted = RenamePlanner.Plan(series, new RenameOptions("[C]", CounterStart: 2));
        Assert.Equal(["2.txt", "3.txt", "4.txt"], Names(shifted));
        Assert.All(shifted, item => Assert.Equal(RenameStatus.Ready, item.Status));

        // Only 1.txt is renamed, onto the name of 2.txt which stays where it is.
        IReadOnlyList<RenamePlanItem> blocked = RenamePlanner.Plan([series[0]], new RenameOptions("2"));
        Assert.Equal(RenameStatus.TargetExists, blocked[0].Status);
    }

    [Fact]
    public void CaseOnlyChange_IsAllowedAndWorks()
    {
        using var dir = new TempDirectory();
        string path = Make(dir, "readme.txt", "hello");

        IReadOnlyList<RenamePlanItem> plan = RenamePlanner.Plan([path], new RenameOptions(NameCase: CaseMode.Upper));
        Assert.Equal(RenameStatus.Ready, plan[0].Status);
        RenameResult result = RenameExecutor.Apply(plan);

        Assert.Empty(result.Failed);
        Assert.Equal("README.txt", Path.GetFileName(Directory.GetFiles(dir.Path).Single()));
        Assert.Equal("hello", File.ReadAllText(dir.File("README.txt")));
    }

    [Fact]
    public void Apply_RenamesFilesAndFolders()
    {
        using var dir = new TempDirectory();
        string[] paths = [Make(dir, "a.txt", "A"), Make(dir, "b.txt", "B")];
        Directory.CreateDirectory(dir.File("folder"));

        RenameResult result = RenameExecutor.Apply(RenamePlanner.Plan([.. paths, dir.File("folder")], new RenameOptions("new_[C]")));

        Assert.Empty(result.Failed);
        Assert.Equal(3, result.Applied.Count);
        Assert.Equal("A", File.ReadAllText(dir.File("new_1.txt")));
        Assert.Equal("B", File.ReadAllText(dir.File("new_2.txt")));
        Assert.True(Directory.Exists(dir.File("new_3")));
        Assert.False(File.Exists(dir.File("a.txt")));
    }

    [Fact]
    public void Apply_ASwapNeverOverwritesAnything()
    {
        using var dir = new TempDirectory();
        string a = Make(dir, "a.txt", "content A");
        string b = Make(dir, "b.txt", "content B");
        IReadOnlyList<RenamePlanItem> plan =
        [
            new(a, "b.txt", RenameStatus.Ready),
            new(b, "a.txt", RenameStatus.Ready),
        ];

        RenameResult result = RenameExecutor.Apply(plan);

        Assert.Empty(result.Failed);
        Assert.Equal("content A", File.ReadAllText(dir.File("b.txt")));
        Assert.Equal("content B", File.ReadAllText(dir.File("a.txt")));
        Assert.Equal(2, Directory.GetFileSystemEntries(dir.Path).Length);
    }

    [Fact]
    public void Apply_AShiftedSeriesEndsWithEveryFileIntact()
    {
        using var dir = new TempDirectory();
        string[] series = [Make(dir, "1.txt", "one"), Make(dir, "2.txt", "two"), Make(dir, "3.txt", "three")];

        RenameResult result = RenameExecutor.Apply(RenamePlanner.Plan(series, new RenameOptions("[C]", CounterStart: 2)));

        Assert.Empty(result.Failed);
        Assert.False(File.Exists(dir.File("1.txt")));
        Assert.Equal("one", File.ReadAllText(dir.File("2.txt")));
        Assert.Equal("two", File.ReadAllText(dir.File("3.txt")));
        Assert.Equal("three", File.ReadAllText(dir.File("4.txt")));
        Assert.Equal(3, Directory.GetFileSystemEntries(dir.Path).Length);
    }

    [Fact]
    public void Apply_SkipsItemsThatCannotBeRenamed_AndReportsFailuresWithoutStopping()
    {
        using var dir = new TempDirectory();
        string a = Make(dir, "a.txt", "A");
        string b = Make(dir, "b.txt", "B");
        string c = Make(dir, "c.txt", "C");
        Make(dir, "taken.txt", "T");

        // b is locked open, so its rename fails; the others still go through.
        using var lockedB = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None);
        IReadOnlyList<RenamePlanItem> plan = RenamePlanner.Plan([a, b, c], new RenameOptions("x[C]"));
        RenameResult result = RenameExecutor.Apply(plan);

        Assert.Equal([a, c], result.Applied.Select(pair => pair.From));
        Assert.Equal([b], result.Failed.Select(failure => failure.Source));
        Assert.True(File.Exists(dir.File("x1.txt")));
        Assert.True(File.Exists(b));
        Assert.True(File.Exists(dir.File("x3.txt")));
        Assert.Equal("T", File.ReadAllText(dir.File("taken.txt")));

        // Items flagged as problems are not touched at all.
        RenameResult none = RenameExecutor.Apply([new RenamePlanItem(dir.File("taken.txt"), "a:b", RenameStatus.InvalidName)]);
        Assert.Empty(none.Applied);
        Assert.Empty(none.Failed);
    }

    [Fact]
    public void Undo_RestoresTheOriginalNames_EvenAfterASwap()
    {
        using var dir = new TempDirectory();
        string[] series = [Make(dir, "1.txt", "one"), Make(dir, "2.txt", "two"), Make(dir, "3.txt", "three")];
        RenameResult applied = RenameExecutor.Apply(RenamePlanner.Plan(series, new RenameOptions("[C]", CounterStart: 2)));

        RenameResult undone = RenameExecutor.Undo(applied);

        Assert.Empty(undone.Failed);
        Assert.Equal("one", File.ReadAllText(dir.File("1.txt")));
        Assert.Equal("two", File.ReadAllText(dir.File("2.txt")));
        Assert.Equal("three", File.ReadAllText(dir.File("3.txt")));
        Assert.Equal(3, Directory.GetFileSystemEntries(dir.Path).Length);
    }

    [Fact]
    public void NoTemporaryNamesAreLeftBehind()
    {
        using var dir = new TempDirectory();
        string a = Make(dir, "a.txt");
        string b = Make(dir, "b.txt");

        RenameExecutor.Apply([new(a, "b.txt", RenameStatus.Ready), new(b, "a.txt", RenameStatus.Ready)]);

        Assert.DoesNotContain(Directory.GetFileSystemEntries(dir.Path), entry => Path.GetFileName(entry).StartsWith(".mdir-rename-"));
    }
}
