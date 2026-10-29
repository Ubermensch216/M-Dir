using MDir.Commands;
using MDir.Settings;

namespace MDir.Tests;

public class CommandRegistryTests
{
    private static Command Make(string id, Keys[]? defaults = null, bool native = false, Func<bool>? can = null) =>
        new(id, "&" + id, () => { }) { DefaultShortcuts = defaults ?? [], NativeShortcut = native, CanExecute = can };

    [Fact]
    public void Add_RejectsDuplicateIdsAndGetFindsCommands()
    {
        var registry = new CommandRegistry();
        Command a = registry.Add(Make("a"));

        Assert.Same(a, registry.Get("a"));
        Assert.True(registry.TryGet("a", out _));
        Assert.False(registry.TryGet("b", out _));
        Assert.Throws<ArgumentException>(() => registry.Add(Make("a")));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("missing"));
        Assert.Equal(["a"], registry.All.Select(c => c.Id));
    }

    [Fact]
    public void FindByShortcut_ReturnsTheBoundCommandIncludingAlternatives()
    {
        var registry = new CommandRegistry();
        Command address = registry.Add(Make("go.address", [Keys.F4, Keys.Alt | Keys.D, Keys.Control | Keys.L]));

        Assert.Same(address, registry.FindByShortcut(Keys.F4));
        Assert.Same(address, registry.FindByShortcut(Keys.Control | Keys.L));
        Assert.Null(registry.FindByShortcut(Keys.F5));
        Assert.Equal("F4", registry.ShortcutText(address));
    }

    [Fact]
    public void NativeDefaults_AreNotInterceptedUntilTheUserRebindsThem()
    {
        var registry = new CommandRegistry();
        Command copy = registry.Add(Make("edit.copy", [Keys.Control | Keys.C], native: true));

        // Ctrl+C belongs to the focused text box or shell view; only the menu shows it.
        Assert.Null(registry.FindByShortcut(Keys.Control | Keys.C));
        Assert.Equal("Ctrl+C", registry.ShortcutText(copy));

        registry.SetShortcuts("edit.copy", [Keys.Control | Keys.Alt | Keys.C]);
        Assert.Same(copy, registry.FindByShortcut(Keys.Control | Keys.Alt | Keys.C));
        Assert.Null(registry.FindByShortcut(Keys.Control | Keys.C));

        registry.ResetShortcuts("edit.copy");
        Assert.Null(registry.FindByShortcut(Keys.Control | Keys.Alt | Keys.C));
        Assert.False(registry.IsCustomized("edit.copy"));
    }

    [Fact]
    public void SetShortcuts_ToTheDefaultsIsNotACustomisation_AndEmptyRemovesTheShortcut()
    {
        var registry = new CommandRegistry();
        Command tab = registry.Add(Make("tab.new", [Keys.Control | Keys.T]));

        registry.SetShortcuts("tab.new", [Keys.Control | Keys.T]);
        Assert.False(registry.IsCustomized("tab.new"));

        registry.SetShortcuts("tab.new", []);
        Assert.True(registry.IsCustomized("tab.new"));
        Assert.Null(registry.FindByShortcut(Keys.Control | Keys.T));
        Assert.Equal(string.Empty, registry.ShortcutText(tab));

        registry.ResetAllShortcuts();
        Assert.Same(tab, registry.FindByShortcut(Keys.Control | Keys.T));
    }

    [Fact]
    public void FindConflict_SeesOtherCommandsKeysEvenNativeOnes()
    {
        var registry = new CommandRegistry();
        registry.Add(Make("edit.copy", [Keys.Control | Keys.C], native: true));
        registry.Add(Make("tab.new", [Keys.Control | Keys.T]));

        Assert.Equal("edit.copy", registry.FindConflict(Keys.Control | Keys.C, "tab.new")!.Id);
        Assert.Null(registry.FindConflict(Keys.Control | Keys.C, "edit.copy")); // a command does not conflict with itself
        Assert.Null(registry.FindConflict(Keys.Control | Keys.Q, "tab.new"));
    }

    [Theory]
    [InlineData(Keys.Control | Keys.T, "Ctrl+T")]
    [InlineData(Keys.Control | Keys.Shift | Keys.F, "Ctrl+Shift+F")]
    [InlineData(Keys.Alt | Keys.Left, "Alt+Left")]
    [InlineData(Keys.Control | Keys.Alt | Keys.D1, "Ctrl+Alt+1")]
    [InlineData(Keys.F12, "F12")]
    [InlineData(Keys.Shift | Keys.Delete, "Shift+Del")]
    public void KeyText_RoundTrips(Keys keys, string text)
    {
        Assert.Equal(text, CommandRegistry.FormatKeys(keys));
        Assert.True(CommandRegistry.TryParseKeys(text, out Keys parsed));
        Assert.Equal(keys, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Banana")]
    [InlineData("Ctrl")]
    public void UnreadableKeyText_IsRejected(string text) => Assert.False(CommandRegistry.TryParseKeys(text, out _));

    [Fact]
    public void ExportAndImport_KeepOnlyChangesAndIgnoreUnknownThings()
    {
        var registry = new CommandRegistry();
        registry.Add(Make("a", [Keys.F1]));
        registry.Add(Make("b", [Keys.F2]));
        registry.SetShortcuts("a", [Keys.Control | Keys.Alt | Keys.A, Keys.F9]);
        registry.SetShortcuts("b", []);

        IReadOnlyDictionary<string, string> exported = registry.ExportCustom();
        Assert.Equal("Ctrl+Alt+A, F9", exported["a"]);
        Assert.Equal(string.Empty, exported["b"]);

        var fresh = new CommandRegistry();
        fresh.Add(Make("a", [Keys.F1]));
        fresh.Add(Make("b", [Keys.F2]));
        fresh.ImportCustom(new Dictionary<string, string>(exported) { ["gone"] = "F3", ["a2"] = "x" });

        Assert.Equal([Keys.Control | Keys.Alt | Keys.A, Keys.F9], fresh.GetShortcuts(fresh.Get("a")));
        Assert.Empty(fresh.GetShortcuts(fresh.Get("b")));

        fresh.ImportCustom(new Dictionary<string, string> { ["a"] = "Banana, F5" });
        Assert.Equal([Keys.F5], fresh.GetShortcuts(fresh.Get("a")));
    }

    [Fact]
    public void Command_IsEnabledFollowsCanExecute_AndDisplayTextDropsTheAccessKey()
    {
        bool allowed = false;
        var command = new Command("x", "새 폴더(&N)...", () => { }) { CanExecute = () => allowed };

        Assert.False(command.IsEnabled);
        allowed = true;
        Assert.True(command.IsEnabled);
        Assert.Equal("새 폴더", command.DisplayText);
        Assert.Equal("Copy", new Command("c", "&Copy", () => { }).DisplayText);
        Assert.True(Make("y").IsEnabled);
    }
}

public class ShortcutSettingsTests
{
    [Fact]
    public void Shortcuts_AreSavedAsChangesOnly_AndRemovedKeysDisappear()
    {
        using var dir = new TempDirectory();
        string path = dir.File("M-Dir.ini");
        var settings = new AppSettings { ShowToolbar = false };
        settings.Shortcuts["tab.new"] = "Ctrl+Alt+N";
        settings.Shortcuts["edit.copy"] = string.Empty;
        settings.Save(path);

        AppSettings loaded = AppSettings.Load(path);
        Assert.False(loaded.ShowToolbar);
        Assert.Equal("Ctrl+Alt+N", loaded.Shortcuts["tab.new"]);
        Assert.Equal(string.Empty, loaded.Shortcuts["edit.copy"]);

        loaded.Shortcuts.Remove("tab.new");
        loaded.Save(path);
        AppSettings again = AppSettings.Load(path);
        Assert.False(again.Shortcuts.ContainsKey("tab.new"));
        Assert.True(again.Shortcuts.ContainsKey("edit.copy"));

        Assert.True(AppSettings.Load(dir.File("absent.ini")).ShowToolbar);
    }

    [Fact]
    public void IniFile_RemoveSectionAndEntries()
    {
        var ini = IniFile.Parse("[A]\nx=1\ny=2\n[B]\nz=3\n");

        Assert.Equal([("x", "1"), ("y", "2")], ini.Entries("a"));
        Assert.Empty(ini.Entries("missing"));

        ini.RemoveSection("A");
        Assert.False(ini.HasSection("A"));
        Assert.True(ini.HasSection("B"));
        ini.RemoveSection("nothing");
    }
}
