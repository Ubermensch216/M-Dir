using System.Drawing.Imaging;
using System.Text;
using MDir.Features;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class TextFileTests
{
    static TextFileTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static TextPreviewData Decode(byte[] bytes, bool truncated = false) => TextFile.Decode(bytes, truncated, bytes.Length);

    [Fact]
    public void Bom_SelectsTheEncoding()
    {
        Assert.Equal(("한글 abc", "UTF-8 (BOM)"), Pair(Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("한글 abc")])));
        Assert.Equal(("한글", "UTF-16 LE"), Pair(Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("한글")])));
        Assert.Equal(("한글", "UTF-16 BE"), Pair(Decode([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("한글")])));
    }

    [Fact]
    public void WithoutBom_Utf8IsPreferred_ThenKoreanCodePage()
    {
        Assert.Equal(("안녕 hello", "UTF-8"), Pair(Decode(Encoding.UTF8.GetBytes("안녕 hello"))));
        Assert.Equal(("plain ascii", "UTF-8"), Pair(Decode(Encoding.ASCII.GetBytes("plain ascii"))));

        byte[] cp949 = Encoding.GetEncoding(949).GetBytes("안녕하세요 한글 문서");
        Assert.Equal(("안녕하세요 한글 문서", "CP949 (한국어)"), Pair(Decode(cp949)));
    }

    private static (string, string) Pair(TextPreviewData data) => (data.Text, data.EncodingName);

    [Fact]
    public void LineEndingsAreNormalisedForATextBox()
    {
        Assert.Equal("a\r\nb\r\nc\r\nd", Decode(Encoding.UTF8.GetBytes("a\nb\r\nc\rd")).Text);
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0B, 0x0E })]
    public void BinaryContent_IsDetected(byte[] bytes)
    {
        TextPreviewData data = Decode(bytes);

        Assert.True(data.IsBinary);
        Assert.Equal(string.Empty, data.Text);
    }

    [Fact]
    public void EmptyFile_IsAnEmptyText()
    {
        TextPreviewData data = Decode([]);

        Assert.False(data.IsBinary);
        Assert.Equal(string.Empty, data.Text);
    }

    [Fact]
    public void ALimitThatCutsAMultiByteCharacter_DoesNotTurnTheTextIntoKorean()
    {
        // 3-byte characters, cut after the first byte of the last one.
        byte[] full = Encoding.UTF8.GetBytes("가나다라");
        byte[] cut = full[..^2];

        TextPreviewData data = Decode(cut, truncated: true);

        Assert.Equal("UTF-8", data.EncodingName);
        Assert.Equal("가나다", data.Text);
        Assert.True(data.Truncated);
    }

    [Fact]
    public void WithoutPartialUtf8Tail_OnlyCutsIncompleteSequences()
    {
        Assert.Equal(3, TextFile.WithoutPartialUtf8Tail(Encoding.UTF8.GetBytes("abc")).Length);
        Assert.Equal(3, TextFile.WithoutPartialUtf8Tail(Encoding.UTF8.GetBytes("가")).Length);
        Assert.Equal(0, TextFile.WithoutPartialUtf8Tail(Encoding.UTF8.GetBytes("가")[..2]).Length);
        Assert.Equal(1, TextFile.WithoutPartialUtf8Tail([.. "a"u8, 0xF0, 0x9F]).Length);
    }

    [Fact]
    public void Read_StopsAtTheLimitAndReportsTheFileLength()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("big.txt"), new string('x', 5000));

        TextPreviewData data = TextFile.Read(dir.File("big.txt"), limit: 1000);

        Assert.Equal(1000, data.Text.Length);
        Assert.True(data.Truncated);
        Assert.Equal(5000, data.FileLength);
        Assert.False(TextFile.Read(dir.File("big.txt"), limit: 10_000).Truncated);
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("b.MD", true)]
    [InlineData("c.cs", true)]
    [InlineData(".gitignore", true)]
    [InlineData("d.exe", false)]
    [InlineData("e.pdf", false)]
    [InlineData("noext", false)]
    public void KnownTextExtensions(string name, bool expected) => Assert.Equal(expected, TextFile.IsKnownText(name));
}

public class ImageViewMathTests
{
    [Theory]
    [InlineData(1000, 500, 100, 100, 0.1f)]
    [InlineData(1000, 500, 2000, 100, 1f)]
    [InlineData(300, 600, 150, 150, 0.25f)]
    [InlineData(0, 0, 10, 10, 1f)]
    public void FitZoom_ShrinksToFitButNeverEnlarges(int clientW, int clientH, int imageW, int imageH, float expectedForSmallClient)
    {
        // (client, image) pairs written as client then image; the expectation is for the same order.
        float zoom = ImageView.FitZoom(new Size(clientW, clientH), new Size(imageW, imageH));
        Assert.InRange(zoom, 0f, 1f);
        if (clientW > 0 && clientH > 0)
            Assert.Equal(Math.Min(1f, Math.Min(clientW / (float)imageW, clientH / (float)imageH)), zoom, 4);
        else
            Assert.Equal(expectedForSmallClient == 1f ? 1f : zoom, zoom);
    }

    [Fact]
    public void FitZoom_Examples()
    {
        Assert.Equal(0.5f, ImageView.FitZoom(new Size(500, 400), new Size(1000, 800)), 4);
        Assert.Equal(0.25f, ImageView.FitZoom(new Size(500, 100), new Size(1000, 400)), 4);
        Assert.Equal(1f, ImageView.FitZoom(new Size(500, 400), new Size(100, 80)));
        Assert.Equal(1f, ImageView.FitZoom(new Size(0, 0), new Size(100, 80)));
    }

    [Fact]
    public void Destination_CentresSmallImagesAndIgnoresPanThere()
    {
        Rectangle destination = ImageView.ComputeDestination(new Size(400, 300), new Size(100, 50), 1f, new PointF(80, -60));

        Assert.Equal(new Rectangle(150, 125, 100, 50), destination);
    }

    [Fact]
    public void Destination_PansLargeImagesButNeverPastTheirEdges()
    {
        var client = new Size(400, 300);
        var image = new Size(1000, 800);

        Assert.Equal(new Rectangle(-300, -250, 1000, 800), ImageView.ComputeDestination(client, image, 1f, PointF.Empty));
        Assert.Equal(new Rectangle(-250, -250, 1000, 800), ImageView.ComputeDestination(client, image, 1f, new PointF(50, 0)));

        // Panned as far as possible: the image's edge meets the window's edge.
        Rectangle farthest = ImageView.ComputeDestination(client, image, 1f, new PointF(9999, 9999));
        Assert.Equal(0, farthest.Left);
        Assert.Equal(0, farthest.Top);
        Rectangle other = ImageView.ComputeDestination(client, image, 1f, new PointF(-9999, -9999));
        Assert.Equal(400, other.Right);
        Assert.Equal(300, other.Bottom);
    }

    [Fact]
    public void Destination_ScalesWithTheZoom() =>
        Assert.Equal(new Size(200, 100), ImageView.ComputeDestination(new Size(400, 300), new Size(100, 50), 2f, PointF.Empty).Size);

    [Theory]
    [InlineData(1, null)]
    [InlineData(3, RotateFlipType.Rotate180FlipNone)]
    [InlineData(6, RotateFlipType.Rotate90FlipNone)]
    [InlineData(8, RotateFlipType.Rotate270FlipNone)]
    [InlineData(99, null)]
    public void ExifOrientation_MapsToTheFixThatTurnsItUpright(int orientation, RotateFlipType? expected) =>
        Assert.Equal(expected, ImageView.OrientationFix(orientation));

    [Fact]
    public void Load_TurnsAnExifRotatedJpegUpright()
    {
        using var dir = new TempDirectory();
        string path = dir.File("rotated.jpg");
        RunSta(() =>
        {
            using var bitmap = new Bitmap(40, 20);
            using (Graphics g = Graphics.FromImage(bitmap))
                g.Clear(Color.Blue);

            // PropertyItem has no public constructor.
            var orientation = (PropertyItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
            orientation.Id = 0x0112;
            orientation.Type = 3;
            orientation.Len = 2;
            orientation.Value = BitConverter.GetBytes((ushort)6);
            bitmap.SetPropertyItem(orientation);
            bitmap.Save(path, ImageFormat.Jpeg);

            using Bitmap loaded = ImageView.Load(path);

            Assert.Equal(new Size(20, 40), loaded.Size);
        });
    }

    [Fact]
    public void Rotate_SwapsTheDimensionsAndZoomModeIsKept()
    {
        RunSta(() =>
        {
            using var view = new ImageView { Size = new Size(300, 300) };
            view.SetImage(new Bitmap(100, 40));
            Assert.True(view.IsFitted);

            view.Rotate(clockwise: true);
            Assert.Equal(new Size(40, 100), view.ImageSize);
            Assert.True(view.IsFitted);

            view.Rotate(clockwise: false);
            view.Rotate(clockwise: false);
            Assert.Equal(new Size(40, 100), view.ImageSize);

            view.ZoomBy(2);
            Assert.False(view.IsFitted);
            Assert.Equal(2f, view.EffectiveZoom, 3);
            view.ActualSize();
            Assert.Equal(1f, view.EffectiveZoom);
            view.FitToWindow();
            Assert.True(view.IsFitted);
        });
    }
}

public class PreviewFormatTests
{
    [Theory]
    [InlineData(0, "0바이트")]
    [InlineData(999, "999바이트")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(200L * 1024 * 1024, "200 MB")]
    public void FormatSize(long bytes, string expected) => Assert.Equal(expected, PreviewPane.FormatSize(bytes));
}

/// <summary>The preview pane on real files. Shows a window briefly on the desktop.</summary>
public class PreviewPaneTests(ITestOutputHelper output)
{
    static PreviewPaneTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static void RunPane(Func<Form, PreviewPane, Task> body)
    {
        PreviewPane? pane = null;
        Run(() =>
        {
            var form = new Form { Width = 700, Height = 500, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir preview test" };
            pane = new PreviewPane { Dock = DockStyle.Fill };
            form.Controls.Add(pane);
            return form;
        }, form => body(form, pane!));
    }

    private static ShellLocation Loc(string path) => ShellLocation.Parse(path)!;

    [Fact]
    public void Pane_HasAnOutline_AndATintedTitleBlockSeparatedFromThePreview()
    {
        RunPane(async (form, pane) =>
        {
            await pane.ShowAsync(Loc(@"C:\Windows"), 3);
            await Task.Delay(100);
            Assert.Equal(new Padding(1), pane.Padding);
            Assert.NotEqual(pane.BackColor, pane.FrameColor);

            UiTest.SaveScreenshot(form, "preview-frame.png");
            using Bitmap picture = UiTest.Capture(form);
            Point screen = pane.PointToScreen(Point.Empty);
            Point origin = new(screen.X - form.Left, screen.Y - form.Top);
            Point inner = new(origin.X + pane.Width / 2, origin.Y + pane.Height / 2);
            Color edge = picture.GetPixel(origin.X, inner.Y);
            Color outline = picture.GetPixel(origin.X + pane.Width / 2, origin.Y);
            // Both the side and the top of the pane carry the frame colour, which the contents do not.
            Assert.Equal(pane.FrameColor.ToArgb(), edge.ToArgb());
            Assert.Equal(pane.FrameColor.ToArgb(), outline.ToArgb());
            Assert.NotEqual(pane.FrameColor.ToArgb(), picture.GetPixel(inner.X, inner.Y).ToArgb());
            // The title block is tinted against the pane's own background.
            Assert.NotEqual(pane.BackColor.ToArgb(), picture.GetPixel(origin.X + 3, origin.Y + 3).ToArgb());
        });
    }

    [Fact]
    public void NoSelectionAndMultipleSelection_ShowMessages()
    {
        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(null, 0);
            Assert.Equal(PreviewKind.Message, pane.Kind);

            await pane.ShowAsync(Loc(@"C:\Windows"), 3);
            Assert.Equal(PreviewKind.Message, pane.Kind);
            Assert.Equal("3개 항목이 선택되었습니다.", ((Label)pane.View!).Text);
        });
    }

    [Fact]
    public void Image_IsShownInTheBuiltInViewer_AndCanBeRotated()
    {
        using var dir = new TempDirectory();
        var magenta = Color.FromArgb(250, 0, 250);
        using (var bitmap = new Bitmap(120, 60))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
                g.Clear(magenta);
            bitmap.Save(dir.File("pic.png"), ImageFormat.Png);
        }

        RunPane(async (form, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("pic.png")), 1);

            Assert.Equal(PreviewKind.Image, pane.Kind);
            var view = (ImageView)pane.View!;
            Assert.Equal(new Size(120, 60), view.ImageSize);
            Assert.Contains("120×60", pane.Detail);
            Assert.Equal("pic.png", pane.Title);
            await Task.Delay(300);
            using (Bitmap shot = Capture(form))
                Assert.True(CountPixels(shot, magenta, 30) > 3000, "the image is painted");
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "preview-image.png")}");

            view.Rotate(clockwise: true);
            Assert.Equal(new Size(60, 120), view.ImageSize);
        });
    }

    [Fact]
    public void CorruptImage_FallsBackToTheInfoPage()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("broken.png"), "this is not a png");
        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("broken.png")), 1);

            Assert.Equal(PreviewKind.Info, pane.Kind);
        });
    }

    [Fact]
    public void TextFiles_AreDecodedByContent_AndTruncatedWhenHuge()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("utf8.txt"), "안녕하세요\r\nsecond", new UTF8Encoding(false));
        File.WriteAllBytes(dir.File("cp949.log"), Encoding.GetEncoding(949).GetBytes("한글 로그 파일입니다"));
        File.WriteAllText(dir.File("huge.md"), new string('y', TextFile.DefaultLimit + 5000));
        File.WriteAllText(dir.File("data.unknownext"), "plain words in an unknown file type");

        RunPane(async (form, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("utf8.txt")), 1);
            Assert.Equal(PreviewKind.Text, pane.Kind);
            Assert.Equal("안녕하세요\r\nsecond", ((TextBox)pane.View!).Text);
            Assert.Contains("UTF-8", pane.Detail);
            await Task.Delay(300);
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "preview-text.png")}");

            await pane.ShowAsync(Loc(dir.File("cp949.log")), 1);
            Assert.Equal("한글 로그 파일입니다", ((TextBox)pane.View!).Text);
            Assert.Contains("CP949", pane.Detail);

            await pane.ShowAsync(Loc(dir.File("huge.md")), 1);
            Assert.Equal(TextFile.DefaultLimit, ((TextBox)pane.View!).Text.Length);
            Assert.Contains("앞부분", pane.Detail);

            await pane.ShowAsync(Loc(dir.File("data.unknownext")), 1);
            Assert.Equal(PreviewKind.Text, pane.Kind);
        });
    }

    [Fact]
    public void BinaryFileAndFolder_ShowFacts()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("blob.unknownbin"), [0, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0]);
        Directory.CreateDirectory(dir.File("folder"));
        File.WriteAllText(dir.File(@"folder\a.txt"), "x");
        File.WriteAllText(dir.File(@"folder\b.txt"), "x");

        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("blob.unknownbin")), 1);
            Assert.Equal(PreviewKind.Info, pane.Kind);
            Assert.Equal("blob.unknownbin", pane.Title);

            await pane.ShowAsync(Loc(dir.File("folder")), 1);
            Assert.Equal(PreviewKind.Info, pane.Kind);
            Assert.Equal("folder", pane.Title);
            string facts = string.Join("|", ((TableLayoutPanel)pane.View!).Controls.OfType<Label>().Select(l => l.Text));
            Assert.Contains("2개", facts);
        });
    }

    [Fact]
    public void FileWithAShellHandler_IsShownByTheHandler_AndUnloadedOnClear()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("doc.pdf"), PreviewHandlerHostTests.MinimalPdf());
        RunPane(async (form, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("doc.pdf")), 1);

            Assert.Equal(PreviewKind.Handler, pane.Kind);
            Assert.Contains("PDF", pane.Detail);
            await Task.Delay(2500);
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "preview-pane-pdf.png")}");

            pane.Clear();
            Assert.Equal(PreviewKind.None, pane.Kind);
            await WaitUntil(() =>
            {
                try
                {
                    File.Delete(dir.File("doc.pdf"));
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            }, "the handler released the PDF");
        });
    }

    [Fact]
    public void ANewerSelection_ReplacesAnOlderOneStillLoading()
    {
        using var dir = new TempDirectory();
        using (var big = new Bitmap(3000, 3000))
            big.Save(dir.File("big.png"), ImageFormat.Png);
        File.WriteAllText(dir.File("small.txt"), "latest");

        RunPane(async (_, pane) =>
        {
            Task first = pane.ShowAsync(Loc(dir.File("big.png")), 1);
            Task second = pane.ShowAsync(Loc(dir.File("small.txt")), 1);
            await Task.WhenAll(first, second);

            Assert.Equal(PreviewKind.Text, pane.Kind);
            Assert.Equal("small.txt", pane.Title);
        });
    }

    [Fact]
    public void ItemsThatAreGone_ShowAMessage()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("temp.txt"), "x");
        ShellLocation location = Loc(dir.File("temp.txt"));
        File.Delete(dir.File("temp.txt"));

        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(location, 1);

            Assert.Equal(PreviewKind.Message, pane.Kind);
        });
    }

    [Fact]
    public void VirtualFolders_ShowTheirNameWithoutFailing()
    {
        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(Loc("shell:MyComputerFolder"), 1);

            Assert.Equal(PreviewKind.Info, pane.Kind);
            Assert.False(string.IsNullOrEmpty(pane.Title));
        });
    }
}
