using MDir.Shell;

namespace MDir.Panes;

/// <summary>
/// One tab of a pane. A pane has a single Explorer view; inactive tabs keep only their history and view mode
/// (like Q-Dir, which also stores tabs as PIDLs) and the view is pointed at a tab when it is selected.
/// </summary>
internal sealed class PaneTab : IDisposable
{
    private ShellLocation? _iconLocation;
    private Bitmap? _icon;

    public PaneTab(ShellLocation? location = null)
    {
        if (location is not null)
            History.Visit(location);
    }

    private PaneTab(NavigationHistory<ShellLocation> history, FolderViewMode viewMode, int iconSize)
    {
        History = history;
        ViewMode = viewMode;
        IconSize = iconSize;
    }

    public NavigationHistory<ShellLocation> History { get; } = new();

    public FolderViewMode ViewMode { get; set; } = FolderViewMode.Details;

    public int IconSize { get; set; } = 16;

    public ShellLocation? Location => History.Current;

    public string Title => Location?.Name ?? "새 탭";

    public string ToolTip => Location?.DisplayName ?? Title;

    /// <summary>Folder icon for the current location, loaded once per location.</summary>
    public Bitmap? Icon
    {
        get
        {
            if (!Equals(_iconLocation, Location))
            {
                _icon?.Dispose();
                _icon = Location?.GetSmallIcon();
                _iconLocation = Location;
            }

            return _icon;
        }
    }

    public PaneTab Clone() => new(History.Clone(), ViewMode, IconSize);

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
        _iconLocation = null;
    }
}
