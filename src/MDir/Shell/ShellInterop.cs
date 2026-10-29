using System.Runtime.InteropServices;

namespace MDir.Shell;

// COM declarations for hosting the Windows Explorer view (shobjidl_core.h).
// Methods return HRESULTs with [PreserveSig] so failures never throw across the COM boundary.

internal enum FolderViewMode
{
    Auto = -1,
    Icon = 1,
    SmallIcon = 2,
    List = 3,
    Details = 4,
    Thumbnail = 5,
    Tile = 6,
    Thumbstrip = 7,
    Content = 8,
}

[Flags]
internal enum ExplorerBrowserOptions : uint
{
    None = 0x0,
    NavigateOnce = 0x1,
    ShowFrames = 0x2,
    AlwaysNavigate = 0x4,
    NoTravelLog = 0x8,
    NoWrapperWindow = 0x10,
    HtmlSharePointView = 0x20,
    NoBorder = 0x40,
    NoPersistViewState = 0x80,
}

internal enum Sigdn : uint
{
    NormalDisplay = 0x00000000,
    DesktopAbsoluteParsing = 0x80028000,
    DesktopAbsoluteEditing = 0x8004C000,
    FileSysPath = 0x80058000,
}

internal static class SbspFlags
{
    public const uint Absolute = 0x0000;
}

internal static class Svgio
{
    public const uint Selection = 0x1;
    public const uint AllView = 0x2;
}

internal static class Svsi
{
    public const uint Deselect = 0x0;
    public const uint Select = 0x1;
    public const uint DeselectOthers = 0x4;
    public const uint EnsureVisible = 0x8;
    public const uint Focused = 0x10;
}

internal static class Svuia
{
    public const uint ActivateFocus = 2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FolderSettings
{
    public FolderViewMode ViewMode;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect(int left, int top, int right, int bottom)
{
    public int Left = left;
    public int Top = top;
    public int Right = right;
    public int Bottom = bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMsg
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int PtX;
    public int PtY;
    public uint Private;
}

/// <summary>LOGFONTW.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LogFont
{
    public int Height;
    public int Width;
    public int Escapement;
    public int Orientation;
    public int Weight;
    public byte Italic;
    public byte Underline;
    public byte StrikeOut;
    public byte CharSet;
    public byte OutPrecision;
    public byte ClipPrecision;
    public byte Quality;
    public byte PitchAndFamily;
    public fixed char FaceName[32];
}

/// <summary>SHFILEINFOW.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ShFileInfo
{
    public IntPtr Icon;
    public int IconIndex;
    public uint Attributes;
    public fixed char DisplayName[260];
    public fixed char TypeName[80];
}

[ComImport]
[Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB")]
[ClassInterface(ClassInterfaceType.None)]
internal class ExplorerBrowserClass
{
}

[ComImport]
[Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerBrowser
{
    [PreserveSig] int Initialize(IntPtr hwndParent, ref NativeRect prc, ref FolderSettings pfs);
    [PreserveSig] int Destroy();
    [PreserveSig] int SetRect(IntPtr phdwp, NativeRect rcBrowser);
    [PreserveSig] int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);
    [PreserveSig] int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);
    [PreserveSig] int SetFolderSettings(ref FolderSettings pfs);
    [PreserveSig] int Advise(IExplorerBrowserEvents psbe, out uint pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    [PreserveSig] int SetOptions(ExplorerBrowserOptions dwFlag);
    [PreserveSig] int GetOptions(out ExplorerBrowserOptions pdwFlag);
    [PreserveSig] int BrowseToIDList(IntPtr pidl, uint uFlags);
    [PreserveSig] int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);
    [PreserveSig] int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint dwFlags);
    [PreserveSig] int RemoveAll();
    [PreserveSig] int GetCurrentView(ref Guid riid, out IntPtr ppv);
}

[ComImport]
[Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerBrowserEvents
{
    [PreserveSig] int OnNavigationPending(IntPtr pidlFolder);
    [PreserveSig] int OnViewCreated([MarshalAs(UnmanagedType.IUnknown)] object psv);
    [PreserveSig] int OnNavigationComplete(IntPtr pidlFolder);
    [PreserveSig] int OnNavigationFailed(IntPtr pidlFolder);
}

// Only the leading methods we call are declared; the vtable order must match shobjidl_core.h.
[ComImport]
[Guid("000214E3-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
    [PreserveSig] int TranslateAccelerator(ref NativeMsg pmsg);
    [PreserveSig] int EnableModeless(int fEnable);
    [PreserveSig] int UIActivate(uint uState);
    [PreserveSig] int Refresh();
}

// IFolderView2 flattened with its IFolderView base methods, up to SetViewModeAndIconSize/GetViewModeAndIconSize.
[ComImport]
[Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // IFolderView
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);
    [PreserveSig] int GetFocusedItem(out int piItem);
    [PreserveSig] int GetItemPosition(IntPtr pidl, out long ppt);
    [PreserveSig] int GetSpacing(IntPtr ppt);
    [PreserveSig] int GetDefaultSpacing(out long ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);
    [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);

    // IFolderView2
    [PreserveSig] int SetGroupBy(IntPtr key, int fAscending);
    [PreserveSig] int GetGroupBy(IntPtr pkey, IntPtr pfAscending);
    [PreserveSig] int SetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr propvar);
    [PreserveSig] int GetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr ppropvar);
    [PreserveSig] int SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
    [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
    [PreserveSig] int SetText(int iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);
    [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
    [PreserveSig] int GetSortColumnCount(out int pcColumns);
    [PreserveSig] int SetSortColumns(IntPtr rgSortColumns, int cColumns);
    [PreserveSig] int GetSortColumns(IntPtr rgSortColumns, int cColumns);
    [PreserveSig] int GetItem(int iItem, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetVisibleItem(int iStart, int fPrevious, out int piItem);
    [PreserveSig] int GetSelectedItem(int iStart, out int piItem);
    [PreserveSig] int GetSelection(int fNoneImpliesFolder, out IntPtr ppsia);
    [PreserveSig] int GetSelectionState(IntPtr pidl, out uint pdwFlags);
    [PreserveSig] int InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string pszVerb);
    [PreserveSig] int SetViewModeAndIconSize(FolderViewMode uViewMode, int iImageSize);
    [PreserveSig] int GetViewModeAndIconSize(out FolderViewMode puViewMode, out int piImageSize);
}

internal static partial class NativeMethods
{
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_CHAR = 0x0102;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSCHAR = 0x0106;
    public const int WM_KEYLAST = 0x0109;
    public const int WM_APPCOMMAND = 0x0319;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int APPCOMMAND_BROWSER_BACKWARD = 1;
    public const int APPCOMMAND_BROWSER_FORWARD = 2;

    public const uint SHACF_FILESYS_DIRS = 0x20;
    public const uint SHACF_AUTOSUGGEST_FORCE_ON = 0x10000000;
    public const uint SHACF_AUTOAPPEND_FORCE_OFF = 0x80000000;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetNameFromIDList(IntPtr pidl, Sigdn sigdnName, out IntPtr ppszName);

    [LibraryImport("shell32.dll")]
    public static partial uint ILGetSize(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    public static partial IntPtr ILClone(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ILRemoveLastID(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    public static partial void ILFree(IntPtr pidl);

    [LibraryImport("shlwapi.dll")]
    public static partial int SHAutoComplete(IntPtr hwndEdit, uint dwFlags);

    [LibraryImport("shlwapi.dll", EntryPoint = "StrRetToStrW")]
    public static partial int StrRetToStr(ref StrRet pstr, IntPtr pidl, out IntPtr ppsz);

    [LibraryImport("shell32.dll")]
    public static partial int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, out IntPtr ppv);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetIDListFromObject(IntPtr punk, out IntPtr ppidl);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const uint GW_CHILD = 5;
    public const uint GW_HWNDNEXT = 2;

    public const uint WM_GETFONT = 0x0031;
    public const uint LVM_GETBKCOLOR = 0x1000;
    public const uint LVM_SETBKCOLOR = 0x1001;
    public const uint LVM_GETITEMSTATE = 0x102C;
    public const uint LVM_SETTEXTBKCOLOR = 0x1026;
    public const int COLOR_WINDOW = 5;
    public const uint RDW_INVALIDATE = 0x1;
    public const uint RDW_ERASE = 0x4;
    public const uint RDW_ALLCHILDREN = 0x80;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int GetSysColor(int nIndex);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static unsafe partial int GetObject(IntPtr h, int c, void* pv);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontIndirectW")]
    public static unsafe partial IntPtr CreateFontIndirect(LogFont* lplf);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    /// <summary>First descendant window of <paramref name="parent"/> with the given class name, or zero.</summary>
    public static IntPtr FindDescendant(IntPtr parent, string className)
    {
        for (IntPtr child = GetWindow(parent, GW_CHILD); child != IntPtr.Zero; child = GetWindow(child, GW_HWNDNEXT))
        {
            if (GetClassName(child) == className)
                return child;

            IntPtr found = FindDescendant(child, className);
            if (found != IntPtr.Zero)
                return found;
        }

        return IntPtr.Zero;
    }

    public const uint SHGFI_SMALLICON = 0x1;
    public const uint SHGFI_PIDL = 0x8;
    public const uint SHGFI_ICON = 0x100;

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    public static partial IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    public static unsafe string GetClassName(IntPtr hWnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetClassName(hWnd, buffer, 256);
        return new string(buffer, 0, length);
    }
}
