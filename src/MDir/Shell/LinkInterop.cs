using System.Runtime.InteropServices;
using System.Text;

namespace MDir.Shell;

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
[ClassInterface(ClassInterfaceType.None)]
internal class ShellLinkClass
{
}

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    [PreserveSig] int GetIDList(out IntPtr ppidl);
    [PreserveSig] int SetIDList(IntPtr pidl);
    [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
    [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
    [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    [PreserveSig] int GetHotkey(out ushort pwHotkey);
    [PreserveSig] int SetHotkey(ushort wHotkey);
    [PreserveSig] int GetShowCmd(out int piShowCmd);
    [PreserveSig] int SetShowCmd(int iShowCmd);
    [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
    [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
    [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

/// <summary>IPersistFile with the IPersist method flattened in front.</summary>
[ComImport]
[Guid("0000010B-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    [PreserveSig] int GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
}

/// <summary>What a favorite's shortcut file points at and which icon it asks for.</summary>
internal sealed record ShellLinkInfo(ShellLocation? Target, string? IconPath, int IconIndex);

/// <summary>Creates and reads <c>.lnk</c> files. Targets are stored as PIDLs, so virtual folders ("This PC") work too.</summary>
internal static class ShellLinks
{
    private const uint StgmRead = 0;

    /// <summary>Writes a shortcut to <paramref name="target"/> at <paramref name="linkPath"/> (replacing a file there).</summary>
    public static void Create(string linkPath, ShellLocation target)
    {
        var link = (IShellLinkW)new ShellLinkClass();
        try
        {
            using NativePidl pidl = target.ToNative();
            ThrowIfFailed(link.SetIDList(pidl.Handle), "SetIDList");
            Save(link, linkPath);
        }
        finally
        {
            Release(link);
        }
    }

    /// <summary>Reads a shortcut; null when the file is not a readable shortcut.</summary>
    public static ShellLinkInfo? Read(string linkPath)
    {
        var link = (IShellLinkW)new ShellLinkClass();
        try
        {
            if (((IPersistFile)link).Load(linkPath, StgmRead) < 0)
                return null;

            ShellLocation? target = null;
            if (link.GetIDList(out IntPtr pidl) >= 0 && pidl != IntPtr.Zero)
            {
                try
                {
                    target = ShellLocation.FromPidl(pidl);
                }
                finally
                {
                    NativeMethods.ILFree(pidl);
                }
            }
            else
            {
                // A shortcut written by another program may carry only a path.
                var path = new StringBuilder(520);
                if (link.GetPath(path, path.Capacity, IntPtr.Zero, 0) >= 0 && path.Length > 0)
                    target = ShellLocation.Parse(path.ToString());
            }

            var icon = new StringBuilder(520);
            link.GetIconLocation(icon, icon.Capacity, out int iconIndex);
            return new ShellLinkInfo(target, icon.Length > 0 ? icon.ToString() : null, iconIndex);
        }
        finally
        {
            Release(link);
        }
    }

    /// <summary>Sets the icon of an existing shortcut (null or empty <paramref name="iconPath"/> restores the target's own icon).</summary>
    public static void SetIcon(string linkPath, string? iconPath, int iconIndex)
    {
        var link = (IShellLinkW)new ShellLinkClass();
        try
        {
            ThrowIfFailed(((IPersistFile)link).Load(linkPath, StgmRead), "Load");
            ThrowIfFailed(link.SetIconLocation(iconPath ?? string.Empty, iconIndex), "SetIconLocation");
            Save(link, linkPath);
        }
        finally
        {
            Release(link);
        }
    }

    /// <summary>The 16x16 icon at <paramref name="iconPath"/>/<paramref name="index"/> (environment variables allowed), or null.</summary>
    public static unsafe Bitmap? LoadIcon(string iconPath, int index)
    {
        IntPtr small = IntPtr.Zero;
        if (NativeMethods.ExtractIconEx(Environment.ExpandEnvironmentVariables(iconPath), index, null, &small, 1) == 0 || small == IntPtr.Zero)
            return null;

        try
        {
            using Icon icon = Icon.FromHandle(small);
            return icon.ToBitmap();
        }
        finally
        {
            NativeMethods.DestroyIcon(small);
        }
    }

    /// <summary>
    /// A loaded shortcut keeps its file open until the COM object is released, which would otherwise wait for the
    /// garbage collector and make an immediate rename or delete of the file fail.
    /// </summary>
    private static void Release(IShellLinkW link) => Marshal.FinalReleaseComObject(link);

    private static void Save(IShellLinkW link, string linkPath) =>
        ThrowIfFailed(((IPersistFile)link).Save(linkPath, true), "Save");

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
            throw new IOException($"Shortcut {operation} failed (0x{hr:X8}).", hr);
    }
}
