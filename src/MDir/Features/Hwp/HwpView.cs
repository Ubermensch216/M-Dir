using System.Reflection;

namespace MDir.Features.Hwp;

/// <summary>
/// Shows an <see cref="HwpPreview"/> in the WebBrowser control, fitted to the pane width by default, with manual
/// zoom (fit / 100% / zoom in / zoom out) and sheet navigation for multi-sheet documents. Links and dropped files do
/// not navigate anywhere; the page has no script.
/// </summary>
internal sealed class HwpView : WebBrowser
{
    private const int OlecmdidOpticalZoom = 63;
    private const int OlecmdexecoptDontPromptUser = 2;
    private const int MinZoom = 20;
    private const int MaxZoom = 300;

    private readonly double _sheetWidthPx;
    private readonly int _sheets;
    private bool _loaded;
    private bool _fitted;
    private int? _manualZoom;
    private int _currentSheet;

    public HwpView(HwpPreview preview)
    {
        _sheetWidthPx = preview.SheetWidthPx;
        _sheets = Math.Max(1, preview.Sheets);
        ScriptErrorsSuppressed = true;
        IsWebBrowserContextMenuEnabled = false;
        AllowWebBrowserDrop = false;
        WebBrowserShortcutsEnabled = true;
        DocumentCompleted += (_, _) =>
        {
            // Opening the drawn page is the only navigation: nothing on it may go anywhere else.
            _loaded = true;
            AllowNavigation = false;
            ApplyZoom();
        };
        Navigate(new Uri(preview.HtmlPath));
    }

    public event EventHandler? SheetChanged;

    /// <summary>The zoom last applied, in percent (100 = real size).</summary>
    internal int ZoomPercent { get; private set; } = 100;

    internal bool Loaded => _loaded;

    internal int SheetCount => _sheets;

    internal int CurrentSheet => _currentSheet;

    internal bool IsFitted => _manualZoom is null;

    public void FitToWindow()
    {
        _manualZoom = null;
        _fitted = false;
        if (_loaded)
            ApplyZoom();
    }

    public void ActualSize()
    {
        _manualZoom = 100;
        if (_loaded)
            ApplyZoom();
    }

    public void ZoomBy(double factor)
    {
        int next = (int)Math.Clamp(Math.Round(ZoomPercent * factor), MinZoom, MaxZoom);
        if (next == ZoomPercent)
            next = factor > 1 ? Math.Min(MaxZoom, ZoomPercent + 10) : Math.Max(MinZoom, ZoomPercent - 10);

        _manualZoom = next;
        if (_loaded)
            ApplyZoom();
    }

    public void StepSheet(int delta)
    {
        if (_sheets <= 1)
            return;

        int target = Math.Clamp(_currentSheet + delta, 0, _sheets - 1);
        _currentSheet = target;
        SheetChanged?.Invoke(this, EventArgs.Empty);
        if (_loaded && Document?.GetElementById($"sheet-{target}") is { } el)
            el.ScrollIntoView(true);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_loaded && _manualZoom is null)
            ApplyZoom();
    }

    private void ApplyZoom()
    {
        int width = ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
        if (width <= 0 || ActiveXInstance is not { } browser)
            return;

        int percent = _manualZoom ?? (int)Math.Clamp(width * 100 / _sheetWidthPx, MinZoom, 100);
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
