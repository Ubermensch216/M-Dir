using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MDir.Integration;

internal enum AppColorMode
{
    /// <summary>Follow the Windows "app mode" setting.</summary>
    System,
    Light,
    Dark,
}

/// <summary>Dark mode and UI scale: what the settings mean and the Windows calls that apply them.</summary>
internal static partial class AppAppearance
{
    public static readonly int[] Scales = [100, 125, 150, 175, 200];

    private const int DwmaUseImmersiveDarkMode = 20;
    private const int DwmaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>The nearest allowed scale, so a hand-edited value cannot break the layout.</summary>
    public static int NormalizeScale(int percent) => Scales.MinBy(scale => Math.Abs(scale - percent));

    /// <summary>Whether Windows is set to dark apps (the "Choose your default app mode" setting).</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static bool IsDark(AppColorMode mode) => mode switch
    {
        AppColorMode.Dark => true,
        AppColorMode.Light => false,
        _ => SystemPrefersDark(),
    };

    /// <summary>Sets the color mode of every WinForms control created from now on (and, in .NET 10, the existing ones).</summary>
    public static void ApplyToProcess(AppColorMode mode)
    {
#pragma warning disable WFO5001 // Application.SetColorMode is marked experimental
        Application.SetColorMode(IsDark(mode) ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
    }

    /// <summary>Dark or light title bar for a top-level window.</summary>
    public static void SetTitleBar(IntPtr window, bool dark)
    {
        if (window == IntPtr.Zero)
            return;

        int value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(window, DwmaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(window, DwmaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
    }

    /// <summary>The dark scroll bars and header of a native list or tree ("DarkMode_Explorer" is the dark variant of "Explorer").</summary>
    public static void SetControlTheme(IntPtr control, bool dark)
    {
        if (control != IntPtr.Zero)
            SetWindowTheme(control, dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    /// <summary>The dark column header of a list ("DarkMode_ItemsView"); the light one is Explorer's "ItemsView".</summary>
    public static void SetHeaderTheme(IntPtr header, bool dark)
    {
        if (header != IntPtr.Zero)
            SetWindowTheme(header, dark ? "DarkMode_ItemsView" : "ItemsView", null);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
}
