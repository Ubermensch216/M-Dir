using System.Runtime.InteropServices;
using MDir.Features;

namespace MDir.Shell;

/// <summary>What a file list needs to show folder sizes in its Size column.</summary>
internal interface IFolderSizeSource
{
    /// <summary>The column is switched on.</summary>
    bool Enabled { get; }

    /// <summary>The file system folder the list shows, or null.</summary>
    string? CurrentFolder { get; }

    /// <summary>The text for the folder at <paramref name="folderPath"/>; asks for the size to be worked out when it is not known yet. Null draws nothing.</summary>
    string? GetText(string folderPath);

    /// <summary>A size text was drawn (for tests and diagnostics).</summary>
    void Drawn();
}

/// <summary>Text output on a device context handed out by a custom-draw notification.</summary>
internal static partial class GdiText
{
    private const uint DtRight = 0x2;
    private const uint DtVCenter = 0x4;
    private const uint DtSingleLine = 0x20;
    private const uint DtNoPrefix = 0x800;
    private const uint DtEndEllipsis = 0x8000;
    private const int Transparent = 1;

    /// <summary>Draws <paramref name="text"/> right-aligned and vertically centred in <paramref name="area"/> with the font of the DC.</summary>
    public static void Draw(IntPtr hdc, string text, ref NativeRect area, uint color)
    {
        int oldMode = SetBkMode(hdc, Transparent);
        uint oldColor = SetTextColor(hdc, color);
        DrawText(hdc, text, text.Length, ref area, DtRight | DtVCenter | DtSingleLine | DtNoPrefix | DtEndEllipsis);
        SetTextColor(hdc, oldColor);
        SetBkMode(hdc, oldMode);
    }

    /// <summary>Sets the text colour of a DC (a custom-draw handler uses it for the text the control draws next).</summary>
    public static void SetColor(IntPtr hdc, uint color) => SetTextColor(hdc, color);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DrawText(IntPtr hdc, string text, int length, ref NativeRect area, uint format);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(IntPtr hdc, uint color);
}

[ComImport]
[Guid("6F79D558-3E96-4549-A1D1-7D75D2288814")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyDescription
{
    [PreserveSig] int GetPropertyKey(out PropertyKey key);
    [PreserveSig] int GetCanonicalName(out IntPtr name);
    [PreserveSig] int GetPropertyType(out ushort type);
    [PreserveSig] int GetDisplayName(out IntPtr name);
}

/// <summary>The names the shell shows for properties in the language of the system (the header of the "Size" column...).</summary>
internal static partial class PropertyDescriptions
{
    /// <summary>System.Size.</summary>
    public static readonly ViewKey Size = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 12);

    private static string? _sizeLabel;
    private static bool _sizeLabelRead;

    /// <summary>The header text of the Size column, e.g. "크기" or "Size"; null when the property system does not say.</summary>
    public static string? SizeLabel
    {
        get
        {
            if (!_sizeLabelRead)
            {
                _sizeLabel = DisplayName(Size);
                _sizeLabelRead = true;
            }

            return _sizeLabel;
        }
    }

    public static string? DisplayName(ViewKey key)
    {
        PropertyKey native = key;
        Guid iid = typeof(IPropertyDescription).GUID;
        if (PSGetPropertyDescription(ref native, ref iid, out IntPtr description) < 0 || description == IntPtr.Zero)
            return null;

        try
        {
            var property = (IPropertyDescription)Marshal.GetObjectForIUnknown(description);
            if (property.GetDisplayName(out IntPtr name) < 0 || name == IntPtr.Zero)
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
        finally
        {
            Marshal.Release(description);
        }
    }

    [LibraryImport("propsys.dll")]
    private static partial int PSGetPropertyDescription(ref PropertyKey key, ref Guid riid, out IntPtr description);
}
