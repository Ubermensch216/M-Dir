namespace MDir.Shell;

/// <summary>Shift + mouse wheel scrolls a file list sideways (the shell list only scrolls vertically with the wheel).</summary>
internal static class ListScroll
{
    public const int WM_MOUSEWHEEL = 0x020A;
    private const uint LVM_SCROLL = 0x1014;
    private const int MK_SHIFT = 0x0004;
    private const int WheelDelta = 120;

    /// <summary>Pixels moved for one notch of the wheel.</summary>
    public const int PixelsPerNotch = 48;

    /// <summary>
    /// Handles a <c>WM_MOUSEWHEEL</c> sent to <paramref name="window"/>: when Shift is held and the window is a list
    /// that can scroll sideways, scrolls it and returns true. Anything else returns false and is left alone.
    /// </summary>
    public static bool TryScrollSideways(IntPtr window, IntPtr wParam)
    {
        long keys = (long)wParam & 0xFFFF;
        if ((keys & MK_SHIFT) == 0 || NativeMethods.GetClassName(window) != "SysListView32")
            return false;

        int delta = (short)(((long)wParam >> 16) & 0xFFFF);
        int pixels = -(int)Math.Round((double)delta / WheelDelta * PixelsPerNotch);
        return pixels != 0 && NativeMethods.SendMessage(window, LVM_SCROLL, (IntPtr)pixels, IntPtr.Zero) != IntPtr.Zero;
    }
}
