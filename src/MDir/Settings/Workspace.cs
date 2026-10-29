using System.Globalization;
using MDir.Features;
using MDir.Layout;
using MDir.Shell;

namespace MDir.Settings;

/// <summary>One tab of a saved pane.</summary>
internal sealed record TabState(ShellLocation Location, FolderViewMode ViewMode, int IconSize);

/// <summary>
/// A saved pane: its tabs, which one was selected, whether its own folder tree is on (and how wide), and its
/// name filter (null = none) with the filter bar's visibility.
/// </summary>
internal sealed record PaneState(
    IReadOnlyList<TabState> Tabs,
    int SelectedTab,
    bool TreeVisible = false,
    int TreeSplit = Workspace.DefaultTreeSplit,
    FileFilter? Filter = null,
    bool FilterBarVisible = false);

/// <summary>Window position and size in screen pixels (the restored rectangle when maximized).</summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    private const int MinWidth = 300;
    private const int MinHeight = 200;
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;

    /// <summary>Q-Dir's <c>windowplacement</c> notation: <c>x;y;w;h|sw=1</c> (sw=3 for maximized).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X};{Y};{Width};{Height}|sw={(Maximized ? ShowMaximized : ShowNormal)}");

    public static WindowPlacement? TryParse(string? text)
    {
        if (text is null)
            return null;

        string[] halves = text.Split('|');
        string[] numbers = halves[0].Split(';');
        if (numbers.Length != 4)
            return null;

        int[] values = new int[4];
        for (int i = 0; i < values.Length; i++)
        {
            if (!int.TryParse(numbers[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                return null;
        }

        if (values[2] < MinWidth || values[3] < MinHeight)
            return null;

        bool maximized = halves.Length > 1 && halves[1].Trim() == $"sw={ShowMaximized}";
        return new WindowPlacement(values[0], values[1], values[2], values[3], maximized);
    }
}

/// <summary>
/// Everything needed to bring the window back as it was: layout, splitter ratios, each pane's tabs (as PIDLs),
/// view modes, the active pane and the window position. Saved as an INI file (<c>.mdr</c>); loading tolerates
/// missing or damaged values by falling back to defaults instead of failing.
/// </summary>
internal sealed class Workspace
{
    private const string Main = "Workspace";
    private const int Version = 1;
    private const int MaxTabsPerPane = 200;
    private const int MinIconSize = 16;
    private const int MaxIconSize = 256;
    private const int MinTreeSplit = 500;
    private const int MaxTreeSplit = 9500;

    /// <summary>Tree width as a share of its pane or window, in 1/10000 (Q-Dir's default <c>Tree1split=2500</c>).</summary>
    public const int DefaultTreeSplit = 2500;

    public PaneLayout Layout { get; init; } = PaneLayout.Default;

    /// <summary>One tree for the whole window, following the active pane, instead of one tree per pane.</summary>
    public bool SharedTree { get; init; }

    public bool SharedTreeVisible { get; init; }

    public int SharedTreeSplit { get; init; } = DefaultTreeSplit;

    /// <summary>Splitter ratios (1/10000) in the layout's pre-order, like Q-Dir's SP0/SP1/SP2.</summary>
    public IReadOnlyList<int> Ratios { get; init; } = PaneLayout.Default.DefaultRatios;

    public int ActivePane { get; init; }

    public bool MultilineTabs { get; init; }

    public WindowPlacement? Window { get; init; }

    /// <summary>Always <see cref="PaneLayout.MaxPanes"/> entries; panes outside the layout keep their tabs.</summary>
    public IReadOnlyList<PaneState> Panes { get; init; } = [];

    /// <summary>"This PC", the start page of a new tab with nothing better to show.</summary>
    public static ShellLocation DefaultLocation => ShellLocation.Parse("shell:MyComputerFolder") ?? ShellLocation.Desktop;

    /// <summary>The first-run layout: four panes at <paramref name="first"/>, Documents, Downloads and the Desktop.</summary>
    public static Workspace CreateDefault(ShellLocation first)
    {
        ShellLocation[] folders =
        [
            first,
            ShellLocation.Parse("shell:Personal") ?? first,
            ShellLocation.Parse("shell:Downloads") ?? first,
            // "shell:Desktop" is the namespace root; the folder with the desktop's files is DesktopDirectory.
            ShellLocation.Parse(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) ?? first,
        ];

        return new Workspace
        {
            Panes = [.. folders.Select(folder => new PaneState([new TabState(folder, FolderViewMode.Details, 16)], 0))],
        };
    }

    /// <summary>Reads a workspace file; null when it is missing, unreadable or not a workspace.</summary>
    public static Workspace? Load(string path)
    {
        try
        {
            return FromIni(IniFile.Load(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path) => ToIni().Save(path);

    public static Workspace? FromIni(IniFile ini)
    {
        if (!ini.HasSection(Main))
            return null;

        PaneLayout layout = PaneLayout.FromId(ini.GetInt(Main, "Layout", PaneLayout.Default.Id)) ?? PaneLayout.Default;
        ShellLocation fallback = DefaultLocation;
        return new Workspace
        {
            Layout = layout,
            Ratios = ParseRatios(ini.Get(Main, "Ratios"), layout),
            ActivePane = Math.Clamp(ini.GetInt(Main, "Active", 0), 0, layout.PaneCount - 1),
            MultilineTabs = ini.GetBool(Main, "MultilineTabs", false),
            SharedTree = ini.GetInt(Main, "TreeMode", 0) == 1,
            SharedTreeVisible = ini.GetBool(Main, "SharedTree", false),
            SharedTreeSplit = Math.Clamp(ini.GetInt(Main, "SharedTreeSplit", DefaultTreeSplit), MinTreeSplit, MaxTreeSplit),
            Window = WindowPlacement.TryParse(ini.Get(Main, "Window")),
            Panes = [.. Enumerable.Range(0, PaneLayout.MaxPanes).Select(i => ReadPane(ini, i, fallback))],
        };
    }

    public IniFile ToIni()
    {
        var ini = new IniFile();
        ini.Set(Main, "Version", Version);
        ini.Set(Main, "Layout", Layout.Id);
        ini.Set(Main, "Ratios", string.Join(',', Ratios.Select(ratio => ratio.ToString(CultureInfo.InvariantCulture))));
        ini.Set(Main, "Active", ActivePane);
        ini.Set(Main, "MultilineTabs", MultilineTabs);
        ini.Set(Main, "TreeMode", SharedTree ? 1 : 0);
        ini.Set(Main, "SharedTree", SharedTreeVisible);
        ini.Set(Main, "SharedTreeSplit", SharedTreeSplit);
        if (Window is not null)
            ini.Set(Main, "Window", Window.ToString());

        for (int i = 0; i < Panes.Count; i++)
        {
            string section = PaneSection(i);
            PaneState pane = Panes[i];
            ini.Set(section, "Selected", pane.SelectedTab);
            ini.Set(section, "Tree", pane.TreeVisible);
            ini.Set(section, "TreeSplit", pane.TreeSplit);
            ini.Set(section, "FilterBar", pane.FilterBarVisible);
            if (pane.Filter is { Pattern.Length: > 0 } filter)
            {
                ini.Set(section, "Filter", filter.Pattern);
                ini.Set(section, "FilterAuto", filter.AutoWildcard);
                ini.Set(section, "FilterNot", filter.Negative);
                ini.Set(section, "FilterDirs", filter.FilterFolders);
            }
            ini.Set(section, "Tabs", pane.Tabs.Count);
            for (int t = 0; t < pane.Tabs.Count; t++)
            {
                TabState tab = pane.Tabs[t];
                ini.Set(section, $"Tab{t + 1}", IdListText.Encode(tab.Location.IdList));
                ini.Set(section, $"Tab{t + 1}.ViewMode", (int)tab.ViewMode);
                ini.Set(section, $"Tab{t + 1}.IconSize", tab.IconSize);
            }
        }

        return ini;
    }

    private static string PaneSection(int index) => $"Pane{index + 1}";

    private static IReadOnlyList<int> ParseRatios(string? text, PaneLayout layout)
    {
        string[] parts = text?.Split(',') ?? [];
        var ratios = new List<int>(parts.Length);
        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ratio))
                return layout.DefaultRatios;

            ratios.Add(Math.Clamp(ratio, 0, PaneLayout.RatioScale));
        }

        return ratios.Count == layout.SplitterCount ? ratios : layout.DefaultRatios;
    }

    private static PaneState ReadPane(IniFile ini, int index, ShellLocation fallback)
    {
        string section = PaneSection(index);
        int count = Math.Clamp(ini.GetInt(section, "Tabs", 0), 0, MaxTabsPerPane);
        var tabs = new List<TabState>(count);
        int selected = ini.GetInt(section, "Selected", 0);
        int selectedTab = 0;
        for (int t = 1; t <= count; t++)
        {
            byte[]? bytes = IdListText.Decode(ini.Get(section, $"Tab{t}"));
            if (bytes is null || ShellLocation.TryFromIdList(bytes) is not { } location)
                continue;

            if (t - 1 == selected)
                selectedTab = tabs.Count;

            int mode = ini.GetInt(section, $"Tab{t}.ViewMode", (int)FolderViewMode.Details);
            tabs.Add(new TabState(
                location,
                Enum.IsDefined((FolderViewMode)mode) && (FolderViewMode)mode != FolderViewMode.Auto ? (FolderViewMode)mode : FolderViewMode.Details,
                Math.Clamp(ini.GetInt(section, $"Tab{t}.IconSize", 16), MinIconSize, MaxIconSize)));
        }

        if (tabs.Count == 0)
            tabs.Add(new TabState(fallback, FolderViewMode.Details, MinIconSize));

        return new PaneState(
            tabs,
            selectedTab,
            ini.GetBool(section, "Tree", false),
            Math.Clamp(ini.GetInt(section, "TreeSplit", DefaultTreeSplit), MinTreeSplit, MaxTreeSplit),
            ini.Get(section, "Filter") is { Length: > 0 } pattern
                ? new FileFilter(pattern, ini.GetBool(section, "FilterAuto", true), ini.GetBool(section, "FilterNot", false), ini.GetBool(section, "FilterDirs", false))
                : null,
            ini.GetBool(section, "FilterBar", false));
    }
}
