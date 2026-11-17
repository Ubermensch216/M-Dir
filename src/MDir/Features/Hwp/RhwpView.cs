using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MDir.Features.Hwp;

/// <summary>
/// Shows a multi-page HWP/HWPX document rendered lazily page-by-page by <see cref="RhwpDocument"/>.
/// Page 0 is visible right away; scrolling or zooming renders visible and nearby pages in the background.
/// </summary>
internal sealed class RhwpView : ScrollableControl
{
    private const int Gap = 12;
    private const int MarginPx = 14;
    private const int AheadPages = 4;
    private const int MaxPages = 600;
    private const int MaxCachedBitmaps = 12;
    private const float MinZoom = 0.2f;
    private const float MaxZoom = 4.0f;
    private const float DefaultLogicalWidth = 794f;
    private const float DefaultLogicalHeight = 1123f;

    private readonly RhwpDocument _doc;
    private readonly RhwpJob _job;
    private readonly int _displayPages;
    private readonly List<(float Width, float Height)> _logicalSizes;
    private readonly List<int> _pageTops = [];
    private readonly Dictionary<int, Bitmap> _bitmaps = [];
    private readonly LinkedList<int> _lru = [];
    private readonly HashSet<int> _failed = [];
    private readonly HashSet<int> _inflight = [];
    private readonly object _stateLock = new();
    private readonly AutoResetEvent _wake = new(false);

    private float? _manualZoom;
    private float _scale = 1.0f;
    private int _totalWidth;
    private int _totalHeight;
    private int _firstVisible;
    private int _lastVisible;
    private int _currentPage;
    private bool _disposed;

    public RhwpView(RhwpDocument doc, RhwpJob? job = null)
    {
        _doc = doc;
        _job = job ?? new RhwpJob();
        _displayPages = Math.Min(doc.PageCount, MaxPages);

        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint
            | ControlStyles.Selectable,
            true);
        AutoScroll = true;
        BackColor = Color.FromArgb(0x6B, 0x6B, 0x6B);

        (float w0, float h0) = LoadLogicalSize(doc.PageFile(0)) ?? (DefaultLogicalWidth, DefaultLogicalHeight);
        _logicalSizes = new List<(float, float)>(_displayPages);
        for (int i = 0; i < _displayPages; i++)
            _logicalSizes.Add((w0, h0));

        RecalculateLayout(preserveScroll: false);
        Task.Run(WorkerLoop);
    }

    public event EventHandler? PageChanged;

    public int PageCount => _doc.PageCount;

    public int DisplayedPageCount => _displayPages;

    public int CurrentPage => _currentPage;

    public int ZoomPercent => (int)Math.Round(_scale * 100f);

    public bool IsFitted => _manualZoom is null;

    public void FitToWindow()
    {
        _manualZoom = null;
        RecalculateLayout(preserveScroll: true);
    }

    public void ActualSize()
    {
        _manualZoom = 1.0f;
        RecalculateLayout(preserveScroll: true);
    }

    public void ZoomBy(double factor)
    {
        float next = (float)Math.Clamp(_scale * factor, MinZoom, MaxZoom);
        _manualZoom = next;
        RecalculateLayout(preserveScroll: true);
    }

    public void GoToPage(int pageIndex)
    {
        if (_displayPages <= 0)
            return;

        int target = Math.Clamp(pageIndex, 0, _displayPages - 1);
        int top = Math.Max(0, _pageTops[target] - MarginPx);
        AutoScrollPosition = new Point(-AutoScrollPosition.X, top);
        UpdateVisibleRange();
        Invalidate();
        _wake.Set();
    }

    public void StepPage(int delta) => GoToPage(_currentPage + delta);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_disposed)
            return;

        RecalculateLayout(preserveScroll: true);
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        UpdateVisibleRange();
        Invalidate();
        _wake.Set();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            ZoomBy(Math.Pow(1.15, e.Delta / 120.0));
            return;
        }

        base.OnMouseWheel(e);
        UpdateVisibleRange();
        Invalidate();
        _wake.Set();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Up or Keys.Down or Keys.Left or Keys.Right
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.PageDown:
            case Keys.Right:
                StepPage(1);
                e.Handled = true;
                break;
            case Keys.PageUp:
            case Keys.Left:
                StepPage(-1);
                e.Handled = true;
                break;
            case Keys.Home:
                GoToPage(0);
                e.Handled = true;
                break;
            case Keys.End:
                GoToPage(_displayPages - 1);
                e.Handled = true;
                break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        if (_displayPages == 0 || _pageTops.Count == 0)
            return;

        UpdateVisibleRange();

        int scrollX = -AutoScrollPosition.X;
        int scrollY = -AutoScrollPosition.Y;
        int clientW = Math.Max(1, ClientSize.Width);
        int clientH = Math.Max(1, ClientSize.Height);

        int first = FindFirstVisible(scrollY);
        float maxW = 0f;
        for (int i = 0; i < _logicalSizes.Count; i++)
            maxW = Math.Max(maxW, _logicalSizes[i].Width);
        int baseX = _totalWidth <= clientW
            ? Math.Max(MarginPx, (int)Math.Round((clientW - (maxW * _scale)) / 2f))
            : MarginPx;

        using var shadowBrush = new SolidBrush(Color.FromArgb(0x4E, 0x4E, 0x4E));
        using var placeholderTextBrush = new SolidBrush(Color.FromArgb(0x88, 0x88, 0x88));
        using var placeholderFont = new Font(Font.FontFamily, 14f, FontStyle.Regular);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        e.Graphics.InterpolationMode = _scale < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

        for (int i = first; i < _displayPages; i++)
        {
            int top = _pageTops[i];
            if (top >= scrollY + clientH)
                break;

            (float lw, float lh) = _logicalSizes[i];
            int pw = Math.Max(1, (int)Math.Round(lw * _scale));
            int ph = Math.Max(1, (int)Math.Round(lh * _scale));
            int px = _totalWidth <= clientW
                ? Math.Max(baseX, (int)Math.Round((clientW - pw) / 2f))
                : baseX;
            int drawX = px - scrollX;
            int drawY = top - scrollY;

            e.Graphics.FillRectangle(shadowBrush, drawX + 3, drawY + 3, pw, ph);

            Bitmap? bmp = GetBitmap(i);
            if (bmp is not null)
            {
                e.Graphics.DrawImage(bmp, new Rectangle(drawX, drawY, pw, ph));
            }
            else
            {
                e.Graphics.FillRectangle(Brushes.White, drawX, drawY, pw, ph);
                bool failed;
                lock (_stateLock)
                    failed = _failed.Contains(i);
                string msg = failed ? "⚠" : $"{i + 1} …";
                var textRect = new RectangleF(drawX, drawY, pw, Math.Min(ph, 240));
                e.Graphics.DrawString(msg, placeholderFont, placeholderTextBrush, textRect, sf);
            }
        }
    }

    private void RecalculateLayout(bool preserveScroll)
    {
        if (_displayPages == 0)
            return;

        double oldFraction = preserveScroll && _totalHeight > 0
            ? Math.Clamp(-AutoScrollPosition.Y / (double)_totalHeight, 0.0, 1.0)
            : 0.0;

        int clientW = Math.Max(ClientSize.Width, 200);
        float maxW = 0f;
        for (int i = 0; i < _logicalSizes.Count; i++)
            maxW = Math.Max(maxW, _logicalSizes[i].Width);

        float fitScale = Math.Clamp((clientW - (2 * MarginPx)) / Math.Max(1f, maxW), MinZoom, 2.0f);
        _scale = _manualZoom ?? fitScale;

        _pageTops.Clear();
        int y = MarginPx;
        for (int i = 0; i < _displayPages; i++)
        {
            _pageTops.Add(y);
            y += Math.Max(1, (int)Math.Round(_logicalSizes[i].Height * _scale)) + Gap;
        }

        _totalWidth = Math.Max(clientW, (int)Math.Round(maxW * _scale) + (2 * MarginPx));
        _totalHeight = Math.Max(1, y - Gap + MarginPx);

        AutoScrollMinSize = new Size(_totalWidth, _totalHeight);
        if (preserveScroll)
        {
            int targetY = (int)Math.Round(oldFraction * _totalHeight);
            AutoScrollPosition = new Point(-AutoScrollPosition.X, targetY);
        }

        UpdateVisibleRange();
        Invalidate();
        _wake.Set();
    }

    private int FindFirstVisible(int scrollY)
    {
        int lo = 0;
        int hi = _pageTops.Count - 1;
        int ans = 0;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (_pageTops[mid] <= scrollY)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return Math.Max(0, ans);
    }

    private void UpdateVisibleRange()
    {
        if (_pageTops.Count == 0)
            return;

        int scrollY = -AutoScrollPosition.Y;
        int viewBottom = scrollY + Math.Max(1, ClientSize.Height);
        int first = FindFirstVisible(scrollY);
        int last = first;
        while (last + 1 < _pageTops.Count && _pageTops[last + 1] < viewBottom)
            last++;

        int centerY = scrollY + (Math.Max(1, ClientSize.Height) / 3);
        int current = FindFirstVisible(centerY);

        lock (_stateLock)
        {
            _firstVisible = first;
            _lastVisible = last;
        }

        if (current != _currentPage)
        {
            _currentPage = current;
            PageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int? NextWantedPage()
    {
        lock (_stateLock)
        {
            int first = _firstVisible;
            int last = _lastVisible;
            int ahead = Math.Min(_displayPages - 1, last + AheadPages);

            for (int i = first; i <= last; i++)
            {
                if (NeedsRenderLocked(i))
                    return i;
            }

            for (int i = last + 1; i <= ahead; i++)
            {
                if (NeedsRenderLocked(i))
                    return i;
            }

            for (int i = first - 1; i >= Math.Max(0, first - 2); i--)
            {
                if (NeedsRenderLocked(i))
                    return i;
            }

            return null;
        }
    }

    private bool NeedsRenderLocked(int i) =>
        i >= 0 && i < _displayPages && !_doc.HasPage(i) && !_failed.Contains(i) && !_inflight.Contains(i);

    private void WorkerLoop()
    {
        while (!_disposed && !_job.IsCancelled)
        {
            int? page = NextWantedPage();
            if (page is not { } index)
            {
                _wake.WaitOne(500);
                continue;
            }

            lock (_stateLock)
                _inflight.Add(index);

            bool ok = _doc.RenderPage(index, _job);

            lock (_stateLock)
            {
                _inflight.Remove(index);
                if (!ok && !_job.IsCancelled)
                    _failed.Add(index);
            }

            if (_disposed || _job.IsCancelled)
                return;

            if (ok)
            {
                (float, float)? size = LoadLogicalSize(_doc.PageFile(index));
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(() =>
                        {
                            if (_disposed)
                                return;

                            if (size is { } actual && !SizesClose(_logicalSizes[index], actual))
                            {
                                _logicalSizes[index] = actual;
                                RecalculateLayout(preserveScroll: true);
                            }
                            else
                            {
                                Invalidate();
                            }
                        });
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private static bool SizesClose((float Width, float Height) a, (float Width, float Height) b) =>
        Math.Abs(a.Width - b.Width) < 1f && Math.Abs(a.Height - b.Height) < 1f;

    private Bitmap? GetBitmap(int index)
    {
        if (_bitmaps.TryGetValue(index, out Bitmap? cached))
        {
            _lru.Remove(index);
            _lru.AddLast(index);
            return cached;
        }

        string file = _doc.PageFile(index);
        if (!File.Exists(file))
            return null;

        Bitmap? loaded = LoadDetachedBitmap(file);
        if (loaded is null)
        {
            lock (_stateLock)
                _failed.Add(index);
            return null;
        }

        _bitmaps[index] = loaded;
        _lru.AddLast(index);
        while (_lru.Count > MaxCachedBitmaps && _lru.First is { } oldest)
        {
            int remove = oldest.Value;
            _lru.RemoveFirst();
            if (_bitmaps.Remove(remove, out Bitmap? oldBmp))
                oldBmp.Dispose();
        }

        return loaded;
    }

    private static Bitmap? LoadDetachedBitmap(string file)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            using var ms = new MemoryStream(bytes, writable: false);
            using var img = Image.FromStream(ms);
            var copy = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
            using Graphics g = Graphics.FromImage(copy);
            g.DrawImageUnscaled(img, 0, 0);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static (float Width, float Height)? LoadLogicalSize(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return (Math.Max(1f, img.Width / 2f), Math.Max(1f, img.Height / 2f));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _job.Cancel();
            _wake.Set();
            _doc.Dispose();
            _job.Dispose();
            _wake.Dispose();

            foreach (Bitmap bmp in _bitmaps.Values)
                bmp.Dispose();
            _bitmaps.Clear();
            _lru.Clear();
        }

        base.Dispose(disposing);
    }
}
