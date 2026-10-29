using System.Runtime.InteropServices;
using MDir.Shell;

namespace MDir.Features;

/// <summary>
/// Shows a file with the shell's preview handler for its type (Office, PDF, text, HTML, Hancom...), the same
/// extension Explorer's preview pane uses. The handler paints into a window parented to this control.
/// </summary>
internal sealed class PreviewHandlerHost : Panel
{
    private IPreviewHandler? _handler;

    /// <summary>The registered name of the handler in use ("PDF Preview Handler"), or null.</summary>
    public string? HandlerName { get; private set; }

    public bool IsShowing => _handler is not null;

    /// <summary>True when the shell has a preview handler registered for <paramref name="path"/>'s extension.</summary>
    public static bool HasHandler(string path) => PreviewHandlers.FindClsid(Path.GetExtension(path)) is not null;

    /// <summary>
    /// Previews <paramref name="path"/>. Returns false (and shows nothing) when there is no handler for the type, it
    /// cannot be created, or it refuses the file. A previous preview is unloaded first.
    /// </summary>
    public bool TryShow(string path)
    {
        Unload();
        if (PreviewHandlers.FindClsid(Path.GetExtension(path)) is not { } clsid || PreviewHandlers.Create(clsid) is not { } handler)
            return false;

        try
        {
            if (!PreviewHandlers.Initialize(handler, path))
            {
                Release(handler);
                return false;
            }

            _ = Handle;
            NativeRect rect = Bounds0;
            if (handler.SetWindow(Handle, ref rect) < 0 || handler.DoPreview() < 0)
            {
                Unload(handler);
                return false;
            }

            _handler = handler;
            HandlerName = PreviewHandlers.GetName(clsid);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Unload(handler);
            return false;
        }
    }

    /// <summary>Releases the preview (and the file the handler holds open).</summary>
    public void Unload()
    {
        if (_handler is { } handler)
        {
            _handler = null;
            Unload(handler);
        }

        HandlerName = null;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_handler is { } handler)
        {
            NativeRect rect = Bounds0;
            handler.SetRect(ref rect);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Unload();

        base.Dispose(disposing);
    }

    private NativeRect Bounds0 => new(0, 0, ClientSize.Width, ClientSize.Height);

    private static void Unload(IPreviewHandler handler)
    {
        try
        {
            handler.Unload();
        }
        catch (COMException)
        {
        }

        Release(handler);
    }

    private static void Release(IPreviewHandler handler) => Marshal.FinalReleaseComObject(handler);
}
