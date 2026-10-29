using System.Runtime.InteropServices;

namespace MDir.Shell;

/// <summary>FOLDERVIEWOPTIONS.</summary>
[Flags]
internal enum FolderViewOptions : uint
{
    Default = 0,
    VistaLayout = 0x1,
    CustomPosition = 0x2,
    CustomOrdering = 0x4,
    SupportHyperlinks = 0x8,
    NoAnimations = 0x10,
    NoScrollTips = 0x20,
}

/// <summary>SHGDNF / SFGAO values used when reading an item's name and kind.</summary>
internal static class ShellNames
{
    public const uint InFolder = 0x1;
    public const uint ForParsing = 0x8000;
    public const uint AttrFolder = 0x20000000;
    public const uint AttrHidden = 0x00080000;
    public const uint AttrGhosted = 0x00008000;
}

/// <summary>ICommDlgBrowser::OnStateChange codes (CDBOSC_*).</summary>
internal static class CommDlgState
{
    public const uint SetFocus = 0;
    public const uint KillFocus = 1;
    public const uint SelectionChanged = 2;
    public const uint Rename = 3;
    public const uint StateChange = 4;
}

/// <summary>STRRET: a name returned by <c>IShellFolder::GetDisplayNameOf</c> (pointer-sized union plus the cStr buffer).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct StrRet
{
    public uint Type;
    public IntPtr Data;
    public fixed byte Rest[256];
}

[ComImport]
[Guid("3CC974D2-B302-4D36-AD3E-06D93F695D3F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderViewOptions
{
    [PreserveSig] int SetFolderViewOptions(FolderViewOptions mask, FolderViewOptions options);
    [PreserveSig] int GetFolderViewOptions(out FolderViewOptions options);
}

[ComImport]
[Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectWithSite
{
    [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);
    [PreserveSig] int GetSite(ref Guid riid, out IntPtr ppvSite);
}

/// <summary>The shell's IServiceProvider (not the managed System.IServiceProvider).</summary>
[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellServiceProvider
{
    [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport]
[Guid("000214F1-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICommDlgBrowser
{
    [PreserveSig] int OnDefaultCommand(IShellView ppshv);
    [PreserveSig] int OnStateChange(IShellView ppshv, uint uChange);
    [PreserveSig] int IncludeObject(IShellView ppshv, IntPtr pidl);
}

/// <summary>ICommDlgBrowser2 with the ICommDlgBrowser methods flattened in front (vtable order).</summary>
[ComImport]
[Guid("10339516-2894-11D2-9039-00C04F8EEB3E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICommDlgBrowser2
{
    [PreserveSig] int OnDefaultCommand(IShellView ppshv);
    [PreserveSig] int OnStateChange(IShellView ppshv, uint uChange);
    [PreserveSig] int IncludeObject(IShellView ppshv, IntPtr pidl);
    [PreserveSig] int Notify(IShellView ppshv, uint dwNotifyType);
    [PreserveSig] int GetDefaultMenuText(IShellView ppshv, IntPtr pszText, int cchMax);
    [PreserveSig] int GetViewFlags(out uint pdwFlags);
}

/// <summary>ICommDlgBrowser3 with the ICommDlgBrowser and ICommDlgBrowser2 methods flattened in front.</summary>
[ComImport]
[Guid("C8AD25A1-3294-41EE-8165-71174BD01C57")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICommDlgBrowser3
{
    [PreserveSig] int OnDefaultCommand(IShellView ppshv);
    [PreserveSig] int OnStateChange(IShellView ppshv, uint uChange);
    [PreserveSig] int IncludeObject(IShellView ppshv, IntPtr pidl);
    [PreserveSig] int Notify(IShellView ppshv, uint dwNotifyType);
    [PreserveSig] int GetDefaultMenuText(IShellView ppshv, IntPtr pszText, int cchMax);
    [PreserveSig] int GetViewFlags(out uint pdwFlags);
    [PreserveSig] int OnColumnClicked(IShellView ppshv, int iColumn);
    [PreserveSig] int GetCurrentFilter(IntPtr pszFileSpec, int cchFileSpec);
    [PreserveSig] int OnPreViewCreated(IShellView ppshv);
}

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
    [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributesOf(uint cidl, ref IntPtr apidl, ref uint rgfInOut);
    [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, ref IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out StrRet pName);
    [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
}

/// <summary>Reads the name and kind of items of a shell folder.</summary>
internal static class ShellFolderItems
{
    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");

    /// <summary>The folder a view shows, or null.</summary>
    public static IShellFolder? GetFolder(IFolderView2 view)
    {
        Guid iid = IidShellFolder;
        if (view.GetFolder(ref iid, out IntPtr folder) < 0 || folder == IntPtr.Zero)
            return null;

        try
        {
            return (IShellFolder)Marshal.GetObjectForIUnknown(folder);
        }
        finally
        {
            Marshal.Release(folder);
        }
    }

    /// <summary>File name with extension (as <c>report.txt</c>, whatever the "hide extensions" setting says).</summary>
    public static string? GetName(IShellFolder folder, IntPtr childPidl)
    {
        if (folder.GetDisplayNameOf(childPidl, ShellNames.InFolder | ShellNames.ForParsing, out StrRet name) < 0
            || NativeMethods.StrRetToStr(ref name, childPidl, out IntPtr text) < 0 || text == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>The requested SFGAO bits that are set for the item.</summary>
    public static uint GetAttributes(IShellFolder folder, IntPtr childPidl, uint mask)
    {
        uint attributes = mask;
        IntPtr pidl = childPidl;
        return folder.GetAttributesOf(1, ref pidl, ref attributes) < 0 ? 0 : attributes & mask;
    }
}
