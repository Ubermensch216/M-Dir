using System.Text;
using MDir.Features;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Hosts the machine's real preview handlers (the Windows TXT and PDF previewers).</summary>
public class PreviewHandlerHostTests(ITestOutputHelper output)
{
    /// <summary>A tiny but valid one-page PDF with a line of text.</summary>
    internal static byte[] MinimalPdf()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            null!,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        const string stream = "BT /F1 24 Tf 30 100 Td (M-Dir preview) Tj ET";
        objects[3] = $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream";

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    [Fact]
    public void HasHandler_ReflectsTheShellRegistration()
    {
        Assert.True(PreviewHandlerHost.HasHandler(@"C:\x\file.txt"));
        Assert.True(PreviewHandlerHost.HasHandler("a.PDF"), "extensions are case-insensitive");
        Assert.False(PreviewHandlerHost.HasHandler("a.nosuchtype"));
        Assert.False(PreviewHandlerHost.HasHandler("noextension"));
    }

    [Fact]
    public void TextFile_IsShownByTheWindowsTextPreviewer_AndUnloads()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("note.txt"), "Hello from M-Dir\r\nsecond line\r\n");
        PreviewHandlerHost? host = null;
        Run(() =>
        {
            var form = new Form { Width = 700, Height = 500, StartPosition = FormStartPosition.CenterScreen };
            host = new PreviewHandlerHost { Dock = DockStyle.Fill };
            form.Controls.Add(host);
            return form;
        }, async form =>
        {
            Assert.True(host!.TryShow(dir.File("note.txt")), "the TXT handler accepted the file");
            Assert.True(host.IsShowing);
            output.WriteLine($"handler: {host.HandlerName}");
            await Task.Delay(1500);
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "preview-handler-txt.png")}");

            host.Unload();
            Assert.False(host.IsShowing);

            // The file must be free again once the preview is unloaded.
            await WaitUntil(() =>
            {
                try
                {
                    File.Delete(dir.File("note.txt"));
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            }, "the handler released the file");
        });
    }

    [Fact]
    public void PdfFile_IsShownByThePdfPreviewer()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("doc.pdf"), MinimalPdf());
        PreviewHandlerHost? host = null;
        Run(() =>
        {
            var form = new Form { Width = 700, Height = 500, StartPosition = FormStartPosition.CenterScreen };
            host = new PreviewHandlerHost { Dock = DockStyle.Fill };
            form.Controls.Add(host);
            return form;
        }, async form =>
        {
            Assert.True(host!.TryShow(dir.File("doc.pdf")), "the PDF handler accepted the file");
            output.WriteLine($"handler: {host.HandlerName}");
            await Task.Delay(3000);
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "preview-handler-pdf.png")}");
            host.Unload();
        });
    }

    [Fact]
    public void FileTypesWithoutAHandler_AreRefusedWithoutSideEffects()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("data.nosuchtype"), "x");
        PreviewHandlerHost? host = null;
        Run(() =>
        {
            var form = new Form { Width = 400, Height = 300 };
            host = new PreviewHandlerHost { Dock = DockStyle.Fill };
            form.Controls.Add(host);
            return form;
        }, _ =>
        {
            Assert.False(host!.TryShow(dir.File("data.nosuchtype")));
            Assert.False(host.TryShow(dir.File("missing.txt")), "a missing file is refused too");
            Assert.False(host.IsShowing);
            Assert.Null(host.HandlerName);
            return Task.CompletedTask;
        });
    }
}
