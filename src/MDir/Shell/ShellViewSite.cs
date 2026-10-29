using System.Runtime.InteropServices;

namespace MDir.Shell;

/// <summary>
/// The "site" handed to the Explorer browser. The shell view asks it (through <c>IServiceProvider</c>) for an
/// <c>ICommDlgBrowser</c>, which is how Q-Dir filters the item list (<see cref="ICommDlgBrowser.IncludeObject"/>)
/// and learns about selection and focus changes (<see cref="ICommDlgBrowser.OnStateChange"/>).
/// </summary>
[ComVisible(true)]
internal sealed class ShellViewSite : IShellServiceProvider, ICommDlgBrowser, ICommDlgBrowser2, ICommDlgBrowser3
{
    private static readonly Guid IidCommDlgBrowser = new("000214F1-0000-0000-C000-000000000046");
    private static readonly Guid IidCommDlgBrowser2 = new("10339516-2894-11D2-9039-00C04F8EEB3E");
    private static readonly Guid IidCommDlgBrowser3 = new("C8AD25A1-3294-41EE-8165-71174BD01C57");
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int ENotImpl = unchecked((int)0x80004001);

    private readonly object _cacheLock = new();
    private IShellView? _cachedView;
    private IShellFolder? _cachedFolder;

    /// <summary>
    /// Decides whether an item stays in the list: (file name, is a folder) → keep. Null keeps everything and
    /// costs nothing. May be called from the shell's enumeration thread, so assign an immutable delegate.
    /// </summary>
    public volatile Func<string, bool, bool>? Include;

    /// <summary>Raised for CDBOSC_* state changes; the argument is the code from <see cref="CommDlgState"/>.</summary>
    public event Action<uint>? StateChanged;

    public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        // The shell asks for the ICommDlgBrowser family (it uses the ICommDlgBrowser IID as the service id too).
        Type? requested = riid == IidCommDlgBrowser3 ? typeof(ICommDlgBrowser3)
            : riid == IidCommDlgBrowser2 ? typeof(ICommDlgBrowser2)
            : riid == IidCommDlgBrowser ? typeof(ICommDlgBrowser)
            : null;
        if (requested is null)
            return ENoInterface;

        ppvObject = Marshal.GetComInterfaceForObject(this, requested);
        return 0;
    }

    public int Notify(IShellView ppshv, uint dwNotifyType) => 0;

    public int GetDefaultMenuText(IShellView ppshv, IntPtr pszText, int cchMax) => ENotImpl;

    public int GetViewFlags(out uint pdwFlags)
    {
        pdwFlags = 0;
        return 0;
    }

    /// <summary>Not handled: the shell sorts by the clicked column as usual.</summary>
    public int OnColumnClicked(IShellView ppshv, int iColumn) => ENotImpl;

    public int GetCurrentFilter(IntPtr pszFileSpec, int cchFileSpec) => ENotImpl;

    public int OnPreViewCreated(IShellView ppshv) => 0;

    /// <summary>S_FALSE: let the shell do its default (open the folder or run the file).</summary>
    public int OnDefaultCommand(IShellView ppshv) => 1;

    public int OnStateChange(IShellView ppshv, uint uChange)
    {
        StateChanged?.Invoke(uChange);
        return 0;
    }

    public int IncludeObject(IShellView ppshv, IntPtr pidl)
    {
        Func<string, bool, bool>? include = Include;
        if (include is null || ResolveFolder(ppshv) is not { } folder || ShellFolderItems.GetName(folder, pidl) is not { } name)
            return 0;

        bool isFolder = ShellFolderItems.GetAttributes(folder, pidl, ShellNames.AttrFolder) != 0;
        return include(name, isFolder) ? 0 : 1;
    }

    /// <summary>The shell folder behind <paramref name="view"/>, cached because every item of a listing asks for it.</summary>
    private IShellFolder? ResolveFolder(IShellView view)
    {
        lock (_cacheLock)
        {
            if (!ReferenceEquals(view, _cachedView))
            {
                _cachedFolder = view is IFolderView2 folderView ? ShellFolderItems.GetFolder(folderView) : null;
                _cachedView = view;
            }

            return _cachedFolder;
        }
    }

    /// <summary>Forgets the cached folder (called when the browser goes away).</summary>
    public void Reset()
    {
        lock (_cacheLock)
        {
            _cachedView = null;
            _cachedFolder = null;
        }
    }
}
