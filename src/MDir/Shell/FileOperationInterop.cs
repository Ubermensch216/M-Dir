using System.Runtime.InteropServices;

namespace MDir.Shell;

[ComImport]
[Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
[ClassInterface(ClassInterfaceType.None)]
internal class FileOperationClass
{
}

/// <summary>
/// IFileOperation: the shell's copy/move/delete engine (progress dialog, collision prompts, Recycle Bin, undo
/// records). Objects are passed as <see cref="IShellItem"/>; the progress-sink parameters are unused here.
/// </summary>
[ComImport]
[Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    [PreserveSig] int Advise(IntPtr pfops, out uint pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
    [PreserveSig] int SetProgressDialog(IntPtr popd);
    [PreserveSig] int SetProperties(IntPtr pproparray);
    [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
    [PreserveSig] int ApplyPropertiesToItem(IShellItem psiItem);
    [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
    [PreserveSig] int RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
    [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    [PreserveSig] int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);
    [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
    [PreserveSig] int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);
    [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
    [PreserveSig] int DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
    [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
    [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
}

/// <summary>FILEOP_FLAGS (FOF_* and FOFX_*) used by <see cref="IFileOperation.SetOperationFlags"/>.</summary>
internal static class FileOperationFlags
{
    public const uint Silent = 0x4;
    public const uint RenameOnCollision = 0x8;
    public const uint NoConfirmation = 0x10;
    public const uint AllowUndo = 0x40;
    public const uint NoConfirmMkdir = 0x200;
    public const uint NoErrorUi = 0x400;
    public const uint WantNukeWarning = 0x4000;
    public const uint RecycleOnDelete = 0x00080000;
    public const uint AddUndoRecord = 0x20000000;
}
