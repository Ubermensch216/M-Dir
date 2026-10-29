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

    /// <summary>Save the workspace on exit and restore it at the next start (Q-Dir's <c>start.qdr</c> behaviour).</summary>
    public bool AutoWorkspace { get; set; } = true;

    /// <summary>Reads the file; a missing or unreadable file gives the defaults.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            var ini = IniFile.Load(path);
            return new AppSettings { AutoWorkspace = ini.GetBool(StartSection, nameof(AutoWorkspace), true) };
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
