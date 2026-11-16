namespace MDir.Settings;

/// <summary>Whether the built-in search may start an elevated helper to read the NTFS MFT (one UAC prompt per run).</summary>
internal enum FinderMftConsent
{
    /// <summary>Ask the first time an index is built or loaded.</summary>
    Ask,
    Allow,
    Deny,
}

/// <summary>The built-in M-Finder search's options (the <c>[Finder]</c> section of <c>M-Dir.ini</c>).</summary>
internal sealed class FinderOptions
{
    private const string Section = "Finder";
    private const int MaxListed = 200;

    /// <summary>Folders and drives to index; empty = every fixed drive.</summary>
    public List<string> Roots { get; set; } = [];

    /// <summary>Read NTFS drives through their MFT (needs the elevated helper unless M-Dir runs as administrator) instead of walking them.</summary>
    public bool UseMft { get; set; } = true;

    public FinderMftConsent MftConsent { get; set; } = FinderMftConsent.Ask;

    /// <summary>Keep the index current from the USN journal / file system notifications.</summary>
    public bool Monitor { get; set; } = true;

    /// <summary>Add up folder sizes in the background (<c>folder: size:&gt;1gb</c>).</summary>
    public bool FolderSizes { get; set; } = true;

    public bool ExcludeHidden { get; set; }

    public bool ExcludeSystem { get; set; }

    public List<string> ExcludeFolders { get; set; } = [];

    /// <summary>Wildcard patterns on file names, e.g. <c>*.tmp</c>.</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    /// <summary><c>content:</c> skips files bigger than this (MB).</summary>
    public int ContentMaxMb { get; set; } = 64;

    /// <summary>Where the index file is kept; empty = in M-Dir's data folder.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>An external M-Finder (its exe, or a source folder) that is used only while <see cref="UseExternal"/> is on.</summary>
    public string ExternalPath { get; set; } = string.Empty;

    /// <summary>Search through the external M-Finder at <see cref="ExternalPath"/> instead of the one built into M-Dir. Off by default.</summary>
    public bool UseExternal { get; set; }

    /// <summary>The external M-Finder is chosen and has a path.</summary>
    public bool UsesExternal => UseExternal && ExternalPath.Trim().Length > 0;

    /// <summary>Everything that decides what the index holds; a saved index is reused only when this is unchanged.</summary>
    public string Signature => string.Join('\n',
        "roots:" + string.Join('|', Roots.Select(r => r.TrimEnd('\\', '/').ToLowerInvariant()).Order()),
        "mft:" + (UseMft ? "1" : "0"),
        "hidden:" + (ExcludeHidden ? "1" : "0"),
        "system:" + (ExcludeSystem ? "1" : "0"),
        "folders:" + string.Join('|', ExcludeFolders.Select(r => r.TrimEnd('\\', '/').ToLowerInvariant()).Order()),
        "patterns:" + string.Join('|', ExcludePatterns.Select(p => p.ToLowerInvariant()).Order()));

    public FinderOptions Clone() => new()
    {
        Roots = [.. Roots],
        UseMft = UseMft,
        MftConsent = MftConsent,
        Monitor = Monitor,
        FolderSizes = FolderSizes,
        ExcludeHidden = ExcludeHidden,
        ExcludeSystem = ExcludeSystem,
        ExcludeFolders = [.. ExcludeFolders],
        ExcludePatterns = [.. ExcludePatterns],
        ContentMaxMb = ContentMaxMb,
        DatabasePath = DatabasePath,
        ExternalPath = ExternalPath,
        UseExternal = UseExternal,
    };

    public static FinderOptions Read(IniFile ini)
    {
        var options = new FinderOptions
        {
            UseMft = ini.GetBool(Section, "UseMft", true),
            Monitor = ini.GetBool(Section, "Monitor", true),
            FolderSizes = ini.GetBool(Section, "FolderSizes", true),
            ExcludeHidden = ini.GetBool(Section, "ExcludeHidden", false),
            ExcludeSystem = ini.GetBool(Section, "ExcludeSystem", false),
            ContentMaxMb = Math.Clamp(ini.GetInt(Section, "ContentMaxMb", 64), 1, 4096),
            DatabasePath = ini.Get(Section, "Database") ?? string.Empty,
            ExternalPath = ini.Get(Section, "Path") ?? string.Empty,
            UseExternal = ini.GetBool(Section, "UseExternal", false),
            MftConsent = Enum.TryParse(ini.Get(Section, "MftConsent"), true, out FinderMftConsent consent) && Enum.IsDefined(consent) ? consent : FinderMftConsent.Ask,
        };
        options.Roots = ReadList(ini, "Root");
        options.ExcludeFolders = ReadList(ini, "ExcludeFolder");
        options.ExcludePatterns = ReadList(ini, "ExcludePattern");
        return options;
    }

    private static List<string> ReadList(IniFile ini, string prefix)
    {
        var list = new List<string>();
        for (int i = 1; i <= MaxListed; i++)
        {
            if (ini.Get(Section, prefix + i) is { Length: > 0 } value)
                list.Add(value);
        }

        return list;
    }

    public void Write(IniFile ini)
    {
        ini.RemoveSection(Section);
        FinderOptions defaults = new();
        if (ExternalPath.Length > 0)
            ini.Set(Section, "Path", ExternalPath);
        if (UseExternal)
            ini.Set(Section, "UseExternal", true);
        if (UseMft != defaults.UseMft)
            ini.Set(Section, "UseMft", UseMft);
        if (Monitor != defaults.Monitor)
            ini.Set(Section, "Monitor", Monitor);
        if (FolderSizes != defaults.FolderSizes)
            ini.Set(Section, "FolderSizes", FolderSizes);
        if (ExcludeHidden)
            ini.Set(Section, "ExcludeHidden", true);
        if (ExcludeSystem)
            ini.Set(Section, "ExcludeSystem", true);
        if (ContentMaxMb != defaults.ContentMaxMb)
            ini.Set(Section, "ContentMaxMb", ContentMaxMb);
        if (DatabasePath.Length > 0)
            ini.Set(Section, "Database", DatabasePath);
        if (MftConsent != FinderMftConsent.Ask)
            ini.Set(Section, "MftConsent", MftConsent.ToString());
        WriteList(ini, "Root", Roots);
        WriteList(ini, "ExcludeFolder", ExcludeFolders);
        WriteList(ini, "ExcludePattern", ExcludePatterns);
    }

    private static void WriteList(IniFile ini, string prefix, List<string> values)
    {
        for (int i = 0; i < Math.Min(values.Count, MaxListed); i++)
            ini.Set(Section, prefix + (i + 1), values[i]);
    }
}
