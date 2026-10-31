using System.Runtime.InteropServices;
using MDir.Features;
using MDir.Integration;

namespace MDir.Shell;

/// <summary>
/// Colours the items of a shell view's list by colour rules, the way Q-Dir does: the shell view window (DefView) is
/// subclassed and the list's <c>NM_CUSTOMDRAW</c> notifications are answered with per-item text/background colours
/// (<c>CDDS_ITEMPREPAINT</c> → set <c>clrText</c>/<c>clrTextBk</c>, return <c>CDRF_NEWFONT</c>). Needs the classic
/// list layout (<c>FVO_VISTALAYOUT</c>); the DirectUI list does not send these notifications.
/// </summary>
internal sealed unsafe class ColorListHook : NativeWindow, IDisposable
{
    private const int WM_NOTIFY = 0x004E;
    private const int NM_CUSTOMDRAW = -12;
    private const int CDDS_PREPAINT = 0x1;
    private const int CDDS_ITEMPREPAINT = 0x10001;
    private const int CDDS_ITEMPOSTPAINT = 0x10002;
    private const int CDRF_NEWFONT = 0x2;
    private const int CDRF_NOTIFYPOSTPAINT = 0x10;
    private const int CDRF_NOTIFYITEMDRAW = 0x20;
    private const uint LVM_GETSUBITEMRECT = 0x1038;
    private const uint LVM_GETCOLUMNW = 0x105F;
    private const uint LVM_GETTEXTCOLOR = 0x1023;
    private const uint LVCF_TEXT = 0x4;
    private const int MaxColumns = 64;
    private const int LvisSelected = 0x2;
    private const int FwBold = 700;
    private const uint ClrNone = 0xFFFFFFFF;
    private const uint DarkBackground = 0x00202020;
    private const uint DarkText = 0x00E6E6E6;

    private readonly IShellView _view;
    private readonly Func<ColorRuleSet?> _getRules;
    private readonly Func<bool> _isActivePane;
    private readonly Func<bool> _isDark;
    private readonly Func<bool> _hideSortHighlight;
    private IShellFolder? _folder;
    private IntPtr _list;
    private IntPtr _boldFont;
    private IntPtr _boldFontBase;
    private bool _backgroundApplied;
    private bool _darkApplied;
    private readonly IFolderSizeSource? _sizes;
    private int _sizeColumn = -1;

    public ColorListHook(IShellView view, Func<ColorRuleSet?> getRules, Func<bool> isActivePane, Func<bool>? isDark = null, Func<bool>? hideSortHighlight = null,
        IFolderSizeSource? sizes = null)
    {
        _sizes = sizes;
        _hideSortHighlight = hideSortHighlight ?? (() => false);
        _view = view;
        _getRules = getRules;
        _isActivePane = isActivePane;
        _isDark = isDark ?? (() => false);
        if (view.GetWindow(out IntPtr window) >= 0 && window != IntPtr.Zero)
            AssignHandle(window);
    }

    /// <summary>Re-applies list background colours and repaints (rules or active pane changed).</summary>
    public void RefreshAppearance()
    {
        if (Handle == IntPtr.Zero)
            return;

        IntPtr list = _list != IntPtr.Zero ? _list : NativeMethods.FindDescendant(Handle, "SysListView32");
        if (list != IntPtr.Zero)
        {
            NativeMethods.SendMessage(list, NativeMethods.LVM_ENABLEGROUPVIEW, IntPtr.Zero, IntPtr.Zero);
            ApplyDark(list, force: true);
            ApplySortHighlight(list);
            ApplyBackground(list, _getRules());
            NativeMethods.RedrawWindow(list, IntPtr.Zero, IntPtr.Zero, NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ERASE | NativeMethods.RDW_ALLCHILDREN);
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            ReleaseHandle();

        if (_boldFont != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_boldFont);
            _boldFont = IntPtr.Zero;
        }

        _folder = null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NOTIFY)
        {
            var header = (NmHdr*)m.LParam;
            if (header->Code == NM_CUSTOMDRAW && IsList(header->HwndFrom))
            {
                ApplyDark(header->HwndFrom);
                ApplySortHighlight(header->HwndFrom);
                ColorRuleSet? rules = _getRules();
                bool sizes = _sizes is { Enabled: true };
                if (rules is null && !sizes)
                {
                    base.WndProc(ref m);
                    return;
                }

                // Let the shell view handle it first (it colours compressed/encrypted files), then override.
                base.WndProc(ref m);
                OnCustomDraw(ref m, rules, sizes);
                return;
            }
        }

        base.WndProc(ref m);
    }

    private void OnCustomDraw(ref Message m, ColorRuleSet? rules, bool sizes)
    {
        var draw = (NmLvCustomDraw*)m.LParam;
        int stage = draw->Nmcd.DrawStage;
        int result = (int)m.Result;

        if (stage == CDDS_PREPAINT)
        {
            if (rules is not null)
                ApplyBackground(draw->Nmcd.Hdr.HwndFrom, rules);
            _sizeColumn = sizes ? FindSizeColumn(draw->Nmcd.Hdr.HwndFrom) : -1;
            m.Result = (IntPtr)(result | CDRF_NOTIFYITEMDRAW);
            return;
        }

        // After an item is drawn, the folder size goes into its (empty) Size cell.
        if (stage == CDDS_ITEMPOSTPAINT)
        {
            if (sizes && _sizeColumn > 0)
                DrawFolderSize(draw, rules);
            return;
        }

        // Item (and sub-item) pre-paint. Selected rows keep the system selection colours so they stay readable.
        // The list's uItemState is not a reliable "selected" flag (it reported CDIS_SELECTED for every row), so ask the list.
        if ((stage & CDDS_ITEMPREPAINT) != CDDS_ITEMPREPAINT)
            return;

        int notifyPost = sizes && _sizeColumn > 0 ? CDRF_NOTIFYPOSTPAINT : 0;
        int index = (int)draw->Nmcd.ItemSpec;
        nint itemState = NativeMethods.SendMessage(draw->Nmcd.Hdr.HwndFrom, NativeMethods.LVM_GETITEMSTATE, (IntPtr)index, (IntPtr)LvisSelected);
        if (rules is null || (itemState & LvisSelected) != 0 || Evaluate(index, rules) is not { } style)
        {
            if (notifyPost != 0)
                m.Result = (IntPtr)(result | notifyPost);
            return;
        }

        if (style.Text is { } text)
            draw->ClrText = ToColorRef(text);
        if (style.Background is { } background)
            draw->ClrTextBk = ToColorRef(background);
        if (style.Bold && GetBoldFont(draw->Nmcd.Hdr.HwndFrom) is var font && font != IntPtr.Zero)
            NativeMethods.SelectObject(draw->Nmcd.Hdc, font);

        m.Result = (IntPtr)(result | CDRF_NEWFONT | notifyPost);
    }

    /// <summary>Draws the size of a folder item into the right-aligned Size cell, in the item's text colour.</summary>
    private void DrawFolderSize(NmLvCustomDraw* draw, ColorRuleSet? rules)
    {
        IntPtr list = draw->Nmcd.Hdr.HwndFrom;
        int index = (int)draw->Nmcd.ItemSpec;
        (string? name, bool isFolder, bool hidden) = ReadItem(index);
        if (_sizes?.CurrentFolder is not { } directory || !isFolder || name is null)
            return;

        string? text = _sizes.GetText(Path.Combine(directory, name));
        if (text is null)
            return;

        var cell = new NativeRect(0, _sizeColumn, 0, 0);
        if (NativeMethods.SendMessage(list, LVM_GETSUBITEMRECT, (IntPtr)index, (IntPtr)(&cell)) == IntPtr.Zero || cell.Right - cell.Left < 24)
            return;

        var area = new NativeRect(cell.Left + 4, cell.Top, cell.Right - 6, cell.Bottom);
        uint color = rules?.Resolve(name, true, hidden)?.Text is { } styled
            ? ToColorRef(styled)
            : ListTextColor(list);
        GdiText.Draw(draw->Nmcd.Hdc, text, ref area, color);
        _sizes.Drawn();
    }

    private static uint ListTextColor(IntPtr list)
    {
        uint color = (uint)NativeMethods.SendMessage(list, LVM_GETTEXTCOLOR, IntPtr.Zero, IntPtr.Zero);
        return color == ClrNone ? (uint)NativeMethods.GetSysColor(NativeMethods.COLOR_WINDOWTEXT) : color;
    }

    /// <summary>The list column (sub-item number) headed with the localized "Size", or -1 when it is not shown.</summary>
    private int FindSizeColumn(IntPtr list)
    {
        string[] labels = [.. new[] { PropertyDescriptions.SizeLabel, "크기", "Size" }.OfType<string>()];
        char* buffer = stackalloc char[260];
        for (int column = 0; column < MaxColumns; column++)
        {
            buffer[0] = '\0';
            var info = new ListColumn { Mask = LVCF_TEXT, Text = buffer, TextMax = 260 };
            if (NativeMethods.SendMessage(list, LVM_GETCOLUMNW, (IntPtr)column, (IntPtr)(&info)) == IntPtr.Zero)
                break;

            string header = new(buffer);
            if (labels.Any(label => string.Equals(label, header, StringComparison.OrdinalIgnoreCase)))
                return column;
        }

        return -1;
    }

    /// <summary>Name and attributes of list item <paramref name="index"/>, run through the rules.</summary>
    private ColorStyle? Evaluate(int index, ColorRuleSet rules) =>
        ReadItem(index) is var (name, isFolder, hidden) && name is not null ? rules.Resolve(name, isFolder, hidden) : null;

    /// <summary>The file name, whether it is a folder and whether it is hidden, for list item <paramref name="index"/>.</summary>
    private (string? Name, bool IsFolder, bool Hidden) ReadItem(int index)
    {
        if (_view is not IFolderView2 folderView || folderView.Item(index, out IntPtr pidl) < 0 || pidl == IntPtr.Zero)
            return (null, false, false);

        try
        {
            _folder ??= ShellFolderItems.GetFolder(folderView);
            if (_folder is null || ShellFolderItems.GetName(_folder, pidl) is not { } name)
                return (null, false, false);

            uint attributes = ShellFolderItems.GetAttributes(_folder, pidl, ShellNames.AttrFolder | ShellNames.AttrHidden | ShellNames.AttrGhosted);
            return (name, (attributes & ShellNames.AttrFolder) != 0, (attributes & (ShellNames.AttrHidden | ShellNames.AttrGhosted)) != 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private bool IsList(IntPtr window)
    {
        if (window == _list)
            return true;

        if (NativeMethods.GetClassName(window) != "SysListView32")
            return false;

        _list = window;
        return true;
    }

    /// <summary>Q-Dir's #BG / #BG-A: a list background for inactive and for the active pane.</summary>
    private void ApplyBackground(IntPtr list, ColorRuleSet? rules)
    {
        Color? wanted = rules is null ? null : _isActivePane() ? rules.ActiveBackground ?? rules.InactiveBackground : rules.InactiveBackground;
        if (wanted is null && !_backgroundApplied)
            return;

        uint color = wanted is { } c ? ToColorRef(c) : _isDark() ? DarkBackground : (uint)NativeMethods.GetSysColor(NativeMethods.COLOR_WINDOW);
        if ((uint)NativeMethods.SendMessage(list, NativeMethods.LVM_GETBKCOLOR, IntPtr.Zero, IntPtr.Zero) == color)
            return;

        NativeMethods.SendMessage(list, NativeMethods.LVM_SETBKCOLOR, IntPtr.Zero, (IntPtr)color);
        NativeMethods.SendMessage(list, NativeMethods.LVM_SETTEXTBKCOLOR, IntPtr.Zero, (IntPtr)color);
        _backgroundApplied = wanted is not null;
    }

    /// <summary>
    /// The list tints the column it is sorted by (a light stripe that looks wrong on a dark list). The shell view
    /// selects that column again after every sort, so the selection is cleared on each paint while it is unwanted.
    /// </summary>
    private void ApplySortHighlight(IntPtr list)
    {
        if (!_hideSortHighlight() && !_isDark())
            return;

        if ((int)NativeMethods.SendMessage(list, NativeMethods.LVM_GETSELECTEDCOLUMN, IntPtr.Zero, IntPtr.Zero) != -1)
            NativeMethods.SendMessage(list, NativeMethods.LVM_SETSELECTEDCOLUMN, (IntPtr)(-1), IntPtr.Zero);
    }

    /// <summary>Dark scroll bars, background and text for the list (and the way back to the system colours).</summary>
    private void ApplyDark(IntPtr list, bool force = false)
    {
        bool dark = _isDark();
        uint background = dark ? DarkBackground : (uint)NativeMethods.GetSysColor(NativeMethods.COLOR_WINDOW);
        uint text = dark ? DarkText : (uint)NativeMethods.GetSysColor(NativeMethods.COLOR_WINDOWTEXT);

        if (!force && dark == _darkApplied)
        {
            if ((uint)NativeMethods.SendMessage(list, NativeMethods.LVM_GETBKCOLOR, IntPtr.Zero, IntPtr.Zero) != background)
            {
                NativeMethods.SendMessage(list, NativeMethods.LVM_SETBKCOLOR, IntPtr.Zero, (IntPtr)background);
                NativeMethods.SendMessage(list, NativeMethods.LVM_SETTEXTBKCOLOR, IntPtr.Zero, (IntPtr)background);
                NativeMethods.SendMessage(list, NativeMethods.LVM_SETTEXTCOLOR, IntPtr.Zero, (IntPtr)text);
            }
            return;
        }

        _darkApplied = dark;
        AppAppearance.SetControlTheme(list, dark);
        AppAppearance.SetHeaderTheme(NativeMethods.SendMessage(list, NativeMethods.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero), dark);
        NativeMethods.SendMessage(list, NativeMethods.LVM_SETBKCOLOR, IntPtr.Zero, (IntPtr)background);
        NativeMethods.SendMessage(list, NativeMethods.LVM_SETTEXTBKCOLOR, IntPtr.Zero, (IntPtr)background);
        NativeMethods.SendMessage(list, NativeMethods.LVM_SETTEXTCOLOR, IntPtr.Zero, (IntPtr)text);
        NativeMethods.RedrawWindow(list, IntPtr.Zero, IntPtr.Zero, NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ERASE | NativeMethods.RDW_ALLCHILDREN);
    }

    private IntPtr GetBoldFont(IntPtr list)
    {
        IntPtr baseFont = NativeMethods.SendMessage(list, NativeMethods.WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
        if (_boldFont != IntPtr.Zero && _boldFontBase == baseFont)
            return _boldFont;

        if (_boldFont != IntPtr.Zero)
            NativeMethods.DeleteObject(_boldFont);

        _boldFont = IntPtr.Zero;
        _boldFontBase = baseFont;
        LogFont font = default;
        if (baseFont != IntPtr.Zero && NativeMethods.GetObject(baseFont, sizeof(LogFont), &font) > 0)
        {
            font.Weight = FwBold;
            _boldFont = NativeMethods.CreateFontIndirect(&font);
        }

        return _boldFont;
    }

    private static uint ToColorRef(Color color) => (uint)(color.R | (color.G << 8) | (color.B << 16));

    [StructLayout(LayoutKind.Sequential)]
    private struct ListColumn
    {
        public uint Mask;
        public int Format;
        public int Width;
        public char* Text;
        public int TextMax;
        public int SubItem;
        public int Image;
        public int Order;
        public int MinWidth;
        public int DefaultWidth;
        public int IdealWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NmHdr
    {
        public IntPtr HwndFrom;
        public UIntPtr IdFrom;
        public int Code;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NmCustomDraw
    {
        public NmHdr Hdr;
        public int DrawStage;
        public IntPtr Hdc;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public UIntPtr ItemSpec;
        public uint ItemState;
        public IntPtr ItemParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NmLvCustomDraw
    {
        public NmCustomDraw Nmcd;
        public uint ClrText;
        public uint ClrTextBk;
        public int SubItem;
    }
}
