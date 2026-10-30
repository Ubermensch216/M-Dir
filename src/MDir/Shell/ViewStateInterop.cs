using System.Runtime.InteropServices;
using MDir.Features;

namespace MDir.Shell;

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid format, uint id)
{
    public Guid Format = format;
    public uint Id = id;

    public static implicit operator PropertyKey(ViewKey key) => new(key.Format, key.Id);

    public static implicit operator ViewKey(PropertyKey key) => new(key.Format, key.Id);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CmColumnInfo
{
    public uint Size;
    public uint Mask;
    public uint State;
    public uint Width;
    public uint DefaultWidth;
    public uint IdealWidth;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string Name;
}

/// <summary>The shell view's column manager: which columns are shown, in which order, how wide.</summary>
[ComImport]
[Guid("D8EC27BB-3F3B-4042-B10A-4ACFD924D453")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IColumnManager
{
    [PreserveSig] int SetColumnInfo(ref PropertyKey propkey, ref CmColumnInfo info);
    [PreserveSig] int GetColumnInfo(ref PropertyKey propkey, ref CmColumnInfo info);
    [PreserveSig] int GetColumnCount(uint flags, out uint count);
    [PreserveSig] int GetColumns(uint flags, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] PropertyKey[] keys, uint count);
    [PreserveSig] int SetColumns([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] PropertyKey[] keys, uint count);
}

[ComImport]
[Guid("0000010C-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersist
{
    [PreserveSig] int GetClassID(out Guid classId);
}

/// <summary>Reads and writes the columns and sort order of a shell view.</summary>
internal static class ViewStateInterop
{
    private const uint EnumVisible = 2;
    private const uint MaskWidth = 1;
    private static readonly Guid IidPersist = new("0000010C-0000-0000-C000-000000000046");

    /// <summary>
    /// What kind of folder the view shows (the class id of its shell folder: one for file system folders, others for
    /// This PC, the Recycle Bin and so on). Settings are kept per kind because their columns differ.
    /// </summary>
    public static Guid? GetFolderKind(IShellView view)
    {
        if (view is not IFolderView2 folderView)
            return null;

        Guid iid = IidPersist;
        if (folderView.GetFolder(ref iid, out IntPtr folder) < 0 || folder == IntPtr.Zero)
            return null;

        try
        {
            return ((IPersist)Marshal.GetObjectForIUnknown(folder)).GetClassID(out Guid kind) >= 0 ? kind : null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        finally
        {
            Marshal.Release(folder);
        }
    }

    public static FolderViewState? Capture(IShellView view)
    {
        var columns = new List<ColumnState>();
        if (view is IColumnManager manager && manager.GetColumnCount(EnumVisible, out uint count) >= 0 && count is > 0 and <= FolderViewState.MaxColumns)
        {
            var keys = new PropertyKey[count];
            if (manager.GetColumns(EnumVisible, keys, count) >= 0)
            {
                foreach (PropertyKey key in keys)
                {
                    PropertyKey copy = key;
                    var info = new CmColumnInfo { Size = (uint)Marshal.SizeOf<CmColumnInfo>(), Mask = MaskWidth, Name = string.Empty };
                    int width = manager.GetColumnInfo(ref copy, ref info) >= 0 ? (int)Math.Min(info.Width, FolderViewState.MaxWidth) : 0;
                    columns.Add(new ColumnState(key, width));
                }
            }
        }

        return columns.Count == 0 && ReadSort(view).Count == 0 ? null : new FolderViewState(columns, ReadSort(view));
    }

    private static List<SortState> ReadSort(IShellView view)
    {
        var sort = new List<SortState>();
        if (view is not IFolderView2 folderView || folderView.GetSortColumnCount(out int count) < 0 || count is <= 0 or > FolderViewState.MaxSortColumns)
            return sort;

        // SORTCOLUMN: a PROPERTYKEY (20 bytes) followed by the direction (4 bytes).
        const int entry = 24;
        IntPtr buffer = Marshal.AllocCoTaskMem(entry * count);
        try
        {
            if (folderView.GetSortColumns(buffer, count) < 0)
                return sort;

            for (int i = 0; i < count; i++)
            {
                var key = Marshal.PtrToStructure<PropertyKey>(buffer + i * entry);
                int direction = Marshal.ReadInt32(buffer + i * entry + 20);
                sort.Add(new SortState(key, direction >= 0 ? 1 : -1));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }

        return sort;
    }

    /// <summary>
    /// Shows the columns and sorts the way <paramref name="state"/> says. Columns this kind of folder does not have
    /// are ignored by the shell; a failing call leaves that part as it was.
    /// </summary>
    public static void Apply(IShellView view, FolderViewState state)
    {
        if (state.Columns.Count > 0 && view is IColumnManager manager)
        {
            PropertyKey[] keys = [.. state.Columns.Select(column => (PropertyKey)column.Key)];
            if (manager.SetColumns(keys, (uint)keys.Length) >= 0)
            {
                foreach (ColumnState column in state.Columns.Where(column => column.Width > 0))
                {
                    PropertyKey key = column.Key;
                    var info = new CmColumnInfo { Size = (uint)Marshal.SizeOf<CmColumnInfo>(), Mask = MaskWidth, Width = (uint)column.Width, Name = string.Empty };
                    manager.SetColumnInfo(ref key, ref info);
                }
            }
        }

        if (state.Sort.Count > 0 && view is IFolderView2 folderView)
        {
            const int entry = 24;
            IntPtr buffer = Marshal.AllocCoTaskMem(entry * state.Sort.Count);
            try
            {
                for (int i = 0; i < state.Sort.Count; i++)
                {
                    Marshal.StructureToPtr((PropertyKey)state.Sort[i].Key, buffer + i * entry, fDeleteOld: false);
                    Marshal.WriteInt32(buffer + i * entry + 20, state.Sort[i].Direction);
                }

                folderView.SetSortColumns(buffer, state.Sort.Count);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }
}
