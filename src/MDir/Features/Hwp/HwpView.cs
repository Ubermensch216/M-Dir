using System.Reflection;

namespace MDir.Features.Hwp;

/// <summary>
/// Shows an <see cref="HwpPreview"/> in the WebBrowser control, zoomed (the browser's own zoom, like Ctrl+wheel) so a
/// sheet fits the width of the pane, never above its real size. Links and dropped files do not navigate anywhere; the
/// page has no script.
/// </summary>
internal sealed class HwpView : WebBrowser
{
    private const int OlecmdidOpticalZoom = 63;
    private const int OlecmdexecoptDontPromptUser = 2;

    private readonly double _sheetWidthPx;
    private bool _loaded;
    private bool _fitted;

    public HwpView(HwpPreview preview)
    {
        _sheetWidthPx = preview.SheetWidthPx;
        ScriptErrorsSuppressed = true;
        IsWebBrowserContextMenuEnabled = false;
        AllowWebBrowserDrop = false;
        WebBrowserShortcutsEnabled = true;
        DocumentCompleted += (_, _) =>
        {
            // Opening the drawn page is the only navigation: nothing on it may go anywhere else.
            _loaded = true;
            AllowNavigation = false;
            Fit();
        };
        Navigate(new Uri(preview.HtmlPath));
    }

    /// <summary>The zoom last applied, in percent (100 = real size).</summary>
    internal int ZoomPercent { get; private set; } = 100;

    internal bool Loaded => _loaded;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_loaded)
            Fit();
    }

    private void Fit()
    {
        int width = ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
        if (width <= 0 || ActiveXInstance is not { } browser)
            return;

        int percent = (int)Math.Clamp(width * 100 / _sheetWidthPx, 20, 100);
        if (percent == ZoomPercent && _fitted)
            return;

        try
        {
            object zoom = percent;
            object? output = null;
            browser.GetType().InvokeMember("ExecWB", BindingFlags.InvokeMethod, null, browser,
                [OlecmdidOpticalZoom, OlecmdexecoptDontPromptUser, zoom, output]);
            ZoomPercent = percent;
            _fitted = true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or System.Runtime.InteropServices.COMException or MissingMethodException)
        {
            // An old browser engine without zoom: the page shows at its real size with a scroll bar.
        }
    }
}
