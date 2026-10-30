using System.Runtime.InteropServices;

namespace MDir.Shell;

[StructLayout(LayoutKind.Sequential)]
internal struct CmInvokeCommandInfo
{
    public uint Size;
    public uint Mask;
    public IntPtr Window;
    public IntPtr Verb;
    public IntPtr Parameters;
    public IntPtr Directory;
    public int Show;
    public uint HotKey;
    public IntPtr Icon;
}

[ComImport]
[Guid("000214E4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
    [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfo info);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
}

/// <summary>The shell's own context menu of a list's empty area, which is where "Undo copy" / "Undo move" lives.</summary>
internal static partial class BackgroundMenu
{
    private const uint SvgioBackground = 0x0;
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;
    private const uint GcsVerbW = 4;
    private const int ShowNormal = 1;
    private static readonly Guid IidContextMenu = new("000214E4-0000-0000-C000-000000000046");

    /// <summary>
    /// Runs the "undo" entry of the view's background menu (the shell remembers the file operations of this program
    /// that can be undone). Returns false when the view has no such entry. The entry is there even when there is
    /// nothing to undo (it then does nothing), so its presence says only that the shell can undo.
    /// </summary>
    public static bool InvokeVerb(IShellView view, IntPtr owner, string verb, bool invoke = true)
    {
        Guid iid = IidContextMenu;
        if (view.GetItemObject(SvgioBackground, ref iid, out IntPtr unknown) < 0 || unknown == IntPtr.Zero)
            return false;

        IntPtr menu = CreatePopupMenu();
        try
        {
            var context = (IContextMenu)Marshal.GetObjectForIUnknown(unknown);
            if (context.QueryContextMenu(menu, 0, FirstCommand, LastCommand, 0) < 0)
                return false;

            int count = GetMenuItemCount(menu);
            for (int position = 0; position < count; position++)
            {
                uint id = GetMenuItemID(menu, position);
                if (id < FirstCommand || id > LastCommand || !string.Equals(VerbOf(context, id - FirstCommand), verb, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!invoke)
                    return true;

                var info = new CmInvokeCommandInfo
                {
                    Size = (uint)Marshal.SizeOf<CmInvokeCommandInfo>(),
                    Window = owner,
                    Verb = (IntPtr)(id - FirstCommand),
                    Show = ShowNormal,
                };
                return context.InvokeCommand(ref info) >= 0;
            }

            return false;
        }
        finally
        {
            DestroyMenu(menu);
            Marshal.Release(unknown);
        }
    }

    private static string? VerbOf(IContextMenu context, uint offset)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return context.GetCommandString((UIntPtr)offset, GcsVerbW, IntPtr.Zero, buffer, 256) >= 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemCount(IntPtr menu);

    [LibraryImport("user32.dll")]
    private static partial uint GetMenuItemID(IntPtr menu, int position);


}
