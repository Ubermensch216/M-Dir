using System.Runtime.InteropServices;
using MDir.Features;

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
    private const int CDRF_NEWFONT = 0x2;
    private const int CDRF_NOTIFYITEMDRAW = 0x20;
    private const int LvisSelected = 0x2;
    private const int FwBold = 700;
    private const uint ClrNone = 0xFFFFFFFF;

    private readonly IShellView _view;
    private readonly Func<ColorRuleSet?> _getRules;
    private readonly Func<bool> _isActivePane;
    private IShellFolder? _folder;
    private IntPtr _list;
    private IntPtr _boldFont;
    private IntPtr _boldFontBase;
    private bool _backgroundApplied;

    public ColorListHook(IShellView view, Func<ColorRuleSet?> getRules, Func<bool> isActivePane)
    {
        _view = view;
        _getRules = getRules;
        _isActivePane = isActivePane;
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
        if (m.Msg == WM_NOTIFY && _getRules() is { } rules)
        {
            var header = (NmHdr*)m.LParam;
            if (header->Code == NM_CUSTOMDRAW && IsList(header->HwndFrom))
            {
                // Let the shell view handle it first (it colours compressed/encrypted files), then override.
                base.WndProc(ref m);
                OnCustomDraw(ref m, rules);
                return;
            }
        }

        base.WndProc(ref m);
    }

    private void OnCustomDraw(ref Message m, ColorRuleSet rules)
    {
        var draw = (NmLvCustomDraw*)m.LParam;
        int stage = draw->Nmcd.DrawStage;
        int result = (int)m.Result;

        if (stage == CDDS_PREPAINT)
        {
            ApplyBackground(draw->Nmcd.Hdr.HwndFrom, rules);
            m.Result = (IntPtr)(result | CDRF_NOTIFYITEMDRAW);
            return;
        }

        // Item (and sub-item) pre-paint. Selected rows keep the system selection colours so they stay readable.
        // The list's uItemState is not a reliable "selected" flag (it reported CDIS_SELECTED for every row), so ask the list.
        if ((stage & CDDS_ITEMPREPAINT) != CDDS_ITEMPREPAINT)
            return;

        int index = (int)draw->Nmcd.ItemSpec;
        nint itemState = NativeMethods.SendMessage(draw->Nmcd.Hdr.HwndFrom, NativeMethods.LVM_GETITEMSTATE, (IntPtr)index, (IntPtr)LvisSelected);
        if ((itemState & LvisSelected) != 0 || Evaluate(index, rules) is not { } style)
            return;

        if (style.Text is { } text)
            draw->ClrText = ToColorRef(text);
        if (style.Background is { } background)
            draw->ClrTextBk = ToColorRef(background);
        if (style.Bold && GetBoldFont(draw->Nmcd.Hdr.HwndFrom) is var font && font != IntPtr.Zero)
            NativeMethods.SelectObject(draw->Nmcd.Hdc, font);

        m.Result = (IntPtr)(result | CDRF_NEWFONT);
    }

    /// <summary>Name and attributes of list item <paramref name="index"/>, run through the rules.</summary>
    private ColorStyle? Evaluate(int index, ColorRuleSet rules)
    {
        if (_view is not IFolderView2 folderView || folderView.Item(index, out IntPtr pidl) < 0 || pidl == IntPtr.Zero)
            return null;

        try
        {
            _folder ??= ShellFolderItems.GetFolder(folderView);
            if (_folder is null || ShellFolderItems.GetName(_folder, pidl) is not { } name)
                return null;

            uint attributes = ShellFolderItems.GetAttributes(_folder, pidl, ShellNames.AttrFolder | ShellNames.AttrHidden | ShellNames.AttrGhosted);
            return rules.Resolve(name, (attributes & ShellNames.AttrFolder) != 0, (attributes & (ShellNames.AttrHidden | ShellNames.AttrGhosted)) != 0);
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

        uint color = wanted is { } c ? ToColorRef(c) : (uint)NativeMethods.GetSysColor(NativeMethods.COLOR_WINDOW);
        if ((uint)NativeMethods.SendMessage(list, NativeMethods.LVM_GETBKCOLOR, IntPtr.Zero, IntPtr.Zero) == color)
            return;

        NativeMethods.SendMessage(list, NativeMethods.LVM_SETBKCOLOR, IntPtr.Zero, (IntPtr)color);
        NativeMethods.SendMessage(list, NativeMethods.LVM_SETTEXTBKCOLOR, IntPtr.Zero, (IntPtr)color);
        _backgroundApplied = wanted is not null;
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
