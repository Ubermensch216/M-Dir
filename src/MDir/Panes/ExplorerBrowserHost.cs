using System.Runtime.InteropServices;
using MDir.Shell;

namespace MDir.Panes;

/// <summary>
/// Hosts the Windows Explorer file view (IExplorerBrowser) as a WinForms control.
/// The shell supplies the item list, context menus, thumbnails, columns and drag and drop;
/// this control adds navigation history, view-mode persistence and keyboard routing.
/// </summary>
public sealed class ExplorerBrowserHost : Control
{
    private readonly NavigationHistory<ShellLocation> _history = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 250 };
    private IExplorerBrowser? _browser;
    private EventSink? _sink;
    private uint _adviseCookie;
    private IShellView? _view;
    private IFolderView2? _folderView;
    private ShellLocation? _pendingLocation;
    private PendingNavigation _pending;
    private FolderViewMode _viewMode = FolderViewMode.Details;
    private int _iconSize = 16;
    private int _itemCount = -1;
    private int _selectedCount = -1;

    private enum PendingNavigation
    {
        None,
        Back,
        Forward,
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
        _sink = new EventSink(this);
        Marshal.ThrowExceptionForHR(_browser.Advise(_sink, out _adviseCookie));

        var rect = new NativeRect(0, 0, ClientSize.Width, ClientSize.Height);
        var settings = new FolderSettings { ViewMode = _viewMode };
        Marshal.ThrowExceptionForHR(_browser.Initialize(Handle, ref rect, ref settings));

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

        _pending = kind;
        using NativePidl pidl = location.ToNative();
        int hr = _browser.BrowseToIDList(pidl.Handle, SbspFlags.Absolute);
        if (hr < 0)
        {
            _pending = PendingNavigation.None;
            NavigationFailed?.Invoke(this, new NavigationFailedEventArgs(location));
        }
    }

    private void OnViewCreated(object view)
    {
        _view = view as IShellView;
        _folderView = view as IFolderView2;
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
            default:
                _history.Visit(location);
                break;
        }

        _pending = PendingNavigation.None;
        _itemCount = _selectedCount = -1;
        NavigationCompleted?.Invoke(this, EventArgs.Empty);
        PollViewState();
    }

    private void OnNavigationFailed(IntPtr pidl)
    {
        _pending = PendingNavigation.None;
        NavigationFailed?.Invoke(this, new NavigationFailedEventArgs(ShellLocation.FromPidl(pidl)));
    }

    // The browser has no selection-changed event short of implementing ICommDlgBrowser (which switches
    // DefView into file-dialog mode), so poll the cheap counters instead.
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

        if (_folderView.GetViewModeAndIconSize(out FolderViewMode mode, out int size) >= 0
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
