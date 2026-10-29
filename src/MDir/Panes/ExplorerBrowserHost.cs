using System.ComponentModel;
using System.Runtime.InteropServices;
using MDir.Features;
using MDir.Shell;

namespace MDir.Panes;

/// <summary>
/// Hosts the Windows Explorer file view (IExplorerBrowser) as a WinForms control.
/// The shell supplies the item list, context menus, thumbnails, columns and drag and drop;
/// this control adds navigation history, view-mode persistence and keyboard routing.
/// </summary>
public sealed class ExplorerBrowserHost : Control
{
    private NavigationHistory<ShellLocation> _history = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 250 };
    private IExplorerBrowser? _browser;
    private EventSink? _sink;
    private uint _adviseCookie;
    private IShellView? _view;
    private IFolderView2? _folderView;
    private ShellLocation? _pendingLocation;
    private PendingNavigation _pending;
    private (ShellLocation Location, PendingNavigation Kind)? _queued;
    private FolderViewMode _viewMode = FolderViewMode.Details;
    private int _iconSize = 16;
    private int _itemCount = -1;
    private int _selectedCount = -1;
    private ColorListHook? _colorHook;
    private ColorRuleSet? _colorRules;
    private FileFilter _fileFilter = FileFilter.None;
    private bool _isActivePane;

    /// <summary>HRESULT_FROM_WIN32(ERROR_BUSY).</summary>
    private const int HResultBusy = unchecked((int)0x800700AA);

    private enum PendingNavigation
    {
        None,
        Back,
        Forward,

        /// <summary>Showing a tab's current entry again: history stays as it is.</summary>
        Restore,
    }

    public ExplorerBrowserHost()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        _pollTimer.Tick += (_, _) => PollViewState();
    }

    /// <summary>Raised after the view shows a new folder.</summary>
    public event EventHandler? NavigationCompleted;

    public event EventHandler<NavigationFailedEventArgs>? NavigationFailed;

    /// <summary>Raised when the item or selection count changes.</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>Raised when the view mode or icon size changes, including changes made from the shell's own menus.</summary>
    public event EventHandler? ViewModeChanged;

    /// <summary>Raised when the shell view's list gets keyboard focus (also by a click inside it).</summary>
    public event EventHandler? ViewFocused;

    /// <summary>Raised when the selection changed, and after navigating (which clears it).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The object the shell view queries for item filtering and state notifications.</summary>
    internal ShellViewSite ViewSite { get; } = new();

    /// <summary>Colour rules for the list; null turns colouring off.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ColorRuleSet? ColorRules
    {
        get => _colorRules;
        set
        {
            _colorRules = value;
            _colorHook?.RefreshAppearance();
        }
    }

    /// <summary>Whether this pane is the active one, which selects the list background rule (#BG-A).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsActivePane
    {
        get => _isActivePane;
        set
        {
            if (_isActivePane == value)
                return;

            _isActivePane = value;
            _colorHook?.RefreshAppearance();
        }
    }

    /// <summary>The name filter of this pane. Applied to every folder the pane shows.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal FileFilter FileFilter
    {
        get => _fileFilter;
        set
        {
            _fileFilter = value;
            ViewSite.Include = value.Compile();
            RefreshView();
        }
    }

    public ShellLocation? CurrentLocation => _history.Current;

    public bool CanGoBack => _history.CanGoBack;

    public bool CanGoForward => _history.CanGoForward;

    public bool CanGoUp => CurrentLocation is { IsDesktop: false };

    public int ItemCount => Math.Max(_itemCount, 0);

    public int SelectedCount => Math.Max(_selectedCount, 0);

    internal FolderViewMode ViewMode => _viewMode;

    internal int IconSize => _iconSize;

    public int FocusedItemIndex
    {
        get
        {
            int index = -1;
            return _folderView?.GetFocusedItem(out index) >= 0 ? index : -1;
        }
    }

    public void Navigate(ShellLocation location) => BrowseTo(location, PendingNavigation.None);

    /// <summary>Parses and navigates to <paramref name="text"/>; returns false when it doesn't resolve.</summary>
    public bool Navigate(string text)
    {
        ShellLocation? location = ShellLocation.Parse(text);
        if (location is null)
            return false;

        Navigate(location);
        return true;
    }

    public void GoBack()
    {
        if (_history.PeekBack() is { } target)
            BrowseTo(target, PendingNavigation.Back);
    }

    public void GoForward()
    {
        if (_history.PeekForward() is { } target)
            BrowseTo(target, PendingNavigation.Forward);
    }

    public void GoUp()
    {
        if (CurrentLocation?.GetParent() is { } parent)
            Navigate(parent);
    }

    public void RefreshView() => _view?.Refresh();

    /// <summary>
    /// Switches to another tab's state: its history object becomes the live one (later navigation updates it
    /// in place) and the view shows its current entry with its view mode.
    /// </summary>
    internal void LoadState(NavigationHistory<ShellLocation> history, FolderViewMode mode, int iconSize)
    {
        _history = history;
        _queued = null;
        _viewMode = mode;
        _iconSize = iconSize;
        if (_browser is not null)
        {
            var settings = new FolderSettings { ViewMode = mode };
            _browser.SetFolderSettings(ref settings);
        }

        ViewModeChanged?.Invoke(this, EventArgs.Empty);
        if (history.Current is { } location)
            BrowseTo(location, PendingNavigation.Restore);
    }

    /// <summary>
    /// The first selected item (a full location), or null when nothing is selected. Together with
    /// <see cref="SelectedCount"/> this tells whether exactly one item is selected.
    /// </summary>
    internal ShellLocation? GetFirstSelected()
    {
        if (_folderView is null || _folderView.GetSelection(0, out IntPtr array) < 0 || array == IntPtr.Zero)
            return null;

        try
        {
            var items = (IShellItemArray)Marshal.GetObjectForIUnknown(array);
            return items.GetCount(out uint count) >= 0 && count > 0 && items.GetItemAt(0, out IShellItem item) >= 0
                ? ShellItems.ToLocation(item)
                : null;
        }
        finally
        {
            Marshal.Release(array);
        }
    }

    /// <summary>All selected items as full locations (virtual items such as "This PC" included).</summary>
    internal IReadOnlyList<ShellLocation> GetSelectedLocations(int limit = 100_000)
    {
        var locations = new List<ShellLocation>();
        if (_folderView is null || _folderView.GetSelection(0, out IntPtr array) < 0 || array == IntPtr.Zero)
            return locations;

        try
        {
            var items = (IShellItemArray)Marshal.GetObjectForIUnknown(array);
            if (items.GetCount(out uint count) < 0)
                return locations;

            for (uint i = 0; i < Math.Min(count, (uint)limit); i++)
            {
                if (items.GetItemAt(i, out IShellItem item) >= 0 && ShellItems.ToLocation(item) is { } location)
                    locations.Add(location);
            }
        }
        finally
        {
            Marshal.Release(array);
        }

        return locations;
    }

    /// <summary>The file system paths of the selected items (items without one are left out).</summary>
    internal IReadOnlyList<string> GetSelectedPaths() => [.. GetSelectedLocations().Select(location => location.FileSystemPath).OfType<string>()];

    /// <summary>The classic list of the shell view, or zero before it exists.</summary>
    private IntPtr ListWindow => _view is not null && _view.GetWindow(out IntPtr window) >= 0 ? NativeMethods.FindDescendant(window, "SysListView32") : IntPtr.Zero;

    internal void SelectAll() => SetAllSelected(_ => true);

    internal void InvertSelection() => SetAllSelected(wasSelected => !wasSelected);

    /// <summary>Starts in-place renaming of the selected item (the first one when several are selected).</summary>
    internal bool BeginRename()
    {
        IntPtr list = ListWindow;
        if (list == IntPtr.Zero || _folderView is null || _folderView.GetSelectedItem(-1, out int index) < 0 || index < 0)
            return false;

        NativeMethods.SetFocus(list);
        return NativeMethods.SendMessage(list, NativeMethods.LVM_EDITLABEL, (IntPtr)index, IntPtr.Zero) != IntPtr.Zero;
    }

    /// <summary>Runs a shell verb ("properties", "copy"...) on the selected items.</summary>
    internal bool InvokeVerb(string verb) => _folderView?.InvokeVerbOnSelection(verb) >= 0;

    /// <summary>
    /// Selects the item named like <paramref name="path"/> once the view lists it (a file just created shows up a
    /// moment later), waiting at most <paramref name="timeout"/>. Returns false when it never appeared.
    /// </summary>
    internal async Task<bool> SelectPathAsync(string path, TimeSpan timeout)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsDisposed)
                return false;

            if (FindItemIndex(name) is int index and >= 0 && _folderView!.SelectItem(index, Svsi.Select | Svsi.DeselectOthers | Svsi.EnsureVisible | Svsi.Focused) >= 0)
                return true;

            await Task.Delay(80);
        }

        return false;
    }

    private int FindItemIndex(string name)
    {
        if (_folderView is null || ShellFolderItems.GetFolder(_folderView) is not { } folder || _folderView.ItemCount(Svgio.AllView, out int count) < 0)
            return -1;

        for (int i = 0; i < count; i++)
        {
            if (_folderView.Item(i, out IntPtr pidl) < 0 || pidl == IntPtr.Zero)
                continue;

            try
            {
                if (string.Equals(ShellFolderItems.GetName(folder, pidl), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }

        return -1;
    }

    /// <summary>Sets every list item's selection to <paramref name="decide"/>(was it selected).</summary>
    private unsafe void SetAllSelected(Func<bool, bool> decide)
    {
        IntPtr list = ListWindow;
        if (list == IntPtr.Zero || _folderView is null || _folderView.ItemCount(Svgio.AllView, out int count) < 0)
            return;

        // LVITEM: state and stateMask sit at offsets 12 and 16, before any pointer member.
        byte* item = stackalloc byte[128];
        for (int i = 0; i < count; i++)
        {
            bool wasSelected = ((int)NativeMethods.SendMessage(list, NativeMethods.LVM_GETITEMSTATE, (IntPtr)i, (IntPtr)NativeMethods.LVIS_SELECTED) & NativeMethods.LVIS_SELECTED) != 0;
            bool select = decide(wasSelected);
            if (select == wasSelected)
                continue;

            new Span<byte>(item, 128).Clear();
            *(int*)(item + 12) = select ? NativeMethods.LVIS_SELECTED : 0;
            *(int*)(item + 16) = NativeMethods.LVIS_SELECTED;
            NativeMethods.SendMessage(list, NativeMethods.LVM_SETITEMSTATE, (IntPtr)i, (IntPtr)item);
        }
    }

    internal void SetViewMode(FolderViewMode mode, int iconSize)
    {
        _viewMode = mode;
        _iconSize = iconSize;
        _folderView?.SetViewModeAndIconSize(mode, iconSize);
        if (_browser is not null)
        {
            var settings = new FolderSettings { ViewMode = mode };
            _browser.SetFolderSettings(ref settings);
        }

        ViewModeChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool SelectItem(int index) =>
        _folderView?.SelectItem(index, Svsi.Select | Svsi.DeselectOthers | Svsi.EnsureVisible | Svsi.Focused) >= 0;

    /// <summary>Moves keyboard focus into the item list.</summary>
    public void FocusView()
    {
        if (_view is null)
            return;

        _view.UIActivate(Svuia.ActivateFocus);
        if (_view.GetWindow(out IntPtr viewWindow) >= 0 && !ContainsFocus)
            NativeMethods.SetFocus(viewWindow);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DesignMode)
            return;

        _browser = (IExplorerBrowser)new ExplorerBrowserClass();

        // Classic list layout (SysListView32) instead of the DirectUI one: only that list can be colour-coded by
        // handling NM_CUSTOMDRAW, which is how Q-Dir does it. Must be set before Initialize.
        if (_browser is IFolderViewOptions viewOptions)
            viewOptions.SetFolderViewOptions(FolderViewOptions.VistaLayout, FolderViewOptions.VistaLayout);

        _sink = new EventSink(this);
        Marshal.ThrowExceptionForHR(_browser.Advise(_sink, out _adviseCookie));

        var rect = new NativeRect(0, 0, ClientSize.Width, ClientSize.Height);
        var settings = new FolderSettings { ViewMode = _viewMode };
        Marshal.ThrowExceptionForHR(_browser.Initialize(Handle, ref rect, ref settings));

        // Lets the shell view find our ICommDlgBrowser (item filter, selection and focus notifications).
        if (_browser is IObjectWithSite withSite)
            withSite.SetSite(ViewSite);
        ViewSite.StateChanged += OnViewStateChanged;

        // Pane-level view mode (like Q-Dir): don't let each folder restore Explorer's saved view state.
        _browser.SetOptions(ExplorerBrowserOptions.AlwaysNavigate | ExplorerBrowserOptions.NoTravelLog
            | ExplorerBrowserOptions.NoBorder | ExplorerBrowserOptions.NoPersistViewState);
        _browser.SetPropertyBag("M-Dir");
        _pollTimer.Start();

        if (_pendingLocation is { } initial)
        {
            _pendingLocation = null;
            Navigate(initial);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _pollTimer.Stop();
        if (_browser is not null)
        {
            if (_adviseCookie != 0)
                _browser.Unadvise(_adviseCookie);

            (_browser as IObjectWithSite)?.SetSite(null);
            ViewSite.StateChanged -= OnViewStateChanged;
            ViewSite.Reset();
            _colorHook?.Dispose();
            _colorHook = null;
            _browser.Destroy();
            Marshal.ReleaseComObject(_browser);
            _browser = null;
            _adviseCookie = 0;
        }

        _view = null;
        _folderView = null;
        _sink = null;
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _pollTimer.Dispose();

        base.Dispose(disposing);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _browser?.SetRect(IntPtr.Zero, new NativeRect(0, 0, ClientSize.Width, ClientSize.Height));
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        FocusView();
    }

    /// <summary>
    /// Keyboard routing for messages aimed at the shell's child windows. WinForms would otherwise treat
    /// arrows and Tab as dialog navigation keys. Order: application shortcuts, Backspace (our history),
    /// the shell view's accelerators (F2, Del, Ctrl+C...), then normal dispatch to the shell window.
    /// </summary>
    public override bool PreProcessMessage(ref Message msg)
    {
        if (msg.Msg is < NativeMethods.WM_KEYDOWN or > NativeMethods.WM_KEYLAST)
            return base.PreProcessMessage(ref msg);

        // Alt+letter menu mnemonics.
        if (msg.Msg == NativeMethods.WM_SYSCHAR)
            return base.PreProcessMessage(ref msg);

        bool keyDown = msg.Msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        Keys keyData = (Keys)(int)(nint)msg.WParam | ModifierKeys;
        if (keyDown && ProcessCmdKey(ref msg, keyData))
            return true;

        if (msg.Msg == NativeMethods.WM_KEYDOWN && keyData == Keys.Back && !IsEditFocused())
        {
            GoBack();
            return true;
        }

        if (_view is not null)
        {
            var native = new NativeMsg { Hwnd = msg.HWnd, Message = (uint)msg.Msg, WParam = msg.WParam, LParam = msg.LParam };
            if (_view.TranslateAccelerator(ref native) == 0)
                return true;
        }

        if (keyDown && (keyData & ~Keys.Shift) == Keys.Tab)
            return ProcessDialogKey(keyData);

        return false;
    }

    protected override void WndProc(ref Message m)
    {
        // Mouse back/forward buttons bubble up from the shell view as WM_APPCOMMAND.
        if (m.Msg == NativeMethods.WM_APPCOMMAND)
        {
            int command = (short)(((int)(nint)m.LParam >> 16) & ~0xF000);
            if (command == NativeMethods.APPCOMMAND_BROWSER_BACKWARD && CanGoBack)
            {
                GoBack();
                m.Result = 1;
                return;
            }

            if (command == NativeMethods.APPCOMMAND_BROWSER_FORWARD && CanGoForward)
            {
                GoForward();
                m.Result = 1;
                return;
            }
        }

        base.WndProc(ref m);
    }

    private void BrowseTo(ShellLocation location, PendingNavigation kind)
    {
        if (_browser is null)
        {
            _pendingLocation = location;
            return;
        }

        PendingNavigation previous = _pending;
        _pending = kind;
        using NativePidl pidl = location.ToNative();
        int hr = _browser.BrowseToIDList(pidl.Handle, SbspFlags.Absolute);
        if (hr == HResultBusy)
        {
            // The browser refuses a second navigation while one is in flight (e.g. right after a tab switch).
            // Keep the newest request and run it when the current one finishes.
            _pending = previous;
            _queued = (location, kind);
        }
        else if (hr < 0)
        {
            _pending = PendingNavigation.None;
            NavigationFailed?.Invoke(this, new NavigationFailedEventArgs(location));
        }
    }

    /// <summary>Starts the navigation that arrived while the previous one was pending.</summary>
    private void RunQueuedNavigation()
    {
        if (_queued is not var (location, kind) || !IsHandleCreated)
            return;

        _queued = null;
        BeginInvoke(() => BrowseTo(location, kind));
    }

    /// <summary>The shell view reports selection and focus changes here, so the status updates immediately.</summary>
    private void OnViewStateChanged(uint change)
    {
        if (change == CommDlgState.SelectionChanged)
        {
            PollViewState();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (change == CommDlgState.SetFocus)
            ViewFocused?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewCreated(object view)
    {
        _view = view as IShellView;
        _folderView = view as IFolderView2;

        // A new shell view window exists for every folder: hook its list for colouring again.
        _colorHook?.Dispose();
        _colorHook = _view is null ? null : new ColorListHook(_view, () => _colorRules, () => _isActivePane);
        _folderView?.SetViewModeAndIconSize(_viewMode, _iconSize);
    }

    private void OnNavigationComplete(IntPtr pidl)
    {
        ShellLocation location = ShellLocation.FromPidl(pidl);
        switch (_pending)
        {
            case PendingNavigation.Back when _history.CanGoBack:
                _history.MoveBack();
                break;
            case PendingNavigation.Forward when _history.CanGoForward:
                _history.MoveForward();
                break;
            case PendingNavigation.Restore when _history.Current is not null:
                break;
            default:
                _history.Visit(location);
                break;
        }

        _pending = PendingNavigation.None;
        _itemCount = _selectedCount = -1;
        NavigationCompleted?.Invoke(this, EventArgs.Empty);
        PollViewState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RunQueuedNavigation();
    }

    private void OnNavigationFailed(IntPtr pidl)
    {
        _pending = PendingNavigation.None;
        NavigationFailed?.Invoke(this, new NavigationFailedEventArgs(ShellLocation.FromPidl(pidl)));
        RunQueuedNavigation();
    }

    // Selection changes arrive through ICommDlgBrowser::OnStateChange; the item count and view mode (which the shell
    // reports no event for, e.g. after the folder finished enumerating or a file appeared) are polled.
    private void PollViewState()
    {
        if (_folderView is null)
            return;

        _folderView.ItemCount(Svgio.AllView, out int items);
        _folderView.ItemCount(Svgio.Selection, out int selected);
        if (items != _itemCount || selected != _selectedCount)
        {
            _itemCount = items;
            _selectedCount = selected;
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        // While a tab switch is loading, the old view still reports the previous tab's mode.
        if (_pending != PendingNavigation.Restore
            && _folderView.GetViewModeAndIconSize(out FolderViewMode mode, out int size) >= 0
            && (mode != _viewMode || size != _iconSize))
        {
            _viewMode = mode;
            _iconSize = size;
            ViewModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsEditFocused()
    {
        IntPtr focus = NativeMethods.GetFocus();
        return focus != IntPtr.Zero && NativeMethods.GetClassName(focus).Equals("Edit", StringComparison.OrdinalIgnoreCase);
    }

    [ComVisible(true)]
    private sealed class EventSink(ExplorerBrowserHost owner) : IExplorerBrowserEvents
    {
        public int OnNavigationPending(IntPtr pidlFolder) => 0;

        public int OnViewCreated(object psv)
        {
            owner.OnViewCreated(psv);
            return 0;
        }

        public int OnNavigationComplete(IntPtr pidlFolder)
        {
            owner.OnNavigationComplete(pidlFolder);
            return 0;
        }

        public int OnNavigationFailed(IntPtr pidlFolder)
        {
            owner.OnNavigationFailed(pidlFolder);
            return 0;
        }
    }
}

public sealed class NavigationFailedEventArgs(ShellLocation location) : EventArgs
{
    public ShellLocation Location { get; } = location;
}
