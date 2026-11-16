using MDir.Features;
using MDir.Integration;

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
    private const string AppearanceSection = "Appearance";
    private const string ToolbarSection = "Toolbar";
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

    /// <summary>The status bar shows the size of the selected folders without being asked.</summary>
    public bool AutoFolderSize { get; set; }

    /// <summary>The Size column of the file lists also shows the size of folders (worked out in the background).</summary>
    public bool FolderSizeColumn { get; set; }

    /// <summary>The list tints the column it is sorted by (always off in dark mode).</summary>
    public bool SortColumnHighlight { get; set; } = true;

    /// <summary>Only one window runs; opening a folder again hands it to that window (needs a restart to take effect).</summary>
    public bool SingleInstance { get; set; }

    /// <summary>Minimizing hides the window and leaves an icon in the notification area.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Language of the menus and dialogs: "ko", "en", or the name of a language pack file. Defaults to Korean on a Korean Windows, English elsewhere.</summary>
    public string Language { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";

    /// <summary>Light, dark, or follow Windows.</summary>
    public AppColorMode ColorMode { get; set; } = AppColorMode.System;

    /// <summary>Size of menus, toolbar, tabs and address bars in percent of the normal size (100, 125, 150, 200).</summary>
    public int UiScale { get; set; } = 100;

    /// <summary>Command ids of the toolbar buttons in order ("-" = separator, "@layouts" = the layout drop-down); empty = the default toolbar.</summary>
    public List<string> ToolbarItems { get; } = [];

    /// <summary>
    /// An external M-Finder to use instead of the built-in search: its exe, or a source folder that holds
    /// <c>mfinder\__main__.py</c>. Empty = the built-in search.
    /// </summary>
    public string FinderPath
    {
        get => Finder.ExternalPath;
        set => Finder.ExternalPath = value;
    }

    /// <summary>The built-in M-Finder search: what it indexes and how.</summary>
    public FinderOptions Finder { get; set; } = new();

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
                AutoFolderSize = ini.GetBool(StartSection, nameof(AutoFolderSize), false),
                SingleInstance = ini.GetBool(StartSection, nameof(SingleInstance), false),
                SortColumnHighlight = ini.GetBool(StartSection, nameof(SortColumnHighlight), true),
                FolderSizeColumn = ini.GetBool(StartSection, nameof(FolderSizeColumn), false),
                Language = ini.Get(AppearanceSection, nameof(Language)) is { Length: > 0 } language && language.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
                    ? language
                    : new AppSettings().Language,
                MinimizeToTray = ini.GetBool(StartSection, nameof(MinimizeToTray), false),
                ColorMode = Enum.TryParse(ini.Get(AppearanceSection, nameof(ColorMode)), true, out AppColorMode mode) && Enum.IsDefined(mode) ? mode : AppColorMode.System,
                UiScale = AppAppearance.NormalizeScale(ini.GetInt(AppearanceSection, nameof(UiScale), 100)),
                Finder = FinderOptions.Read(ini),
            };
            if (ini.Get(ToolbarSection, "Items") is { Length: > 0 } toolbar)
                settings.ToolbarItems.AddRange(toolbar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
        ini.Set(StartSection, nameof(AutoFolderSize), AutoFolderSize);
        ini.Set(StartSection, nameof(SingleInstance), SingleInstance);
        ini.Set(StartSection, nameof(SortColumnHighlight), SortColumnHighlight);
        ini.Set(StartSection, nameof(FolderSizeColumn), FolderSizeColumn);
        ini.Set(StartSection, nameof(MinimizeToTray), MinimizeToTray);
        ini.Set(AppearanceSection, nameof(ColorMode), ColorMode.ToString());
        ini.Set(AppearanceSection, nameof(Language), Language);
        ini.Set(AppearanceSection, nameof(UiScale), UiScale);
        Finder.Write(ini);
        if (ToolbarItems.Count > 0)
            ini.Set(ToolbarSection, "Items", string.Join(',', ToolbarItems));
        else
            ini.RemoveSection(ToolbarSection);

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
