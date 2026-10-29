using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;

namespace MDir.Features;

public enum ExportFormat
{
    Text,
    Csv,
    Html,
    Xlsx,
}

/// <summary>One line of an exported file list.</summary>
/// <param name="Size">Bytes, or null for a folder.</param>
internal sealed record ExportRow(string Name, string Type, long? Size, DateTime Modified, string Path, bool IsFolder);

/// <summary>Writes a file list as text, CSV, HTML or an Excel workbook.</summary>
internal static class ListExporter
{
    public static readonly string[] Headers = ["이름", "종류", "크기(바이트)", "수정한 날짜", "경로"];

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Html => ".html",
        ExportFormat.Xlsx => ".xlsx",
        _ => ".txt",
    };

    /// <summary>
    /// Writes <paramref name="rows"/> to <paramref name="path"/>. Text and CSV use UTF-8 with a BOM (so Excel opens
    /// Korean text correctly) or, when <paramref name="utf16"/> is set, UTF-16.
    /// </summary>
    public static void Write(string path, IReadOnlyList<ExportRow> rows, ExportFormat format, bool utf16 = false)
    {
        if (format == ExportFormat.Xlsx)
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            WriteXlsx(file, rows);
            return;
        }

        string text = format switch
        {
            ExportFormat.Csv => ToCsv(rows),
            ExportFormat.Html => ToHtml(rows),
            _ => ToText(rows),
        };

        // HTML declares its charset, so it is always UTF-8.
        Encoding encoding = utf16 && format != ExportFormat.Html ? new UnicodeEncoding(false, true) : new UTF8Encoding(true);
        File.WriteAllText(path, text, encoding);
    }

    public static string ToText(IReadOnlyList<ExportRow> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join('\t', Headers)).Append("\r\n");
        foreach (ExportRow row in rows)
            text.Append(string.Join('\t', Fields(row).Select(field => field.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')))).Append("\r\n");

        return text.ToString();
    }

    public static string ToCsv(IReadOnlyList<ExportRow> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join(',', Headers.Select(Quote))).Append("\r\n");
        foreach (ExportRow row in rows)
            text.Append(string.Join(',', Fields(row).Select(Quote))).Append("\r\n");

        return text.ToString();
    }

    public static string ToHtml(IReadOnlyList<ExportRow> rows)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"ko\">\r\n<head>\r\n<meta charset=\"utf-8\">\r\n<title>파일 목록</title>\r\n");
        html.Append("<style>body{font-family:'Segoe UI','Malgun Gothic',sans-serif}table{border-collapse:collapse}th,td{border:1px solid #bbb;padding:3px 8px;text-align:left}th{background:#eee}td.num{text-align:right}</style>\r\n");
        html.Append("</head>\r\n<body>\r\n<table>\r\n<tr>");
        foreach (string header in Headers)
            html.Append("<th>").Append(WebUtility.HtmlEncode(header)).Append("</th>");
        html.Append("</tr>\r\n");
        foreach (ExportRow row in rows)
        {
            string[] fields = Fields(row);
            html.Append("<tr>");
            for (int i = 0; i < fields.Length; i++)
                html.Append(i == 2 ? "<td class=\"num\">" : "<td>").Append(WebUtility.HtmlEncode(fields[i])).Append("</td>");
            html.Append("</tr>\r\n");
        }

        html.Append("</table>\r\n</body>\r\n</html>\r\n");
        return html.ToString();
    }

    /// <summary>
    /// A minimal Excel workbook: one sheet, a bold header, sizes as numbers and dates as real dates (so they sort
    /// and filter). Text is stored inline, so no shared-strings part is needed.
    /// </summary>
    public static void WriteXlsx(Stream output, IReadOnlyList<ExportRow> rows)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        AddPart(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
            """);
        AddPart(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        AddPart(zip, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="파일 목록" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        AddPart(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);

        // Styles: 0 = normal, 1 = date and time, 2 = bold header.
        AddPart(zip, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy\-mm\-dd\ hh:mm:ss"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>
            """);

        ZipArchiveEntry sheet = zip.CreateEntry("xl/worksheets/sheet1.xml");
        using var stream = sheet.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false });
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("worksheet", ns);
        xml.WriteStartElement("cols");
        foreach ((int index, int width) in new[] { (1, 40), (2, 22), (3, 16), (4, 22), (5, 70) })
        {
            xml.WriteStartElement("col");
            xml.WriteAttributeString("min", index.ToString());
            xml.WriteAttributeString("max", index.ToString());
            xml.WriteAttributeString("width", width.ToString());
            xml.WriteAttributeString("customWidth", "1");
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
        xml.WriteStartElement("sheetData");
        WriteRow(xml, 1, Headers.Select(header => (object?)header).ToArray(), headerStyle: true);
        for (int i = 0; i < rows.Count; i++)
        {
            ExportRow row = rows[i];
            WriteRow(xml, i + 2, [row.Name, row.Type, row.Size, row.Modified, row.Path], headerStyle: false);
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter xml, int rowNumber, object?[] values, bool headerStyle)
    {
        xml.WriteStartElement("row");
        xml.WriteAttributeString("r", rowNumber.ToString());
        for (int column = 0; column < values.Length; column++)
        {
            object? value = values[column];
            string reference = $"{(char)('A' + column)}{rowNumber}";
            xml.WriteStartElement("c");
            xml.WriteAttributeString("r", reference);
            switch (value)
            {
                case null:
                    break;
                case long number:
                    xml.WriteElementString("v", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case DateTime date:
                    xml.WriteAttributeString("s", "1");
                    xml.WriteElementString("v", date.ToOADate().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    xml.WriteAttributeString("t", "inlineStr");
                    if (headerStyle)
                        xml.WriteAttributeString("s", "2");
                    xml.WriteStartElement("is");
                    xml.WriteStartElement("t");
                    xml.WriteAttributeString("xml", "space", null, "preserve");
                    xml.WriteString(XmlSafe(value.ToString() ?? string.Empty));
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                    break;
            }

            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    private static void AddPart(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content.TrimStart());
    }

    private static string[] Fields(ExportRow row) =>
    [
        row.Name,
        row.Type,
        row.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        row.Modified.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        row.Path,
    ];

    /// <summary>RFC 4180: quote a field that holds a comma, a quote or a line break, doubling inner quotes.</summary>
    private static string Quote(string field) =>
        field.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field;

    /// <summary>File names may hold characters XML cannot carry (control characters): they are shown as '?'.</summary>
    private static string XmlSafe(string text) => new([.. text.Select(c => XmlConvert.IsXmlChar(c) || char.IsSurrogate(c) ? c : '?')]);
}
