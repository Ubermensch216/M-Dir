using System.Runtime.InteropServices;

namespace MDir.Shell;

/// <summary>NSTCSTYLE: look and behaviour of the shell's folder tree control.</summary>
[Flags]
internal enum NstcStyle : uint
{
    None = 0,
    HasExpandos = 0x1,
    HasLines = 0x2,
    SingleClickExpand = 0x4,
    FullRowSelect = 0x8,
    SpringExpand = 0x10,
    HorizontalScroll = 0x20,
    RootHasExpando = 0x40,
    ShowSelectionAlways = 0x80,
    NoEditLabels = 0x10000,
    TabStop = 0x20000,
}

/// <summary>NSTCITEMSTATE.</summary>
[Flags]
internal enum NstcItemState : uint
{
    None = 0,
    Selected = 0x1,
    Expanded = 0x2,
    Bold = 0x4,
    Disabled = 0x8,
    SelectedNoExpand = 0x10,
}

/// <summary>NSTCROOTSTYLE.</summary>
[Flags]
internal enum NstcRootStyle : uint
{
    Visible = 0,
    Hidden = 0x1,
    Expanded = 0x2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[ComImport]
[Guid("AE054212-3535-4430-83ED-D501AA6680E6")]
[ClassInterface(ClassInterfaceType.None)]
internal class NamespaceTreeControlClass
{
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(Sigdn sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
    [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
}

/// <summary>
/// INameSpaceTreeControl2 with its INameSpaceTreeControl base methods flattened in vtable order
/// (a ComImport interface does not inherit its base's slots).
/// </summary>
[ComImport]
[Guid("7CC7AED8-290E-49BC-8945-C1401CC9306C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INameSpaceTreeControl2
{
    [PreserveSig] int Initialize(IntPtr hwndParent, ref NativeRect prc, NstcStyle nstcsFlags);
    [PreserveSig] int TreeAdvise([MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);
    [PreserveSig] int TreeUnadvise(uint dwCookie);
    [PreserveSig] int AppendRoot(IShellItem psiRoot, uint grfEnumFlags, NstcRootStyle grfRootStyle, IntPtr pif);
    [PreserveSig] int InsertRoot(int iIndex, IShellItem psiRoot, uint grfEnumFlags, NstcRootStyle grfRootStyle, IntPtr pif);
    [PreserveSig] int RemoveRoot(IShellItem psiRoot);
    [PreserveSig] int RemoveAllRoots();
    [PreserveSig] int GetRootItems(out IShellItemArray ppsiaRootItems);
    [PreserveSig] int SetItemState(IShellItem psi, NstcItemState nstcisMask, NstcItemState nstcisFlags);
    [PreserveSig] int GetItemState(IShellItem psi, NstcItemState nstcisMask, out NstcItemState pnstcisFlags);
    [PreserveSig] int GetSelectedItems(out IShellItemArray psiaItems);
    [PreserveSig] int GetItemCustomState(IShellItem psi, out int piStateNumber);
    [PreserveSig] int SetItemCustomState(IShellItem psi, int iStateNumber);
    [PreserveSig] int EnsureItemVisible(IShellItem psi);
    [PreserveSig] int SetTheme([MarshalAs(UnmanagedType.LPWStr)] string pszTheme);
    [PreserveSig] int GetNextItem(IShellItem psi, int nstcgi, out IShellItem ppsiNext);
    [PreserveSig] int HitTest(ref NativePoint ppt, out IShellItem ppsiOut);
    [PreserveSig] int GetItemRect(IShellItem psi, out NativeRect prect);
    [PreserveSig] int CollapseAll();
    [PreserveSig] int SetControlStyle(NstcStyle nstcsMask, NstcStyle nstcsStyle);
    [PreserveSig] int GetControlStyle(NstcStyle nstcsMask, out NstcStyle pnstcsStyle);
    [PreserveSig] int SetControlStyle2(uint nstcsMask, uint nstcsStyle);
    [PreserveSig] int GetControlStyle2(uint nstcsMask, out uint pnstcsStyle);
}

[ComImport]
[Guid("93D77985-B3D8-4484-8318-672CDDA002CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INameSpaceTreeControlEvents
{
    [PreserveSig] int OnItemClick(IShellItem psi, uint nstceHitTest, NstcStyle nstcsFlags);
    [PreserveSig] int OnPropertyItemCommit(IShellItem psi);
    [PreserveSig] int OnItemStateChanging(IShellItem psi, NstcItemState nstcisMask, NstcItemState nstcisState);
    [PreserveSig] int OnItemStateChanged(IShellItem psi, NstcItemState nstcisMask, NstcItemState nstcisState);
    [PreserveSig] int OnSelectionChanged(IShellItemArray psiaSelection);
    [PreserveSig] int OnKeyboardInput(uint uMsg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int OnBeforeExpand(IShellItem psi);
    [PreserveSig] int OnAfterExpand(IShellItem psi);
    [PreserveSig] int OnBeginLabelEdit(IShellItem psi);
    [PreserveSig] int OnEndLabelEdit(IShellItem psi);
    [PreserveSig] int OnGetToolTip(IShellItem psi, IntPtr pszTip, int cchTip);
    [PreserveSig] int OnBeforeItemDelete(IShellItem psi);
    [PreserveSig] int OnItemAdded(IShellItem psi, [MarshalAs(UnmanagedType.Bool)] bool fIsRoot);
    [PreserveSig] int OnItemDeleted(IShellItem psi, [MarshalAs(UnmanagedType.Bool)] bool fIsRoot);
    [PreserveSig] int OnBeforeContextMenu(IShellItem psi, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int OnAfterContextMenu(IShellItem psi, IntPtr pcmIn, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int OnBeforeStateImageChange(IShellItem psi);
    [PreserveSig] int OnGetDefaultIconIndex(IShellItem psi, out int piDefaultIcon, out int piOpenIcon);
}

/// <summary>Conversions between <see cref="ShellLocation"/> and <see cref="IShellItem"/>.</summary>
internal static class ShellItems
{
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static IShellItem? FromLocation(ShellLocation location)
    {
        using NativePidl pidl = location.ToNative();
        Guid iid = IidShellItem;
        if (NativeMethods.SHCreateItemFromIDList(pidl.Handle, ref iid, out IntPtr item) < 0 || item == IntPtr.Zero)
            return null;

        try
        {
            return (IShellItem)Marshal.GetObjectForIUnknown(item);
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    /// <summary>
    /// The item for a file system path, used as it is (unlike <see cref="ShellLocation.Parse"/>, which expands
    /// <c>%variables%</c> and would change a file name that contains percent signs).
    /// </summary>
    public static IShellItem? FromPath(string path)
    {
        Guid iid = IidShellItem;
        if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IntPtr item) < 0 || item == IntPtr.Zero)
            return null;

        try
        {
            return (IShellItem)Marshal.GetObjectForIUnknown(item);
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    public static ShellLocation? ToLocation(IShellItem item)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(item);
        try
        {
            if (NativeMethods.SHGetIDListFromObject(unknown, out IntPtr pidl) < 0 || pidl == IntPtr.Zero)
                return null;

            try
            {
                return ShellLocation.FromPidl(pidl);
            }
            finally
            {
                NativeMethods.ILFree(pidl);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
