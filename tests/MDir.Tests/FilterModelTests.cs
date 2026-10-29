using MDir.Features;
using MDir.Settings;

namespace MDir.Tests;

public class WildcardMatcherTests
{
    [Theory]
    [InlineData("*.txt", "report.TXT", true)]
    [InlineData("*.txt", "report.txt.bak", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("*", "", true)]
    [InlineData("*", "anything", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*a*", "banana", true)]
    [InlineData("thumbs.db", "Thumbs.DB", true)]
    [InlineData("*한글*", "내 한글 문서.hwp", true)]
    [InlineData("**x", "abx", true)]
    public void IsMatch_FollowsFileNameWildcards(string pattern, string name, bool expected) =>
        Assert.Equal(expected, WildcardMatcher.IsMatch(pattern, name));

    [Fact]
    public void SplitPatterns_AcceptsSemicolonAndCommaAndTrims() =>
        Assert.Equal(["*.txt", "*.doc", "a b"], WildcardMatcher.SplitPatterns(" *.txt ;*.doc,, a b ;"));
}

public class FileFilterTests
{
    private static bool Keeps(FileFilter filter, string name, bool isFolder = false) => filter.Compile()!(name, isFolder);

    [Fact]
    public void EmptyFilter_IsInactiveAndCompilesToNothing()
    {
        Assert.False(FileFilter.None.IsActive);
        Assert.Null(FileFilter.None.Compile());
        Assert.Null(new FileFilter(" ; , ").Compile());
    }

    [Fact]
    public void Include_KeepsMatchingNames()
    {
        var filter = new FileFilter("*.txt;*.md");

        Assert.True(Keeps(filter, "a.txt"));
        Assert.True(Keeps(filter, "README.MD"));
        Assert.False(Keeps(filter, "a.exe"));
    }

    [Fact]
    public void AutoWildcard_WrapsASinglePlainWordOnly()
    {
        Assert.True(Keeps(new FileFilter("port"), "report.docx"));
        Assert.False(Keeps(new FileFilter("port", AutoWildcard: false), "report.docx"));
        Assert.True(Keeps(new FileFilter("report.docx", AutoWildcard: false), "REPORT.docx"));
        Assert.False(Keeps(new FileFilter("a;b"), "banana"), "two plain words are exact names, not substrings");
        Assert.True(Keeps(new FileFilter("*.log"), "x.log"));
        Assert.False(Keeps(new FileFilter("*.log"), "x.log.old"), "a pattern with a wildcard is not wrapped");
    }

    [Fact]
    public void Negative_HidesMatchingNames()
    {
        var filter = new FileFilter("Thumbs.db;*.bak", Negative: true);

        Assert.False(Keeps(filter, "Thumbs.db"));
        Assert.False(Keeps(filter, "old.BAK"));
        Assert.True(Keeps(filter, "keep.txt"));
    }

    [Fact]
    public void Folders_AreAlwaysShownUnlessFilterFoldersIsOn()
    {
        var plain = new FileFilter("*.txt");
        var withFolders = plain with { FilterFolders = true };
        var excludeFolders = new FileFilter("src", Negative: true, FilterFolders: true);

        Assert.True(Keeps(plain, "docs", isFolder: true));
        Assert.False(Keeps(withFolders, "docs", isFolder: true));
        Assert.True(Keeps(withFolders, "notes.txt", isFolder: true));
        Assert.False(Keeps(excludeFolders, "src", isFolder: true));
        Assert.True(Keeps(new FileFilter("src", Negative: true), "src", isFolder: true));
    }
}

public class ColorRuleSetTests
{
    private static ColorStyle? Resolve(ColorRuleSet rules, string name, bool folder = false, bool hidden = false) =>
        rules.Resolve(name, folder, hidden);

    [Fact]
    public void Default_ColorsExecutablesRedAndFoldersGreen()
    {
        ColorRuleSet rules = ColorRuleSet.CreateDefault();

        Assert.Equal(Color.FromArgb(255, 0, 0), Resolve(rules, "setup.EXE")!.Value.Text);
        Assert.Equal(Color.FromArgb(0, 0x88, 0), Resolve(rules, "Documents", folder: true)!.Value.Text);
        Assert.Equal(Color.FromArgb(0, 0xAA, 0), Resolve(rules, "backup.7z")!.Value.Text);
        Assert.Null(Resolve(rules, "notes.txt"));
        Assert.Equal(Color.FromArgb(128, 128, 128), Resolve(rules, "secret.txt", hidden: true)!.Value.Text);
    }

    [Fact]
    public void FirstMatchingRuleSetsEachAttribute_LaterRulesFillTheRest()
    {
        var rules = new ColorRuleSet(
        [
            new ColorRule("#Hidden", Background: Color.LightGray),
            new ColorRule("*.exe", Text: Color.Red),
            new ColorRule("*", Text: Color.Blue, Bold: true),
        ]);

        // A hidden .exe: background from the first rule, text from the second, bold from the catch-all.
        ColorStyle hiddenExe = Resolve(rules, "x.exe", hidden: true)!.Value;
        Assert.Equal(Color.LightGray, hiddenExe.Background);
        Assert.Equal(Color.Red, hiddenExe.Text);
        Assert.True(hiddenExe.Bold);

        ColorStyle plain = Resolve(rules, "x.txt")!.Value;
        Assert.Equal(Color.Blue, plain.Text);
        Assert.Null(plain.Background);
    }

    [Fact]
    public void DisabledRulesAndRulesWithoutAnyColor_DoNothing()
    {
        var rules = new ColorRuleSet(
        [
            new ColorRule("*.exe", Enabled: false, Text: Color.Red),
            new ColorRule("*.dll"),
        ]);

        Assert.Null(Resolve(rules, "a.exe"));
        Assert.Null(Resolve(rules, "a.dll"));
    }

    [Fact]
    public void BackgroundRules_AreColoursOfTheListNotOfItems()
    {
        var rules = new ColorRuleSet(
        [
            new ColorRule("#BG", Text: Color.White),
            new ColorRule("#BG-A", Text: Color.Beige),
            new ColorRule("*", Text: Color.Black),
        ]);

        Assert.Equal(Color.White, rules.InactiveBackground);
        Assert.Equal(Color.Beige, rules.ActiveBackground);
        Assert.Equal(Color.Black, Resolve(rules, "#BG")!.Value.Text);
        Assert.Null(ColorRuleSet.CreateDefault().ActiveBackground);
    }

    [Fact]
    public void OwnFormat_RoundTripsThroughIni()
    {
        var original = new ColorRuleSet(
        [
            new ColorRule("*.a=b;*.c", Text: Color.FromArgb(1, 2, 3), Background: Color.FromArgb(250, 251, 252), Bold: true),
            new ColorRule("#DIR", Enabled: false),
        ]);
        var ini = new IniFile();
        original.Write(ini);

        ColorRuleSet loaded = ColorRuleSet.Read(IniFile.Parse(ini.ToText()))!;

        Assert.Equal(original.Rules, loaded.Rules);
        Assert.Null(ColorRuleSet.Read(new IniFile()));
    }

    [Fact]
    public void QDirFormat_ImportsPatternEnabledAndTextColour()
    {
        var ini = IniFile.Parse("""
            [Filter2]
            0=#Hidden=1=-1=1=1=-1=0
            1=*.zip;*.rar=1=00AA00=-1=1=-1=0
            2=*.exe;*.dll;*.bat=0=FF0000=-1=1=-1=0
            3=*=1=0000BB=-1=1=-1=0
            """);

        ColorRuleSet rules = ColorRuleSet.ImportQDir(ini)!;

        Assert.Equal(4, rules.Rules.Count);
        Assert.Equal(new ColorRule("#Hidden", true, null), rules.Rules[0]);
        Assert.Equal(new ColorRule("*.zip;*.rar", true, Color.FromArgb(0, 0xAA, 0)), rules.Rules[1]);
        Assert.False(rules.Rules[2].Enabled);
        Assert.Equal(Color.FromArgb(0, 0, 0xBB), rules.Rules[3].Text);
        Assert.Null(ColorRuleSet.ImportQDir(new IniFile()));
    }

    [Fact]
    public void QDirExport_CanBeImportedAgain()
    {
        ColorRuleSet defaults = ColorRuleSet.CreateDefault();

        ColorRuleSet imported = ColorRuleSet.ImportQDir(IniFile.Parse(defaults.ExportQDir().ToText()))!;

        Assert.Equal(defaults.Rules.Select(r => (r.Pattern, r.Enabled, r.Text)), imported.Rules.Select(r => (r.Pattern, r.Enabled, r.Text)));
    }

    [Theory]
    [InlineData("FF8000", 255, 128, 0)]
    [InlineData("00aa00", 0, 170, 0)]
    public void ParseColor_ReadsRrggbb(string text, int r, int g, int b) =>
        Assert.Equal(Color.FromArgb(r, g, b), ColorRuleSet.ParseColor(text));

    [Theory]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12345")]
    [InlineData("GGGGGG")]
    public void ParseColor_TreatsOtherTextAsNotSet(string? text) => Assert.Null(ColorRuleSet.ParseColor(text));
}
