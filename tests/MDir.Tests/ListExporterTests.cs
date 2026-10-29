using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MDir.Features;

namespace MDir.Tests;

public class ListExporterTests
{
    private static readonly ExportRow[] Rows =
    [
        new("보고서.txt", "텍스트 문서", 1234, new DateTime(2026, 10, 29, 13, 5, 9), @"C:\Docs\보고서.txt", false),
        new("a,b \"quoted\".csv", "CSV", 0, new DateTime(2026, 1, 2, 3, 4, 5), @"C:\Docs\a,b ""quoted"".csv", false),
        new("<folder> & more", "파일 폴더", null, new DateTime(2025, 12, 31, 23, 59, 59), @"C:\Docs\<folder> & more", true),
    ];

    [Fact]
    public void Text_IsTabSeparatedWithAHeader()
    {
        string[] lines = ListExporter.ToText(Rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("이름\t종류\t크기(바이트)\t수정한 날짜\t경로", lines[0]);
        Assert.Equal("보고서.txt\t텍스트 문서\t1234\t2026-10-29 13:05:09\tC:\\Docs\\보고서.txt", lines[1]);
        Assert.Equal(4, lines.Length);
        Assert.Contains("\t\t2025-12-31", lines[3]);
    }

    [Fact]
    public void Text_NeverBreaksTheTableForTabsOrLineBreaksInNames()
    {
        ExportRow[] rows = [new("a\tb\r\nc", "x", 1, DateTime.UnixEpoch, "p", false)];

        string[] lines = ListExporter.ToText(rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal(5, lines[1].Split('\t').Length);
    }

    [Fact]
    public void Csv_QuotesFieldsWithCommasQuotesAndLineBreaks()
    {
        string csv = ListExporter.ToCsv(Rows);

        Assert.StartsWith("이름,종류,크기(바이트),수정한 날짜,경로\r\n", csv);
        Assert.Contains("\"a,b \"\"quoted\"\".csv\",CSV,0,2026-01-02 03:04:05,\"C:\\Docs\\a,b \"\"quoted\"\".csv\"", csv);
        Assert.Contains("<folder> & more,파일 폴더,,2025-12-31 23:59:59,", csv);
    }

    [Fact]
    public void Csv_RoundTripsThroughAStrictParser()
    {
        string[][] parsed = ParseCsv(ListExporter.ToCsv(Rows));

        Assert.Equal(4, parsed.Length);
        Assert.All(parsed, row => Assert.Equal(5, row.Length));
        Assert.Equal("a,b \"quoted\".csv", parsed[2][0]);
        Assert.Equal(@"C:\Docs\a,b ""quoted"".csv", parsed[2][4]);
    }

    [Fact]
    public void Html_EscapesMarkupAndIsAWellFormedTable()
    {
        string html = ListExporter.ToHtml(Rows);

        Assert.Contains("&lt;folder&gt; &amp; more", html);
        Assert.DoesNotContain("<folder>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Equal(4, html.Split("<tr>").Length - 1);
        Assert.Contains("<td class=\"num\">1234</td>", html);
    }

    [Fact]
    public void Write_UsesUtf8WithABomOrUtf16AsAsked_AndHtmlIsAlwaysUtf8()
    {
        using var dir = new TempDirectory();

        ListExporter.Write(dir.File("a.csv"), Rows, ExportFormat.Csv);
        ListExporter.Write(dir.File("b.txt"), Rows, ExportFormat.Text, utf16: true);
        ListExporter.Write(dir.File("c.html"), Rows, ExportFormat.Html, utf16: true);

        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(dir.File("a.csv"))[..3]);
        Assert.Equal([0xFF, 0xFE], File.ReadAllBytes(dir.File("b.txt"))[..2]);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(dir.File("c.html"))[..3]);
        Assert.Contains("보고서.txt", File.ReadAllText(dir.File("a.csv")));
        Assert.Contains("보고서.txt", File.ReadAllText(dir.File("b.txt")));
        Assert.Equal([".txt", ".csv", ".html", ".xlsx"], new[] { ExportFormat.Text, ExportFormat.Csv, ExportFormat.Html, ExportFormat.Xlsx }.Select(ListExporter.Extension));
    }

    [Fact]
    public void Xlsx_IsAValidPackageWithTypedCells()
    {
        using var dir = new TempDirectory();
        ListExporter.Write(dir.File("list.xlsx"), Rows, ExportFormat.Xlsx);

        using ZipArchive zip = ZipFile.OpenRead(dir.File("list.xlsx"));
        string[] expectedParts = ["[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml"];
        Assert.All(expectedParts, part => Assert.NotNull(zip.GetEntry(part)));
        foreach (string part in expectedParts)
        {
            using Stream stream = zip.GetEntry(part)!.Open();
            XDocument.Load(stream); // every part is well-formed XML
        }

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XDocument sheet;
        using (Stream stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
            sheet = XDocument.Load(stream);

        XElement[] sheetRows = [.. sheet.Descendants(ns + "row")];
        Assert.Equal(4, sheetRows.Length);
        XElement[] first = [.. sheetRows[1].Elements(ns + "c")];
        Assert.Equal("보고서.txt", first[0].Descendants(ns + "t").Single().Value);
        Assert.Equal("1234", first[2].Element(ns + "v")!.Value);
        Assert.Null(first[2].Attribute("t"));
        Assert.Equal("1", first[3].Attribute("s")!.Value);
        Assert.Equal(new DateTime(2026, 10, 29, 13, 5, 9).ToOADate(), double.Parse(first[3].Element(ns + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);

        // A folder has no size: the cell is empty, not zero.
        XElement[] folder = [.. sheetRows[3].Elements(ns + "c")];
        Assert.Null(folder[2].Element(ns + "v"));
        Assert.Equal("<folder> & more", folder[0].Descendants(ns + "t").Single().Value);
    }

    [Fact]
    public void Xlsx_ReplacesCharactersXmlCannotCarry()
    {
        using var stream = new MemoryStream();
        ListExporter.WriteXlsx(stream, [new ExportRow("bad\u0001name", "t", 1, DateTime.UnixEpoch, "p", false)]);
        stream.Position = 0;

        using var zip = new ZipArchive(stream);
        using Stream sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XDocument document = XDocument.Load(sheet);

        Assert.Contains("bad?name", document.ToString());
    }

    /// <summary>A small RFC 4180 reader to prove the writer's output can be read back.</summary>
    private static string[][] ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                rows.Add([.. fields]);
                fields.Clear();
                i++;
            }
            else
            {
                field.Append(c);
            }
        }

        return [.. rows];
    }
}
