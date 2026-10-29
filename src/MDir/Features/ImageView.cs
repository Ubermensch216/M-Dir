using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MDir.Features;

/// <summary>
/// Shows one image fitted to the window, or zoomed (mouse wheel, drag to pan) and rotated in 90 degree steps.
/// Rotation only changes the view; the file is never touched. The image is owned (and disposed) by the view.
/// </summary>
internal sealed class ImageView : Control
{
    private const float MinZoom = 0.02f;
    private const float MaxZoom = 32f;

    private Bitmap? _image;
    private float? _zoom;
    private PointF _pan;
    private Point? _dragStart;
    private PointF _panAtDragStart;

    public ImageView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
    }

    public Size? ImageSize => _image?.Size;

    /// <summary>The zoom in use: the fit zoom while the image is fitted, else the chosen one.</summary>
    public float EffectiveZoom => _image is null ? 1f : _zoom ?? FitZoom(ClientSize, _image.Size);

    public bool IsFitted => _zoom is null;

    /// <summary>Shows <paramref name="image"/>, fitted. The previous image is disposed.</summary>
    public void SetImage(Bitmap image)
    {
        _image?.Dispose();
        _image = image;
        _zoom = null;
        _pan = PointF.Empty;
        Invalidate();
    }

    public void FitToWindow()
    {
        _zoom = null;
        _pan = PointF.Empty;
        Invalidate();
    }

    public void ActualSize() => SetZoom(1f);

    /// <summary>Zooms in (factor above 1) or out around the image centre.</summary>
    public void ZoomBy(double factor) => SetZoom((float)Math.Clamp(EffectiveZoom * factor, MinZoom, MaxZoom));

    /// <summary>Turns the image a quarter turn; the zoom mode is kept.</summary>
    public void Rotate(bool clockwise)
    {
        if (_image is null)
            return;

        _image.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
        _pan = PointF.Empty;
        Invalidate();
    }

    /// <summary>The zoom that makes the whole image visible. Images that already fit are not enlarged.</summary>
    internal static float FitZoom(Size client, Size image)
    {
        if (image.Width <= 0 || image.Height <= 0 || client.Width <= 0 || client.Height <= 0)
            return 1f;

        return Math.Min(1f, Math.Min(client.Width / (float)image.Width, client.Height / (float)image.Height));
    }

    /// <summary>Where the image is drawn: centred, moved by <paramref name="pan"/>, and never panned past its edges.</summary>
    internal static Rectangle ComputeDestination(Size client, Size image, float zoom, PointF pan)
    {
        float width = image.Width * zoom;
        float height = image.Height * zoom;
        float x = (client.Width - width) / 2f + ClampPan(pan.X, width, client.Width);
        float y = (client.Height - height) / 2f + ClampPan(pan.Y, height, client.Height);
        return new Rectangle((int)Math.Round(x), (int)Math.Round(y), Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)));
    }

    /// <summary>An axis can only be panned while the image is larger than the window along it.</summary>
    private static float ClampPan(float pan, float imageExtent, float clientExtent)
    {
        float limit = Math.Max(0f, (imageExtent - clientExtent) / 2f);
        return Math.Clamp(pan, -limit, limit);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        if (_image is null)
            return;

        float zoom = EffectiveZoom;
        Rectangle destination = ComputeDestination(ClientSize, _image.Size, zoom, _pan);

        // Smooth when shrinking; crisp pixels when magnified a lot.
        e.Graphics.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBicubic : zoom >= 3f ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_image, destination);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomBy(Math.Pow(1.15, e.Delta / 120.0));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragStart = e.Location;
            _panAtDragStart = _pan;
            Focus();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not { } start || _image is null)
            return;

        float zoom = EffectiveZoom;
        _pan = new PointF(
            ClampPan(_panAtDragStart.X + e.X - start.X, _image.Width * zoom, ClientSize.Width),
            ClampPan(_panAtDragStart.Y + e.Y - start.Y, _image.Height * zoom, ClientSize.Height));
        Cursor = Cursors.SizeAll;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragStart = null;
        Cursor = Cursors.Default;
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (IsFitted)
            ActualSize();
        else
            FitToWindow();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image?.Dispose();
            _image = null;
        }

        base.Dispose(disposing);
    }

    private void SetZoom(float zoom)
    {
        if (_image is null)
            return;

        _zoom = zoom;
        Invalidate();
    }

    /// <summary>Decodes an image file into a bitmap upright according to its EXIF orientation. Throws when it cannot be decoded.</summary>
    public static Bitmap Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var original = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);

        // A copy: the file stream is released, and animated images show their first frame.
        var bitmap = new Bitmap(original);
        try
        {
            if (Array.IndexOf(original.PropertyIdList, ExifOrientationId) >= 0
                && original.GetPropertyItem(ExifOrientationId) is { Value: { Length: >= 2 } value })
            {
                RotateFlipType? fix = OrientationFix(BitConverter.ToUInt16(value, 0));
                if (fix is { } flip)
                    bitmap.RotateFlip(flip);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private const int ExifOrientationId = 0x0112;

    /// <summary>What turns an image stored with the given EXIF orientation upright (null = already upright).</summary>
    internal static RotateFlipType? OrientationFix(int orientation) => orientation switch
    {
        2 => RotateFlipType.RotateNoneFlipX,
        3 => RotateFlipType.Rotate180FlipNone,
        4 => RotateFlipType.RotateNoneFlipY,
        5 => RotateFlipType.Rotate90FlipX,
        6 => RotateFlipType.Rotate90FlipNone,
        7 => RotateFlipType.Rotate270FlipX,
        8 => RotateFlipType.Rotate270FlipNone,
        _ => null,
    };
}
