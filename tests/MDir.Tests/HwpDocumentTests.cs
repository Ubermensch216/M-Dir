using System.IO.Compression;
using System.Text;
using MDir.Features;
using MDir.Features.Hwp;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>The built-in Hancom (.hwp/.hwpx) reader and viewer that shows every page with its formatting.</summary>
public class HwpDocumentTests
{
    private const uint NoStream = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;

    // ---- HWPX builders ----

    private const string Ns = """xmlns:hh="http://www.hancom.co.kr/hwpml/2011/head" xmlns:hp="http://www.hancom.co.kr/hwpml/2011/paragraph" xmlns:hc="http://www.hancom.co.kr/hwpml/2011/core" xmlns:hs="http://www.hancom.co.kr/hwpml/2011/section" """;

    private static byte[] Zip(params (string Name, byte[] Data)[] parts)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in parts)
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    private static readonly string Header = $"""
        <hh:head {Ns}><hh:refList>
          <hh:fontfaces><hh:fontface lang="HANGUL"><hh:font id="0" face="맑은 고딕"/></hh:fontface></hh:fontfaces>
          <hh:borderFills>
            <hh:borderFill id="1"><hh:leftBorder type="NONE"/></hh:borderFill>
            <hh:borderFill id="2"><hh:leftBorder type="SOLID"/><hc:fillBrush><hc:winBrush faceColor="#FFFF00"/></hc:fillBrush></hh:borderFill>
          </hh:borderFills>
          <hh:charProperties>
            <hh:charPr id="0" height="1000" textColor="#000000" shadeColor="none"><hh:fontRef hangul="0"/></hh:charPr>
            <hh:charPr id="1" height="2000" textColor="#FF0000" shadeColor="none"><hh:fontRef hangul="0"/><hh:bold/><hh:underline type="BOTTOM"/></hh:charPr>
          </hh:charProperties>
          <hh:paraProperties>
            <hh:paraPr id="0"><hh:align horizontal="JUSTIFY"/><hh:lineSpacing type="PERCENT" value="160"/></hh:paraPr>
            <hh:paraPr id="1"><hh:align horizontal="CENTER"/></hh:paraPr>
          </hh:paraProperties>
        </hh:refList></hh:head>
        """;

    private static string Section(string body, string pagePr = "") => $"<hs:sec {Ns}>{pagePr}{body}</hs:sec>";

    private static string P(string text, int para = 0, int chars = 0, bool pageBreak = false) =>
        $"""<hp:p paraPrIDRef="{para}" pageBreak="{(pageBreak ? 1 : 0)}"><hp:run charPrIDRef="{chars}"><hp:t>{text}</hp:t></hp:run></hp:p>""";

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static byte[] SampleHwpx() => Zip(
        ("mimetype", Utf8("application/hwp+zip")),
        ("Contents/header.xml", Utf8(Header)),
        ("Contents/section1.xml", Utf8(Section(P("둘째 구역")))),
        ("Contents/section0.xml", Utf8(Section(
            P("제목", para: 1, chars: 1)
            + P("본문 첫 쪽")
            + """<hp:p paraPrIDRef="0"><hp:run charPrIDRef="0"><hp:tbl colCnt="2" borderFillIDRef="2"><hp:tr>"""
            + """<hp:tc borderFillIDRef="2"><hp:cellAddr colAddr="0" rowAddr="0"/><hp:cellSpan colSpan="1" rowSpan="1"/><hp:cellSz width="10000" height="1000"/><hp:subList vertAlign="CENTER">""" + P("셀A") + "</hp:subList></hp:tc>"
            + """<hp:tc borderFillIDRef="1"><hp:cellAddr colAddr="1" rowAddr="0"/><hp:cellSpan colSpan="1" rowSpan="2"/><hp:cellSz width="20000" height="1000"/><hp:subList>""" + P("병합&lt;b&gt;") + "</hp:subList></hp:tc>"
            + "</hp:tr></hp:tbl></hp:run></hp:p>"
            + """<hp:p><hp:run><hp:pic><hc:img binaryItemIDRef="image1"/><hp:curSz width="7200" height="7200"/></hp:pic></hp:run></hp:p>"""
            + P("다음 쪽", pageBreak: true),
            """<hp:pagePr landscape="WIDELY" width="59528" height="84188"><hp:margin left="8504" right="8504" top="5668" bottom="4252" header="4252" footer="4252"/></hp:pagePr>"""))),
        ("BinData/image1.png", Png));

    [Fact]
    public void Hwpx_ReadsShapesTablesPicturesPagesAndSectionsInOrder()
    {
        HwpDoc doc = HwpDocument.Read(SampleHwpx())!;

        var paras = doc.Blocks.OfType<HwpPara>().ToList();
        var title = (HwpRun)paras[0].Runs[0];
        Assert.Equal(("제목", "center", 20.0, true, true, "#ff0000", "맑은 고딕"), (title.Text, paras[0].Align, title.Size!.Value, title.Bold, title.Underline, title.Color, title.Font));
        Assert.Equal(1.6, paras[1].Line);

        HwpTable table = doc.Blocks.OfType<HwpTable>().Single();
        Assert.True(table.Border);
        Assert.Equal([100.0, 200.0], table.ColumnWidths.Select(w => w!.Value));
        Assert.Equal(("#ffff00", "middle", 2), (table.Rows[0][0].Background, table.Rows[0][0].VAlign, table.Rows[0][1].RowSpan));

        Assert.Contains(paras, p => p.Runs.OfType<HwpImage>().Any(i => i.Width == 72));
        Assert.True(paras.Single(p => p.Runs.OfType<HwpRun>().Any(r => r.Text == "다음 쪽")).PageBreak);
        Assert.Equal("둘째 구역", ((HwpRun)((HwpPara)doc.Blocks[^1]).Runs[0]).Text);
        Assert.True(((HwpPara)doc.Blocks[^1]).PageBreak, "a new section starts a new page");
        Assert.Equal((595.28, 841.88, 85.04, 99.2), (doc.Page.Width, doc.Page.Height, doc.Page.Left, doc.Page.Top));
    }

    [Fact]
    public void Html_DrawsSheetsFormattingAndEscapesText()
    {
        using var dir = new TempDirectory();
        HwpPreview preview = HwpHtml.Render(HwpDocument.Read(SampleHwpx())!, dir.File("out"));
        string html = File.ReadAllText(preview.HtmlPath);

        Assert.Equal(3, preview.Sheets);
        Assert.Contains("font-weight:bold", html);
        Assert.Contains("color:#ff0000", html);
        Assert.Contains("rowspan=\"2\"", html);
        Assert.Contains("background:#ffff00", html);
        Assert.Contains("<img src=\"img0.png\"", html);
        Assert.Equal(Png, File.ReadAllBytes(dir.File(@"out\img0.png")));
        Assert.Contains("width:595.28pt", html);
        Assert.Contains("병합&lt;b&gt;", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_WritesAPictureUsedManyTimesOnce()
    {
        using var dir = new TempDirectory();
        var doc = new HwpDoc();
        for (int i = 0; i < 5; i++)
        {
            var para = new HwpPara();
            para.Runs.Add(new HwpImage(Png, 10, 10));
            doc.Blocks.Add(para);
        }

        string html = File.ReadAllText(HwpHtml.Render(doc, dir.Path).HtmlPath);

        Assert.Single(Directory.GetFiles(dir.Path, "img*"));
        Assert.Equal(5, html.Split("img0.png").Length - 1);
    }

    [Fact]
    public void Preview_IsDrawnOnceAndRedrawnWhenTheFileChanges()
    {
        using var dir = new TempDirectory();
        string old = HwpDocument.CacheFolder;
        HwpDocument.CacheFolder = dir.File("cache");
        try
        {
            string path = dir.File("a.hwpx");
            File.WriteAllBytes(path, SampleHwpx());
            HwpPreview first = HwpDocument.Preview(path)!;
            File.AppendAllText(first.HtmlPath, "<!-- seen -->");

            Assert.Contains("seen", File.ReadAllText(HwpDocument.Preview(path)!.HtmlPath));

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            HwpPreview redrawn = HwpDocument.Preview(path)!;
            Assert.NotEqual(first.HtmlPath, redrawn.HtmlPath);
            Assert.Equal(3, redrawn.Sheets);
        }
        finally
        {
            HwpDocument.CacheFolder = old;
        }
    }

    [Fact]
    public void Hwpx_WithoutSections_IsNull() => Assert.Null(HwpDocument.Read(Zip(("mimetype", Utf8("application/hwp+zip")))));

    // ---- HWP builders ----

    private static byte[] Record(int tag, int level, byte[] payload)
    {
        var bytes = new List<byte>();
        uint head = (uint)tag | ((uint)level << 10) | ((uint)Math.Min(payload.Length, 0xFFF) << 20);
        bytes.AddRange(BitConverter.GetBytes(head));
        if (payload.Length >= 0xFFF)
            bytes.AddRange(BitConverter.GetBytes((uint)payload.Length));

        bytes.AddRange(payload);
        return [.. bytes];
    }

    private static byte[] Bytes(params object[] parts)
    {
        var bytes = new List<byte>();
        foreach (object part in parts)
        {
            bytes.AddRange(part switch
            {
                byte b => [b],
                ushort u => BitConverter.GetBytes(u),
                uint u => BitConverter.GetBytes(u),
                int i => BitConverter.GetBytes(i),
                byte[] a => a,
                string s => Encoding.Unicode.GetBytes(s),
                _ => throw new ArgumentException(part.GetType().Name),
            });
        }

        return [.. bytes];
    }

    /// <summary>DocInfo: one Hangul font, char shape 0 (10pt) and 1 (bold red 16pt), para shape 0 (left) and 1 (center), a bordered yellow fill.</summary>
    private static byte[] DocInfo()
    {
        byte[] CharShape(int size, uint attr, uint color) => Bytes(new byte[42], size, attr, (byte)0, (byte)0, color, new byte[16]);
        byte[] ParaShape(uint align) => Bytes(align << 2, 0, 0, 0, 0, 0, 160u, new byte[30]);
        byte[] borders = Bytes((ushort)0, (byte)1, (byte)0, 0u, (byte)1, (byte)0, 0u, (byte)1, (byte)0, 0u, (byte)1, (byte)0, 0u, new byte[6], 1u, 0x0000FFFFu, new byte[8]);
        return
        [
            .. Record(17, 0, Bytes(0u, 1u, new byte[64])),
            .. Record(19, 0, Bytes((byte)0, (ushort)5, "맑은 고딕")),
            .. Record(20, 0, borders),
            .. Record(21, 0, CharShape(1000, 0, 0)),
            .. Record(21, 0, CharShape(1600, 2, 0x000000FF)),
            .. Record(25, 0, ParaShape(1)),
            .. Record(25, 0, ParaShape(3)),
        ];
    }

    private static byte[] ParaText(string text, bool table = false)
    {
        var chars = new List<byte>();
        if (table)
            chars.AddRange(Bytes((ushort)11, new byte[14]));

        chars.AddRange(Encoding.Unicode.GetBytes(text));
        chars.AddRange(BitConverter.GetBytes((ushort)13));
        return [.. chars];
    }

    private static byte[] Paragraph(string text, int level = 0, ushort paraShape = 0, uint charShape = 0, byte breakType = 0, byte[]? controls = null)
    {
        byte[] header = Bytes(0u, 0u, paraShape, (byte)0, breakType, new byte[10]);
        return
        [
            .. Record(66, level, header),
            .. Record(67, level + 1, ParaText(text, controls is not null)),
            .. Record(68, level + 1, Bytes(0u, charShape)),
            .. controls ?? [],
        ];
    }

    private static byte[] Cell(ushort column, ushort row, ushort rowSpan, string text, int level) =>
    [
        .. Record(72, level, Bytes((ushort)1, 0u, (ushort)0, column, row, (ushort)1, rowSpan, 5000u, 1000u, new byte[8], (ushort)1, new byte[8])),
        .. Paragraph(text, level),
    ];

    private static byte[] SampleSection()
    {
        byte[] pageDef = Record(73, 2, Bytes(59528u, 84188u, 8504u, 8504u, 5668u, 4252u, 4252u, 4252u, 0u, 0u));
        byte[] secd = [.. Record(71, 1, Bytes(Encoding.ASCII.GetBytes("dces"))), .. pageDef];
        byte[] header = [.. Record(71, 1, Bytes(Encoding.ASCII.GetBytes("daeh"))), .. Paragraph("머리말", level: 2)];
        byte[] table =
        [
            .. Record(71, 1, Bytes(Encoding.ASCII.GetBytes(" lbt"), new byte[40])),
            .. Record(77, 2, Bytes(0u, (ushort)1, (ushort)2, new byte[20])),
            .. Cell(0, 0, 1, "셀A", 2),
            .. Cell(1, 0, 2, "셀B", 2),
        ];
        return
        [
            .. Paragraph("제목", paraShape: 1, charShape: 1, controls: [.. secd, .. header]),
            .. Paragraph("", controls: table),
            .. Paragraph("다음 쪽", breakType: 0x04),
        ];
    }

    private static byte[] Deflate(byte[] data)
    {
        var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);

        return buffer.ToArray();
    }

    private static byte[] FileHeader(uint flags)
    {
        var header = new byte[256];
        Encoding.ASCII.GetBytes("HWP Document File").CopyTo(header, 0);
        BitConverter.GetBytes(flags).CopyTo(header, 36);
        return header;
    }

    private static byte[] Hwp(uint flags)
    {
        bool compressed = (flags & 1) != 0;
        byte[] Part(byte[] d) => compressed ? Deflate(d) : d;
        return CompoundFile(FileHeader(flags), Part(DocInfo()), Part(SampleSection()));
    }

    /// <summary>A minimal OLE compound file (512-byte sectors): FileHeader, DocInfo and BodyText/Section0 as regular streams.</summary>
    private static byte[] CompoundFile(byte[] fileHeader, byte[] docInfo, byte[] section)
    {
        byte[][] streams = [Pad(fileHeader), Pad(docInfo), Pad(section)];
        var fat = new uint[128];
        Array.Fill(fat, NoStream);
        fat[0] = 0xFFFFFFFD;
        fat[1] = EndOfChain;
        uint next = 2;
        var starts = new uint[streams.Length];
        for (int s = 0; s < streams.Length; s++)
        {
            starts[s] = next;
            int sectors = streams[s].Length / 512;
            for (int i = 0; i < sectors; i++, next++)
                fat[next] = i == sectors - 1 ? EndOfChain : next + 1;
        }

        var file = new MemoryStream();
        var writer = new BinaryWriter(file);
        var head = new byte[512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(head, 0);
        BitConverter.GetBytes((ushort)0x3E).CopyTo(head, 24);
        BitConverter.GetBytes((ushort)3).CopyTo(head, 26);
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(head, 28);
        BitConverter.GetBytes((ushort)9).CopyTo(head, 30);
        BitConverter.GetBytes((ushort)6).CopyTo(head, 32);
        BitConverter.GetBytes(1).CopyTo(head, 44);
        BitConverter.GetBytes(1).CopyTo(head, 48);
        BitConverter.GetBytes(4096).CopyTo(head, 56);
        BitConverter.GetBytes(EndOfChain).CopyTo(head, 60);
        BitConverter.GetBytes(EndOfChain).CopyTo(head, 68);
        for (int i = 0; i < 109; i++)
            BitConverter.GetBytes(i == 0 ? 0u : NoStream).CopyTo(head, 76 + (i * 4));

        writer.Write(head);
        foreach (uint entry in fat)
            writer.Write(entry);

        // 0 root -> 2 FileHeader (left 3 DocInfo, right 1 BodyText); BodyText -> 4 Section0.
        writer.Write(Entry("Root Entry", 5, NoStream, NoStream, 2, EndOfChain, 0));
        writer.Write(Entry("BodyText", 1, NoStream, NoStream, 4, 0, 0));
        writer.Write(Entry("FileHeader", 2, 3, 1, NoStream, starts[0], streams[0].Length));
        writer.Write(Entry("DocInfo", 2, NoStream, NoStream, NoStream, starts[1], streams[1].Length));
        writer.Write(new byte[512 - (4 * 128)]);

        // The directory needs a second sector for Section0: put it after the streams.
        uint secondDirectory = next;
        fat[1] = secondDirectory;
        fat[secondDirectory] = EndOfChain;
        foreach (byte[] stream in streams)
            writer.Write(stream);

        writer.Write(Entry("Section0", 2, NoStream, NoStream, NoStream, starts[2], streams[2].Length));
        writer.Write(new byte[512 - 128]);

        byte[] result = file.ToArray();
        for (int i = 0; i < fat.Length; i++)
            BitConverter.GetBytes(fat[i]).CopyTo(result, 512 + (i * 4));

        return result;

        static byte[] Pad(byte[] data)
        {
            // Streams of 4096 bytes and more live in regular sectors, so pad the small ones up to that.
            var padded = new byte[Math.Max(4096, (data.Length + 511) / 512 * 512)];
            data.CopyTo(padded, 0);
            return padded;
        }

        static byte[] Entry(string name, byte type, uint left, uint right, uint child, uint start, long size)
        {
            var entry = new byte[128];
            Encoding.Unicode.GetBytes(name).CopyTo(entry, 0);
            BitConverter.GetBytes((ushort)((name.Length + 1) * 2)).CopyTo(entry, 64);
            entry[66] = type;
            entry[67] = 1;
            BitConverter.GetBytes(left).CopyTo(entry, 68);
            BitConverter.GetBytes(right).CopyTo(entry, 72);
            BitConverter.GetBytes(child).CopyTo(entry, 76);
            BitConverter.GetBytes(start).CopyTo(entry, 116);
            BitConverter.GetBytes(size).CopyTo(entry, 120);
            return entry;
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(0u)]
    public void Hwp_ReadsShapesTablesAndPageBreaksAndSkipsHeaders(uint flags)
    {
        HwpDoc doc = HwpDocument.Read(Hwp(flags))!;

        var title = (HwpPara)doc.Blocks[0];
        var run = (HwpRun)title.Runs[0];
        Assert.Equal(("제목", "center", 16.0, true, "#ff0000", "맑은 고딕"), (run.Text, title.Align, run.Size!.Value, run.Bold, run.Color, run.Font));
        Assert.Equal(1.6, title.Line);

        HwpTable table = doc.Blocks.OfType<HwpTable>().Single();
        Assert.True(table.Border);
        Assert.Equal(("셀A", "셀B", 2, "#ffff00"), (Text(table.Rows[0][0]), Text(table.Rows[0][1]), table.Rows[0][1].RowSpan, table.Rows[0][0].Background));
        Assert.Equal([50.0, 50.0], table.ColumnWidths.Select(w => w!.Value));

        HwpPara last = (HwpPara)doc.Blocks[^1];
        Assert.Equal("다음 쪽", ((HwpRun)last.Runs[0]).Text);
        Assert.True(last.PageBreak);
        Assert.DoesNotContain(doc.Blocks.OfType<HwpPara>(), p => p.Runs.OfType<HwpRun>().Any(r => r.Text == "머리말"));
        Assert.Equal((595.28, 85.04, 99.2), (doc.Page.Width, doc.Page.Left, doc.Page.Top));

        static string Text(HwpCell cell) => string.Concat(cell.Blocks.OfType<HwpPara>().SelectMany(p => p.Runs.OfType<HwpRun>()).Select(r => r.Text));
    }

    [Theory]
    [InlineData(0x02u)] // password
    [InlineData(0x04u)] // distribution document
    [InlineData(0x10u)] // DRM
    public void Hwp_ProtectedDocuments_AreNull(uint flags) => Assert.Null(HwpDocument.Read(Hwp(flags)));

    [Fact]
    public void DamagedFiles_AreNullNotExceptions()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("bad.hwp"), new byte[600]);
        File.WriteAllText(dir.File("bad.hwpx"), "not a zip");
        File.WriteAllBytes(dir.File("cut.hwpx"), SampleHwpx()[..300]);

        Assert.Null(HwpDocument.Read(dir.File("bad.hwp")));
        Assert.Null(HwpDocument.Read(dir.File("bad.hwpx")));
        Assert.Null(HwpDocument.Read(dir.File("cut.hwpx")));
    }

    // ---- preview pane ----

    [Fact]
    public void PreviewPane_ShowsHwpxInTheBuiltInViewerFittedToTheWidth()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("보고서.hwpx"), SampleHwpx());
        PreviewPane? pane = null;

        Run(() =>
        {
            var form = new Form { Width = 500, Height = 600, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir hwp preview" };
            pane = new PreviewPane { Dock = DockStyle.Fill };
            form.Controls.Add(pane);
            return form;
        }, async _ =>
        {
            await pane!.ShowAsync(ShellLocation.Parse(dir.File("보고서.hwpx"))!, 1);

            Assert.Equal(PreviewKind.Document, pane.Kind);
            Assert.Contains("내장 한글 뷰어", pane.Detail);
            var view = (HwpView)pane.View!;
            await WaitUntil(() => view.Loaded, "the document loaded");
            Assert.Contains("본문 첫 쪽", view.Document!.Body!.InnerText);
            Assert.InRange(view.ZoomPercent, 20, 99);
            Assert.False(view.AllowNavigation, "nothing on the page navigates");
        });
    }
}
