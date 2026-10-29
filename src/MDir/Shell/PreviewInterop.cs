using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MDir.Shell;

[ComImport]
[Guid("8895B1C6-B41F-4C1C-A562-0D564250836F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    [PreserveSig] int SetWindow(IntPtr hwnd, ref NativeRect prc);
    [PreserveSig] int SetRect(ref NativeRect prc);
    [PreserveSig] int DoPreview();
    [PreserveSig] int Unload();
    [PreserveSig] int SetFocus();
    [PreserveSig] int QueryFocus(out IntPtr phwnd);
    [PreserveSig] int TranslateAccelerator(ref NativeMsg pmsg);
}

[ComImport]
[Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    [PreserveSig] int Initialize(IStream pstream, uint grfMode);
}

[ComImport]
[Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithItem
{
    [PreserveSig] int Initialize(IShellItem psi, uint grfMode);
}

[ComImport]
[Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

/// <summary>Finds and creates the shell's preview handler for a file type.</summary>
internal static class PreviewHandlers
{
    private const string PreviewHandlerCategory = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const uint AssocfInitIgnoreUnknown = 0x400;
    private const uint AssocstrShellExtension = 16;
    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;
    private const uint StgmReadShareDenyNone = 0x40;

    private static readonly Guid IidPreviewHandler = new("8895B1C6-B41F-4C1C-A562-0D564250836F");

    /// <summary>The CLSID of the preview handler registered for <paramref name="extension"/> (".pdf"), or null.</summary>
    public static unsafe Guid? FindClsid(string extension)
    {
        if (extension.Length < 2 || extension[0] != '.')
            return null;

        char* buffer = stackalloc char[260];
        uint length = 260;
        if (NativeMethods.AssocQueryString(AssocfInitIgnoreUnknown, AssocstrShellExtension, extension, PreviewHandlerCategory, buffer, ref length) != 0)
            return null;

        return Guid.TryParse(new string(buffer), out Guid clsid) ? clsid : null;
    }

    /// <summary>The registered display name of a handler class (for the status line), or null.</summary>
    public static string? GetName(Guid clsid)
    {
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}");
        return key?.GetValue(null) as string;
    }

    /// <summary>
    /// Creates the handler. Out of process first (<c>CLSCTX_LOCAL_SERVER</c>, which uses the preview surrogate for
    /// handlers registered for it, as Explorer does) so a faulty handler cannot take this program down; handlers
    /// without a surrogate are loaded in process.
    /// </summary>
    public static IPreviewHandler? Create(Guid clsid)
    {
        Guid iid = IidPreviewHandler;
        foreach (uint context in new[] { ClsctxLocalServer, ClsctxInprocServer })
        {
            Guid id = clsid;
            if (NativeMethods.CoCreateInstance(ref id, IntPtr.Zero, context, ref iid, out IntPtr instance) >= 0 && instance != IntPtr.Zero)
            {
                try
                {
                    return (IPreviewHandler)Marshal.GetObjectForIUnknown(instance);
                }
                finally
                {
                    Marshal.Release(instance);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Initializes a handler with a file the way the shell prefers: a stream (which also works for handlers in the
    /// restricted surrogate process), else a shell item, else the file path.
    /// </summary>
    public static bool Initialize(object handler, string path)
    {
        if (handler is IInitializeWithStream withStream && OpenStream(path) is { } stream)
        {
            if (withStream.Initialize(stream, 0) >= 0)
                return true;
        }

        if (handler is IInitializeWithItem withItem && ShellLocation.Parse(path) is { } location && ShellItems.FromLocation(location) is { } item
            && withItem.Initialize(item, 0) >= 0)
            return true;

        return handler is IInitializeWithFile withFile && withFile.Initialize(path, 0) >= 0;
    }

    private static IStream? OpenStream(string path)
    {
        if (NativeMethods.SHCreateStreamOnFileEx(path, StgmReadShareDenyNone, 0, false, IntPtr.Zero, out IntPtr stream) < 0 || stream == IntPtr.Zero)
            return null;

        try
        {
            return (IStream)Marshal.GetObjectForIUnknown(stream);
        }
        finally
        {
            Marshal.Release(stream);
        }
    }
}
