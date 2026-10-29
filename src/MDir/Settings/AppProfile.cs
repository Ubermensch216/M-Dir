using MDir.Features;

namespace MDir.Settings;

/// <summary>
/// Where M-Dir keeps its files. Portable mode (an <c>M-Dir.ini</c> next to the executable) keeps everything
/// beside the program and leaves the registry and profile alone; otherwise files go to <c>%APPDATA%\M-Dir</c>.
/// </summary>
internal sealed class AppPaths
{
    public const string SettingsFileName = "M-Dir.ini";
    public const string StartWorkspaceFileName = "start.mdr";
    public const string WorkspaceExtension = ".mdr";

    private AppPaths(string dataDirectory, bool isPortable)
    {
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
    }

    public string DataDirectory { get; }

    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(DataDirectory, SettingsFileName);

    /// <summary>Folder of favorite shortcuts (<c>.lnk</c>), with a "Quick-Link" subfolder.</summary>
    public string FavoritesDirectory => Path.Combine(DataDirectory, "Favorites");

    /// <summary>Workspace saved when the program exits and restored at the next start.</summary>
    public string StartWorkspaceFile => Path.Combine(DataDirectory, StartWorkspaceFileName);

    public static AppPaths Resolve(string? appDirectory = null, string? roamingAppData = null)
    {
        appDirectory ??= AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(appDirectory, SettingsFileName)))
            return new AppPaths(appDirectory, isPortable: true);

        roamingAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new AppPaths(Path.Combine(roamingAppData, "M-Dir"), isPortable: false);
    }
}

/// <summary>Options stored in <c>M-Dir.ini</c>.</summary>
internal sealed class AppSettings
{
    private const string StartSection = "Start";
    private const string ColorSection = "ColorFilter";
    private const string ShortcutSection = "Shortcuts";
    private const string RecentSection = "RecentFilters";
    private const int MaxRecentFilters = 20;

    /// <summary>Save the workspace on exit and restore it at the next start (Q-Dir's <c>start.qdr</c> behaviour).</summary>
    public bool AutoWorkspace { get; set; } = true;

    /// <summary>Folder tree look and behaviour, shared by every tree.</summary>
    public TreeOptions Tree { get; private set; } = new();

    /// <summary>Colour the file lists by <see cref="ColorRules"/>.</summary>
    public bool ColorFilterEnabled { get; set; } = true;

    /// <summary>The colour rules (Q-Dir's colour filter), shared by every pane.</summary>
    public ColorRuleSet ColorRules { get; set; } = ColorRuleSet.CreateDefault();

    /// <summary>The user's keyboard shortcut changes: command id to key text (empty = no shortcut).</summary>
    public Dictionary<string, string> Shortcuts { get; } = new(StringComparer.Ordinal);

    /// <summary>The toolbar under the menu is shown.</summary>
    public bool ShowToolbar { get; set; } = true;

    /// <summary>File name filters typed before, newest first.</summary>
    public List<string> RecentFilters { get; } = [];

    /// <summary>Puts <paramref name="pattern"/> at the front of <see cref="RecentFilters"/> (once, at most 20 kept).</summary>
    public void AddRecentFilter(string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return;

        RecentFilters.RemoveAll(existing => string.Equals(existing, pattern, StringComparison.OrdinalIgnoreCase));
        RecentFilters.Insert(0, pattern);
        if (RecentFilters.Count > MaxRecentFilters)
            RecentFilters.RemoveRange(MaxRecentFilters, RecentFilters.Count - MaxRecentFilters);
    }

    /// <summary>Reads the file; a missing or unreadable file gives the defaults.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            var ini = IniFile.Load(path);
            var settings = new AppSettings
            {
                AutoWorkspace = ini.GetBool(StartSection, nameof(AutoWorkspace), true),
                Tree = TreeOptions.Read(ini),
                ColorFilterEnabled = ini.GetBool(ColorSection, "Enabled", true),
                ColorRules = ColorRuleSet.Read(ini) ?? ColorRuleSet.CreateDefault(),
                ShowToolbar = ini.GetBool(StartSection, nameof(ShowToolbar), true),
            };
            foreach ((string id, string keys) in ini.Entries(ShortcutSection))
                settings.Shortcuts[id] = keys;

            for (int i = 1; i <= MaxRecentFilters; i++)
            {
                if (ini.Get(RecentSection, $"Filter{i}") is { Length: > 0 } filter)
                    settings.RecentFilters.Add(filter);
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Updates this object's keys and keeps any other keys already in the file.</summary>
    public void Save(string path)
    {
        var ini = IniFile.Load(path);
        ini.Set(StartSection, nameof(AutoWorkspace), AutoWorkspace);
        ini.Set(StartSection, nameof(ShowToolbar), ShowToolbar);

        // Only the changes are stored, so a default changed in a later version still reaches users who never touched it.
        ini.RemoveSection(ShortcutSection);
        foreach ((string id, string keys) in Shortcuts)
            ini.Set(ShortcutSection, id, keys);

        Tree.Write(ini);
        ini.Set(ColorSection, "Enabled", ColorFilterEnabled);
        ColorRules.Write(ini);
        ini.Set(RecentSection, "Count", RecentFilters.Count);
        for (int i = 0; i < MaxRecentFilters; i++)
            ini.Set(RecentSection, $"Filter{i + 1}", i < RecentFilters.Count ? RecentFilters[i] : string.Empty);
        ini.Save(path);
    }
}

/// <summary>Resolved file locations plus the loaded settings for this run.</summary>
internal sealed class AppProfile(AppPaths paths, AppSettings settings)
{
    public AppPaths Paths { get; } = paths;

    public AppSettings Settings { get; } = settings;

    public static AppProfile Load()
    {
        AppPaths paths = AppPaths.Resolve();
        return new AppProfile(paths, AppSettings.Load(paths.SettingsFile));
    }

    public void SaveSettings() => Settings.Save(Paths.SettingsFile);
}
