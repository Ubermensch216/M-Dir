using System.Runtime.InteropServices;

namespace MDir.Shell;

/// <summary>
/// An immutable shell namespace location stored as a copied ITEMIDLIST.
/// Keeping the PIDL bytes (not a path) lets virtual folders such as "This PC" round-trip,
/// the same way Q-Dir stores folders in its .qdr workspace files.
/// </summary>
public sealed class ShellLocation : IEquatable<ShellLocation>
{
    private readonly byte[] _idList;
    private string? _displayName;
    private string? _name;

    private ShellLocation(byte[] idList) => _idList = idList;

    public static ShellLocation Desktop { get; } = new([0, 0]);

    /// <summary>True for the namespace root (an empty ITEMIDLIST).</summary>
    public bool IsDesktop => _idList.Length <= 2;

    public ReadOnlySpan<byte> IdList => _idList;

    /// <summary>File system path when there is one; otherwise the editable display name ("This PC").</summary>
    public string DisplayName => _displayName ??= GetName(Sigdn.FileSysPath) ?? GetName(Sigdn.DesktopAbsoluteEditing) ?? string.Empty;

    public string? FileSystemPath => GetName(Sigdn.FileSysPath);

    /// <summary>Short localized name as Explorer shows it ("Windows", "로컬 디스크 (C:)").</summary>
    public string Name => _name ??= GetName(Sigdn.NormalDisplay) ?? DisplayName;

    /// <summary>Parses a path, "shell:" folder or environment-variable string. Returns null when it doesn't resolve.</summary>
    public static ShellLocation? Parse(string text)
    {
        string name = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        if (name.Length == 0)
            return null;

        if (NativeMethods.SHParseDisplayName(name, IntPtr.Zero, out IntPtr pidl, 0, out _) < 0 || pidl == IntPtr.Zero)
            return null;

        try
        {
            return FromPidl(pidl);
        }
        finally
        {
            NativeMethods.ILFree(pidl);
        }
    }

    /// <summary>Copies a native PIDL; the caller keeps ownership of <paramref name="pidl"/>.</summary>
    public static ShellLocation FromPidl(IntPtr pidl)
    {
        var bytes = new byte[NativeMethods.ILGetSize(pidl)];
        Marshal.Copy(pidl, bytes, 0, bytes.Length);
        return new ShellLocation(bytes);
    }

    public static ShellLocation FromIdList(ReadOnlySpan<byte> idList) => new(idList.ToArray());

    /// <summary>For bytes read from a file: null unless they form a complete, terminated ITEMIDLIST.</summary>
    public static ShellLocation? TryFromIdList(ReadOnlySpan<byte> idList) => IsWellFormed(idList) ? FromIdList(idList) : null;

    private static bool IsWellFormed(ReadOnlySpan<byte> idList)
    {
        int offset = 0;
        while (offset + 2 <= idList.Length)
        {
            int size = idList[offset] | (idList[offset + 1] << 8);
            if (size == 0)
                return offset + 2 == idList.Length;
            if (size < 2)
                return false;

            offset += size;
        }

        return false;
    }

    /// <summary>
    /// This location, or its nearest parent that still exists, so a restored workspace never points at a deleted
    /// folder. Virtual folders and network paths are trusted (checking them can block for a long time).
    /// </summary>
    public ShellLocation ExistingOrParent()
    {
        ShellLocation location = this;
        while (location.FileSystemPath is { } path && !path.StartsWith(@"\\", StringComparison.Ordinal)
            && !Directory.Exists(path) && !File.Exists(path))
        {
            location = location.GetParent() ?? Desktop;
            if (location.IsDesktop)
                break;
        }

        return location;
    }

    public ShellLocation? GetParent()
    {
        if (IsDesktop)
            return null;

        using NativePidl pidl = ToNative();
        NativeMethods.ILRemoveLastID(pidl.Handle);
        return FromPidl(pidl.Handle);
    }

    /// <summary>The shell's description of this item's type ("텍스트 문서", "파일 폴더"), or null.</summary>
    public unsafe string? GetTypeName()
    {
        using NativePidl pidl = ToNative();
        var info = default(ShFileInfo);
        if (NativeMethods.SHGetFileInfo(pidl.Handle, 0, ref info, (uint)sizeof(ShFileInfo), NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_TYPENAME) == 0)
            return null;

        string name = new string(info.TypeName);
        return name.Length > 0 ? name : null;
    }

    /// <summary>The shell's small icon for this location (folder, drive, virtual folder), or null.</summary>
    public unsafe Bitmap? GetSmallIcon()
    {
        using NativePidl pidl = ToNative();
        var info = default(ShFileInfo);
        if (NativeMethods.SHGetFileInfo(pidl.Handle, 0, ref info, (uint)sizeof(ShFileInfo),
                NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON) == 0 || info.Icon == IntPtr.Zero)
            return null;

        try
        {
            using Icon icon = Icon.FromHandle(info.Icon);
            return icon.ToBitmap();
        }
        finally
        {
            NativeMethods.DestroyIcon(info.Icon);
        }
    }

    /// <summary>Allocates a CoTaskMem copy for APIs that take a PIDL.</summary>
    public NativePidl ToNative()
    {
        IntPtr handle = Marshal.AllocCoTaskMem(_idList.Length);
        Marshal.Copy(_idList, 0, handle, _idList.Length);
        return new NativePidl(handle);
    }

    private string? GetName(Sigdn sigdn)
    {
        using NativePidl pidl = ToNative();
        if (NativeMethods.SHGetNameFromIDList(pidl.Handle, sigdn, out IntPtr name) < 0)
            return null;

        try
        {
            return Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    public bool Equals(ShellLocation? other) => other is not null && _idList.AsSpan().SequenceEqual(other._idList);

    public override bool Equals(object? obj) => Equals(obj as ShellLocation);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_idList);
        return hash.ToHashCode();
    }

    public override string ToString() => DisplayName;
}

/// <summary>Owns a CoTaskMem-allocated PIDL.</summary>
public readonly struct NativePidl(IntPtr handle) : IDisposable
{
    public IntPtr Handle { get; } = handle;

    public void Dispose() => Marshal.FreeCoTaskMem(Handle);
}
