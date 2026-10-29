using System.Text;
using MDir.Settings;

namespace MDir.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdir-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class IniFileTests
{
    [Fact]
    public void Parse_ReadsSectionsKeysAndKeepsEqualsSignsInValues()
    {
        var ini = IniFile.Parse("""
            ; comment
            [Start]
            Language = 1042
            Path=C:\a=b
            # another comment

            [Other]
            key=value
            """);

        Assert.Equal("1042", ini.Get("start", "language"));
        Assert.Equal(@"C:\a=b", ini.Get("Start", "Path"));
        Assert.Equal("value", ini.Get("OTHER", "KEY"));
        Assert.Null(ini.Get("Start", "missing"));
        Assert.Null(ini.Get("missing", "key"));
        Assert.Equal(["Start", "Other"], ini.SectionNames);
    }

    [Fact]
    public void TypedGetters_FallBackOnBadValues()
    {
        var ini = IniFile.Parse("[S]\nn=42\nbad=x\nyes=1\nno=0\nword=true\nmaybe=?\n");

        Assert.Equal(42, ini.GetInt("S", "n", -1));
        Assert.Equal(-1, ini.GetInt("S", "bad", -1));
        Assert.Equal(-1, ini.GetInt("S", "absent", -1));
        Assert.True(ini.GetBool("S", "yes", false));
        Assert.False(ini.GetBool("S", "no", true));
        Assert.True(ini.GetBool("S", "word", false));
        Assert.True(ini.GetBool("S", "maybe", true));
    }

    [Fact]
    public void SetAndToText_KeepOrderAndReplaceValues()
    {
        var ini = new IniFile();
        ini.Set("B", "x", "1");
        ini.Set("A", "y", 2);
        ini.Set("B", "z", true);
        ini.Set("b", "X", "changed");

        Assert.Equal("[B]\r\nx=changed\r\nz=1\r\n\r\n[A]\r\ny=2\r\n", ini.ToText());
    }

    [Theory]
    [InlineData("S", "k", "line1\nline2")]
    [InlineData("S", "a=b", "v")]
    [InlineData("S", "", "v")]
    [InlineData("", "k", "v")]
    [InlineData("S]", "k", "v")]
    public void Set_RejectsValuesThatCannotBeStored(string section, string key, string value) =>
        Assert.Throws<ArgumentException>(() => new IniFile().Set(section, key, value));

    [Fact]
    public void Save_WritesUtf16WithBomAndLoadReadsItBack()
    {
        using var dir = new TempDirectory();
        string path = dir.File(@"sub\settings.ini");
        var ini = new IniFile();
        ini.Set("Start", "Name", "한글 값");
        ini.Save(path);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("한글 값", IniFile.Load(path).Get("Start", "Name"));

        ini.Set("Start", "Name", "다시");
        ini.Save(path);
        Assert.Equal("다시", IniFile.Load(path).Get("Start", "Name"));
    }

    [Fact]
    public void Load_ReadsUtf8FilesAndMissingFilesAreEmpty()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("u8.ini"), "[S]\nk=값\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("값", IniFile.Load(dir.File("u8.ini")).Get("S", "k"));
        Assert.Empty(IniFile.Load(dir.File("absent.ini")).SectionNames);
    }
}

public class IdListTextTests
{
    [Fact]
    public void Encode_UsesPercentHexPerByteAndRoundTrips()
    {
        byte[] bytes = [0x00, 0x0F, 0xAB, 0xFF];

        string text = IdListText.Encode(bytes);

        Assert.Equal("%00%0F%AB%FF", text);
        Assert.Equal(bytes, IdListText.Decode(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%0")]
    [InlineData("%0G")]
    [InlineData("0A0")]
    [InlineData("%00%1")]
    [InlineData("%+1")]
    public void Decode_RejectsMalformedText(string? text) => Assert.Null(IdListText.Decode(text));
}

public class AppPathsTests
{
    [Fact]
    public void Resolve_UsesRoamingAppDataUnlessAnIniSitsBesideTheProgram()
    {
        using var app = new TempDirectory();
        using var roaming = new TempDirectory();

        AppPaths installed = AppPaths.Resolve(app.Path, roaming.Path);
        Assert.False(installed.IsPortable);
        Assert.Equal(Path.Combine(roaming.Path, "M-Dir"), installed.DataDirectory);
        Assert.Equal(Path.Combine(roaming.Path, "M-Dir", "start.mdr"), installed.StartWorkspaceFile);

        File.WriteAllText(app.File("M-Dir.ini"), "");
        AppPaths portable = AppPaths.Resolve(app.Path, roaming.Path);
        Assert.True(portable.IsPortable);
        Assert.Equal(app.Path, portable.DataDirectory);
        Assert.Equal(app.File("M-Dir.ini"), portable.SettingsFile);
    }

    [Fact]
    public void AppSettings_DefaultsOnSaveKeepsOtherKeys()
    {
        using var dir = new TempDirectory();
        string path = dir.File("M-Dir.ini");
        Assert.True(AppSettings.Load(path).AutoWorkspace);

        var ini = new IniFile();
        ini.Set("Other", "keep", "me");
        ini.Save(path);
        new AppSettings { AutoWorkspace = false }.Save(path);

        Assert.False(AppSettings.Load(path).AutoWorkspace);
        Assert.Equal("me", IniFile.Load(path).Get("Other", "keep"));
    }
}
