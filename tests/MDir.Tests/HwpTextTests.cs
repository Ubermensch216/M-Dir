using System.IO.Compression;
using System.Text;
using MDir.Features;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>The Hancom (.hwp/.hwpx) text reader that lets the preview pane show every page, not only the first.</summary>
public class HwpTextTests
{
    private const uint NoStream = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;

    // ---- builders ----

    private static byte[] Hwpx(params (string Name, string Xml)[] parts)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string xml) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
        }

        return buffer.ToArray();
    }

    private static string Section(string body) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><hs:sec xmlns:hs="http://www.hancom.co.kr/hwpml/2011/section" xmlns:hp="http://www.hancom.co.kr/hwpml/2011/paragraph">{body}</hs:sec>""";

    private static string Para(string text, bool pageBreak = false) =>
        $"""<hp:p pageBreak="{(pageBreak ? 1 : 0)}"><hp:run><hp:t>{text}</hp:t></hp:run></hp:p>""";

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

    /// <summary>PARA_HEADER + PARA_TEXT for one paragraph; <paramref name="breakType"/> is the paragraph's break flags.</summary>
    private static byte[] HwpParagraph(string text, byte breakType = 0, int level = 0, bool extendedControl = false)
    {
        var header = new byte[22];
        header[11] = breakType;
        var chars = new List<byte>();
        if (extendedControl)
        {
            chars.AddRange(BitConverter.GetBytes((ushort)11)); // a table: control char + 14 bytes of control info
            chars.AddRange(new byte[14]);
        }

        chars.AddRange(Encoding.Unicode.GetBytes(text));
        chars.AddRange(BitConverter.GetBytes((ushort)13));
        return [.. Record(0x42, level, header), .. Record(0x43, level + 1, [.. chars])];
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

    /// <summary>A minimal OLE compound file (512-byte sectors) holding FileHeader and BodyText/Section0 as regular streams.</summary>
    private static byte[] Hwp(uint flags, byte[] section0)
    {
        byte[] fileHeader = Pad(FileHeader(flags));
        byte[] body = Pad(section0);
        int headerSectors = fileHeader.Length / 512, bodySectors = body.Length / 512;

        var fat = new uint[128];
        Array.Fill(fat, NoStream);
        fat[0] = 0xFFFFFFFD; // the FAT sector itself
        fat[1] = EndOfChain; // the directory
        uint next = 2;
        uint headerStart = next;
        for (int i = 0; i < headerSectors; i++, next++)
            fat[next] = i == headerSectors - 1 ? EndOfChain : next + 1;

        uint bodyStart = next;
        for (int i = 0; i < bodySectors; i++, next++)
            fat[next] = i == bodySectors - 1 ? EndOfChain : next + 1;

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

        // Directory: root -> FileHeader (right sibling: BodyText) ; BodyText -> Section0.
        writer.Write(Entry("Root Entry", 5, NoStream, NoStream, 2, EndOfChain, 0));
        writer.Write(Entry("BodyText", 1, NoStream, NoStream, 3, 0, 0));
        writer.Write(Entry("FileHeader", 2, NoStream, 1, NoStream, headerStart, fileHeader.Length));
        writer.Write(Entry("Section0", 2, NoStream, NoStream, NoStream, bodyStart, body.Length));
        // The root's child (index 2) is FileHeader whose right sibling is BodyText (index 1).
        writer.Write(fileHeader);
        writer.Write(body);
        return file.ToArray();

        static byte[] Pad(byte[] data)
        {
            // Streams of 4096 bytes and more live in regular sectors, so pad the small ones up to that.
            int length = Math.Max(4096, (data.Length + 511) / 512 * 512);
            var padded = new byte[length];
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

    private static string Lines(string? text) => (text ?? string.Empty).ReplaceLineEndings("\n");

    // ---- HWPX ----

    [Fact]
    public void ReadHwpx_ReturnsEverySectionInNumericOrderWithPageBreakLines()
    {
        byte[] data = Hwpx(
            ("Contents/section10.xml", Section(Para("열번째"))),
            ("Contents/section0.xml", Section(Para("첫 쪽") + Para("둘째 쪽", pageBreak: true))),
            ("Contents/section2.xml", Section(Para("셋째"))),
            ("Contents/content.hpf", "<x/>"));

        string? text = HwpText.ReadHwpx(data);

        Assert.Equal($"첫 쪽\n{HwpText.PageBreakLine}\n둘째 쪽\n셋째\n열번째", Lines(text));
    }

    [Fact]
    public void ReadHwpx_KeepsTabsAndLineBreaksAndTableCellParagraphs()
    {
        string body = """
            <hp:p><hp:run><hp:t>가<hp:tab/>나<hp:lineBreak/>다</hp:t></hp:run></hp:p>
            <hp:p><hp:run><hp:tbl><hp:tr><hp:tc><hp:subList><hp:p><hp:run><hp:t>셀1</hp:t></hp:run></hp:p></hp:subList></hp:tc>
            <hp:tc><hp:subList><hp:p><hp:run><hp:t>셀2</hp:t></hp:run></hp:p></hp:subList></hp:tc></hp:tr></hp:tbl></hp:run></hp:p>
            """;

        string text = Lines(HwpText.ReadHwpx(Hwpx(("Contents/section0.xml", Section(body)))));

        Assert.Contains("가\t나\n다", text);
        Assert.Contains("셀1\n셀2", text);
    }

    [Fact]
    public void ReadHwpx_WithoutSections_IsNull() =>
        Assert.Null(HwpText.ReadHwpx(Hwpx(("mimetype", "application/hwp+zip"))));

    // ---- HWP ----

    [Fact]
    public void AppendHwpSection_SkipsControlInfoAndMarksPageBreaksAndSkipsHeaderFooter()
    {
        byte[] ctrlHeader = Record(0x47, 0, "daeh"u8.ToArray().Concat(new byte[8]).ToArray());
        byte[] section = [
            .. HwpParagraph("첫 문단", extendedControl: true),
            .. ctrlHeader,
            .. HwpParagraph("머리말은 빠짐", level: 1),
            .. HwpParagraph("새 쪽", breakType: 0x04),
        ];
        var text = new StringBuilder();

        HwpText.AppendHwpSection(section, text);

        Assert.Equal($"첫 문단\n{HwpText.PageBreakLine}\n새 쪽\n", Lines(text.ToString()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadHwp_ReadsAllParagraphsFromCompoundFile(bool compressed)
    {
        byte[] section = [.. HwpParagraph("한 쪽"), .. HwpParagraph("두 쪽", breakType: 0x04), .. HwpParagraph("세 쪽", breakType: 0x04)];

        string? text = HwpText.ReadHwp(Hwp(compressed ? 1u : 0u, compressed ? Deflate(section) : section));

        Assert.Equal($"한 쪽\n{HwpText.PageBreakLine}\n두 쪽\n{HwpText.PageBreakLine}\n세 쪽", Lines(text));
    }

    [Theory]
    [InlineData(0x02u)] // password
    [InlineData(0x04u)] // distribution document
    [InlineData(0x10u)] // DRM
    public void ReadHwp_ProtectedDocuments_AreNull(uint flags) =>
        Assert.Null(HwpText.ReadHwp(Hwp(flags, HwpParagraph("비밀"))));

    [Fact]
    public void ReadHwp_NotACompoundFile_IsNull() => Assert.Null(HwpText.ReadHwp(Encoding.ASCII.GetBytes(new string('x', 2000))));

    [Fact]
    public void Read_DamagedFile_IsNullNotAnException()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("bad.hwp"), new byte[600]);
        File.WriteAllText(dir.File("bad.hwpx"), "not a zip");

        Assert.Null(HwpText.Read(dir.File("bad.hwp")));
        Assert.Null(HwpText.Read(dir.File("bad.hwpx")));
    }

    // ---- preview pane ----

    /// <summary>
    /// A made-up .hwpx (not one Hancom can open): with Hancom's handler or Office installed it is handled by them,
    /// otherwise the pane falls back to the body text. Never an error page.
    /// </summary>
    [Fact]
    public void PreviewPane_ShowsHwpxWithHancomOrFallsBackToBodyText()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("보고서.hwpx"), Hwpx(("Contents/section0.xml", Section(Para("첫 쪽") + Para("마지막 쪽", pageBreak: true)))));
        PreviewPane? pane = null;

        Run(() =>
        {
            var form = new Form { Width = 700, Height = 500, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir hwp preview" };
            pane = new PreviewPane { Dock = DockStyle.Fill };
            form.Controls.Add(pane);
            return form;
        }, async _ =>
        {
            await pane!.ShowAsync(ShellLocation.Parse(dir.File("보고서.hwpx"))!, 1);

            if (pane.Kind == PreviewKind.Text)
            {
                Assert.Contains("첫 쪽", ((TextBox)pane.View!).Text);
                Assert.Contains("마지막 쪽", ((TextBox)pane.View!).Text);
            }
            else
            {
                Assert.Equal(PreviewKind.Handler, pane.Kind);
                Assert.DoesNotContain("변환하는 중", pane.Detail);
            }
        });
    }
}
