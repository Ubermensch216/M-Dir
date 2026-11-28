using System.IO.Compression;
using System.Text;
using MDir.Features;
using MDir.Features.Hwp;
using MDir.Features.Office;
using MDir.Finder.Engine;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>
/// Tests for document parsing and preview improvements benchmarked from kordoc:
/// PUA mapping, auto-numbering, equations, distribution HWP decryption, per-side borders,
/// lineseg page breaks, track-changes filtering, broken-ZIP recovery, HWPML, and built-in Office preview fallback.
/// </summary>
public class KordocBenchmarkedParsingTests
{
    private const string HwpxNs = """xmlns:hh="http://www.hancom.co.kr/hwpml/2011/head" xmlns:hp="http://www.hancom.co.kr/hwpml/2011/paragraph" xmlns:hc="http://www.hancom.co.kr/hwpml/2011/core" xmlns:hs="http://www.hancom.co.kr/hwpml/2011/section" """;

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    private static byte[] MakeZip(params (string Name, byte[] Data)[] parts)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in parts)
            {
                using Stream s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(data);
            }
        }

        return ms.ToArray();
    }

    // ---- 1. PUA & Symbol Mapping ----

    [Fact]
    public void PuaMapper_ResolvesBmpSymbolsWingdingsFoldedPuaAAndPairedNumbers()
    {
        Assert.Equal("● ☑ ➔ ❶", HwpPuaMapper.MapText("\uF06C \uF0FE \uF0E8 \uF08C"));
        Assert.Equal("홍길동 (인) [예]", HwpPuaMapper.MapText("홍길동 \uA12B \uA0DA", unfoldHwp5: true));
        Assert.Equal("(인)", HwpPuaMapper.MapText("\uA0E1", unfoldHwp5: true));

        // Paired two-digit enclosed number: F0289 (tens=1) + F0298 (units=6) -> ⑯
        string paired = char.ConvertFromUtf32(0xF0289) + char.ConvertFromUtf32(0xF0298);
        Assert.Equal("⑯", HwpPuaMapper.MapText(paired));

        // Unpaired lone surrogate becomes □ (matching Hancom Hangul).
        Assert.Equal("A□B", HwpPuaMapper.MapText("A\uD800B"));

        // Unmapped BMP PUA (archaic Hangul) preserved; unmapped Supplementary PUA-A stripped.
        string unknownSupp = char.ConvertFromUtf32(0xF0999);
        Assert.Equal("\uE0BC", HwpPuaMapper.MapText("\uE0BC" + unknownSupp));
    }

    // ---- 2. Auto-Numbering ----

    [Fact]
    public void Numbering_FormatsKoreanCircledRomanAndTracksHierarchy()
    {
        Assert.Equal("가", HwpNumbering.HangulOrdinal(0));
        Assert.Equal("하", HwpNumbering.HangulOrdinal(13));
        Assert.Equal("거", HwpNumbering.HangulOrdinal(14));
        Assert.Equal("①", HwpNumbering.CircledNumber(0));
        Assert.Equal("㊿", HwpNumbering.CircledNumber(49));
        Assert.Equal("(51)", HwpNumbering.CircledNumber(50));
        Assert.Equal("㉮", HwpNumbering.CircledHangul(0));
        Assert.Equal("IV", HwpNumbering.RomanNumeral(4, upper: true));

        var state = new HwpNumberingState();
        string[] fmts = ["DIGIT", "HANGUL_SYLLABLE", "CIRCLED_DIGIT"];
        int[] starts = [1, 1, 1];

        Assert.Equal("1.", HwpNumbering.Expand("^1.", state.Advance("n1", 0), fmts, starts));
        Assert.Equal("1.가)", HwpNumbering.Expand("^1.^2)", state.Advance("n1", 1), fmts, starts));
        Assert.Equal("1.나)", HwpNumbering.Expand("^1.^2)", state.Advance("n1", 1), fmts, starts));
        Assert.Equal("2.", HwpNumbering.Expand("^1.", state.Advance("n1", 0), fmts, starts));
        Assert.Equal("2.가)", HwpNumbering.Expand("^1.^2)", state.Advance("n1", 1), fmts, starts));

        // Stray '^' before non-alphanumeric chars is stripped.
        Assert.Equal("□", HwpNumbering.Expand("^  □", [1], fmts, starts));
    }

    // ---- 3. Equations ----

    [Fact]
    public void Equation_ConvertsHancomScriptToUnicodeAndStackedHtml()
    {
        const string quad = "x = { -b +- SQRT { b^2 - 4ac } } over { 2a }";
        string plain = HwpEquation.ToPlainText(quad);
        Assert.Contains("±", plain);
        Assert.Contains("√", plain);
        Assert.Contains("b²", plain);
        Assert.Contains("/", plain);

        string html = HwpEquation.ToHtml(quad);
        Assert.Contains("class=\"eq\"", html);
        Assert.Contains("class=\"eq-frac\"", html);
        Assert.Contains("class=\"eq-num\"", html);
        Assert.Contains("class=\"eq-den\"", html);
        Assert.Contains("<sup>2</sup>", html);

        Assert.Equal("α + β ≤ γ", HwpEquation.ToPlainText("alpha + beta LEQ gamma"));
        Assert.Equal("³√x", HwpEquation.ToPlainText("root 3 of x"));
    }

    // ---- 4. Distribution HWP 5.x Decryption (HwpCrypto + HwpReader + FinderDocumentText) ----

    [Fact]
    public void DistributionHwp5_DecryptsViewTextInReaderAndFinder()
    {
        byte[] distHwp = BuildDistributionHwp("배포용 공문서 본문 \uA12B", "alpha + beta");
        HwpDoc? doc = HwpDocument.Read(distHwp);
        Assert.NotNull(doc);

        var paras = doc.Blocks.OfType<HwpPara>().ToList();
        Assert.NotEmpty(paras);
        string text = string.Concat(paras.SelectMany(p => p.Runs.OfType<HwpRun>()).Select(r => r.Text));
        Assert.Contains("배포용 공문서 본문 (인)", text);
        Assert.Contains(paras.SelectMany(p => p.Runs.OfType<HwpEquationRun>()), eq => eq.Script == "alpha + beta");

        using var dir = new TempDirectory();
        string path = dir.File("dist.hwp");
        File.WriteAllBytes(path, distHwp);
        string? extracted = FinderDocumentText.TryExtract(path, ".hwp");
        Assert.NotNull(extracted);
        Assert.Contains("배포용 공문서 본문 (인)", extracted);
        Assert.Contains("α + β", extracted);
    }

    // ---- 5. HWPX Enhancements (Borders, Numbering, Equations, Footnotes, Track Changes, Lineseg, Manifest, Broken ZIP) ----

    [Fact]
    public void Hwpx_ParsesPerSideBordersNumberingEquationsFootnotesCaptionsDeletesAndLinesegPages()
    {
        string header = $"""
            <hh:head {HwpxNs}><hh:refList>
              <hh:fontfaces><hh:fontface lang="HANGUL"><hh:font id="0" face="함초롬돋움"/></hh:fontface></hh:fontfaces>
              <hh:borderFills>
                <hh:borderFill id="1">
                  <hh:leftBorder type="NONE"/><hh:rightBorder type="NONE"/>
                  <hh:topBorder type="SOLID" width="0.4 mm" color="#000000"/>
                  <hh:bottomBorder type="DOUBLE_SLIM" width="0.5 mm" color="#0000FF"/>
                </hh:borderFill>
                <hh:borderFill id="2">
                  <hh:leftBorder type="SOLID" width="0.12 mm" color="#FFFFFF"/>
                  <hh:rightBorder type="NONE"/><hh:topBorder type="NONE"/><hh:bottomBorder type="NONE"/>
                </hh:borderFill>
              </hh:borderFills>
              <hh:charProperties>
                <hh:charPr id="0" height="1000" textColor="#000000" shadeColor="none"><hh:fontRef hangul="0"/></hh:charPr>
              </hh:charProperties>
              <hh:numberings>
                <hh:numbering id="1">
                  <hh:paraHead level="1" numFormat="HANGUL_SYLLABLE" start="1">^1.</hh:paraHead>
                </hh:numbering>
              </hh:numberings>
              <hh:bullets><hh:bullet id="1" char="&#xF06C;"/></hh:bullets>
              <hh:paraProperties>
                <hh:paraPr id="0"><hh:align horizontal="LEFT"/></hh:paraPr>
                <hh:paraPr id="1"><hh:align horizontal="LEFT"/><hh:heading type="NUMBER" idRef="1" level="0"/></hh:paraPr>
                <hh:paraPr id="2"><hh:align horizontal="LEFT"/><hh:heading type="BULLET" idRef="1" level="0"/></hh:paraPr>
              </hh:paraProperties>
            </hh:refList></hh:head>
            """;

        string section0 = $"""
            <hs:sec {HwpxNs}>
              <hp:p paraPrIDRef="1" pageBreak="0">
                <hp:run charPrIDRef="0"><hp:t>첫째 항목<hp:deleteBegin/>지운글<hp:deleteEnd/></hp:t></hp:run>
                <hp:linesegarray><hp:lineseg vertpos="0" horzpos="0"/></hp:linesegarray>
              </hp:p>
              <hp:p paraPrIDRef="2" pageBreak="0">
                <hp:run charPrIDRef="0">
                  <hp:t>둘째 </hp:t>
                  <hp:equation><hp:script>a over b</hp:script></hp:equation>
                  <hp:compose composeText="3" circleType="CIRCLE"/>
                  <hp:ctrl><hp:footNote><hp:subList><hp:p><hp:run><hp:t>각주 설명입니다</hp:t></hp:run></hp:p></hp:subList></hp:footNote></hp:ctrl>
                </hp:run>
                <hp:linesegarray><hp:lineseg vertpos="1200" horzpos="0"/></hp:linesegarray>
              </hp:p>
              <hp:p paraPrIDRef="0" pageBreak="0">
                <hp:run charPrIDRef="0">
                  <hp:tbl colCnt="2" borderFillIDRef="2">
                    <hp:caption side="TOP"><hp:subList><hp:p><hp:run><hp:t>표 1. 예산 현황</hp:t></hp:run></hp:p></hp:subList></hp:caption>
                    <hp:tr>
                      <hp:tc borderFillIDRef="1"><hp:cellAddr colAddr="0" rowAddr="0"/><hp:cellSpan colSpan="1" rowSpan="1"/><hp:cellSz width="10000" height="1000"/><hp:subList><hp:p><hp:run><hp:t>머리칸</hp:t></hp:run></hp:p></hp:subList></hp:tc>
                      <hp:tc borderFillIDRef="2"><hp:cellAddr colAddr="1" rowAddr="0"/><hp:cellSpan colSpan="1" rowSpan="1"/><hp:cellSz width="10000" height="1000"/><hp:subList><hp:p><hp:run><hp:t>무테두리칸</hp:t></hp:run></hp:p></hp:subList></hp:tc>
                    </hp:tr>
                  </hp:tbl>
                </hp:run>
                <hp:linesegarray><hp:lineseg vertpos="2400" horzpos="0"/></hp:linesegarray>
              </hp:p>
              <hp:p paraPrIDRef="0" pageBreak="0">
                <hp:run charPrIDRef="0"><hp:t>조판 캐시로 나뉜 둘째 쪽</hp:t></hp:run>
                <hp:linesegarray><hp:lineseg vertpos="0" horzpos="0"/></hp:linesegarray>
              </hp:p>
            </hs:sec>
            """;

        byte[] hwpx = MakeZip(
            ("mimetype", Utf8("application/hwp+zip")),
            ("Contents/header.xml", Utf8(header)),
            ("Contents/section0.xml", Utf8(section0)));

        HwpDoc doc = HwpDocument.Read(hwpx)!;
        Assert.NotNull(doc);

        var paras = doc.Blocks.OfType<HwpPara>().ToList();
        string p0 = string.Concat(paras[0].Runs.OfType<HwpRun>().Select(r => r.Text));
        Assert.Equal("가. 첫째 항목", p0);
        Assert.DoesNotContain("지운글", p0);

        string p1 = string.Concat(paras[1].Runs.OfType<HwpRun>().Select(r => r.Text));
        Assert.Contains("● 둘째", p1);
        Assert.Contains("③", p1);
        Assert.Contains("1)", p1);
        Assert.Contains(paras[1].Runs.OfType<HwpEquationRun>(), eq => eq.Script == "a over b");
        Assert.Single(doc.Footnotes);
        Assert.Contains("각주 설명입니다", doc.Footnotes[0]);

        // Per-side borders: cell 0 has top solid & bottom double blue, NO left/right; cell 1 has only white border -> none visible!
        HwpTable table = doc.Blocks.OfType<HwpTable>().Single();
        Assert.Equal("표 1. 예산 현황", table.Caption);
        HwpCell c0 = table.Rows[0][0];
        HwpCell c1 = table.Rows[0][1];
        Assert.NotNull(c0.Borders?.Top);
        Assert.NotNull(c0.Borders?.Bottom);
        Assert.Equal("double", c0.Borders!.Bottom!.Style);
        Assert.Null(c0.Borders.Left);
        Assert.False(c1.Borders!.HasAny);

        // lineseg vertpos reset (2400 -> 0) triggered an automatic page break!
        Assert.True(paras[^1].PageBreak);

        using var dir = new TempDirectory();
        HwpPreview preview = HwpHtml.Render(doc, dir.File("out"));
        Assert.Equal(2, preview.Sheets);
        string html = File.ReadAllText(preview.HtmlPath);
        Assert.Contains("class=\"tbl-cap\"", html);
        Assert.Contains("class=\"eq-frac\"", html);
        Assert.Contains("class=\"footnotes\"", html);
        Assert.Contains("border-bottom:2pt double #0000ff", html);
        Assert.Contains("'HCR Dotum'", html);
        Assert.Contains("sans-serif", html);
    }

    [Fact]
    public void Hwpx_RespectsManifestSpineOrder_AndRecoversBrokenCentralDirectory()
    {
        const string hpf = """
            <opf:package xmlns:opf="http://www.idpf.org/2007/opf/">
              <opf:manifest>
                <opf:item id="s0" href="Contents/section0.xml"/>
                <opf:item id="s1" href="Contents/section1.xml"/>
              </opf:manifest>
              <opf:spine><opf:itemref idref="s1"/><opf:itemref idref="s0"/></opf:spine>
            </opf:package>
            """;
        string s0 = $"<hs:sec {HwpxNs}><hp:p><hp:run><hp:t>뒤에 올 구역</hp:t></hp:run></hp:p></hs:sec>";
        string s1 = $"<hs:sec {HwpxNs}><hp:p><hp:run><hp:t>먼저 올 구역</hp:t></hp:run></hp:p></hs:sec>";

        byte[] hwpx = MakeZip(
            ("Contents/content.hpf", Utf8(hpf)),
            ("Contents/section0.xml", Utf8(s0)),
            ("Contents/section1.xml", Utf8(s1)));

        HwpDoc doc = HwpDocument.Read(hwpx)!;
        var texts = doc.Blocks.OfType<HwpPara>().Select(p => string.Concat(p.Runs.OfType<HwpRun>().Select(r => r.Text))).ToList();
        Assert.Equal(["먼저 올 구역", "뒤에 올 구역"], texts);

        // Corrupt the Central Directory (truncate the tail after local file entries) and verify Local File Header recovery!
        int cdIndex = FindBytes(hwpx, [0x50, 0x4B, 0x01, 0x02]);
        Assert.True(cdIndex > 30);
        byte[] broken = hwpx[..cdIndex];
        HwpDoc? recovered = HwpDocument.Read(broken);
        Assert.NotNull(recovered);
        string all = string.Concat(recovered.Blocks.OfType<HwpPara>().SelectMany(p => p.Runs.OfType<HwpRun>()).Select(r => r.Text));
        Assert.Contains("먼저 올 구역", all);
        Assert.Contains("뒤에 올 구역", all);
    }

    // ---- 6. HWPML 2.x (.hml) ----

    [Fact]
    public void Hwpml_ParsesFormattedTextTablesEquationsAndFootnotes()
    {
        const string hml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <HWPML Version="2.8">
              <HEAD>
                <MAPPINGTABLE>
                  <FACENAMELIST><FONTFACE Lang="Hangul"><FONT Id="0" Name="함초롬바탕"/></FONTFACE></FACENAMELIST>
                  <CHARSHAPELIST><CHARSHAPE Id="0" Height="1100" TextColor="255"><FONTID Hangul="0"/><BOLD/></CHARSHAPE></CHARSHAPELIST>
                  <PARASHAPELIST><PARASHAPE Id="0" Align="Center"/></PARASHAPELIST>
                  <BORDERFILLLIST><BORDERFILL Id="1"><TOPBORDER Type="Solid" Color="0"/><BOTTOMBORDER Type="Solid" Color="0"/></BORDERFILL></BORDERFILLLIST>
                </MAPPINGTABLE>
              </HEAD>
              <BODY>
                <SECTION>
                  <P ParaShape="0">
                    <TEXT CharShape="0">
                      <CHAR>한글 XML 문서</CHAR>
                      <EQUATION><SCRIPT>E = mc^2</SCRIPT></EQUATION>
                      <FOOTNOTE><PARALIST><P><TEXT><CHAR>HWPML 각주</CHAR></TEXT></P></PARALIST></FOOTNOTE>
                    </TEXT>
                  </P>
                  <P ParaShape="0">
                    <TEXT CharShape="0">
                      <TABLE ColCount="2" BorderFill="1">
                        <ROW>
                          <CELL ColAddr="0" RowAddr="0" ColSpan="1" RowSpan="1" Width="10000" Height="1000" BorderFill="1"><PARALIST><P><TEXT><CHAR>항목</CHAR></TEXT></P></PARALIST></CELL>
                          <CELL ColAddr="1" RowAddr="0" ColSpan="1" RowSpan="1" Width="10000" Height="1000" BorderFill="1"><PARALIST><P><TEXT><CHAR>값</CHAR></TEXT></P></PARALIST></CELL>
                        </ROW>
                      </TABLE>
                    </TEXT>
                  </P>
                </SECTION>
              </BODY>
            </HWPML>
            """;

        Assert.True(HwpDocument.IsHwpPath("doc.hml"));
        HwpDoc? doc = HwpDocument.Read(Utf8(hml));
        Assert.NotNull(doc);
        var p0 = (HwpPara)doc.Blocks[0];
        Assert.Equal("center", p0.Align);
        var r0 = (HwpRun)p0.Runs[0];
        Assert.Equal(("한글 XML 문서", true, "#ff0000", 11.0), (r0.Text, r0.Bold, r0.Color, r0.Size));
        Assert.Contains(p0.Runs.OfType<HwpEquationRun>(), eq => eq.Script == "E = mc^2");
        Assert.Single(doc.Footnotes);
        Assert.Single(doc.Blocks.OfType<HwpTable>());

        using var dir = new TempDirectory();
        string path = dir.File("sample.hml");
        File.WriteAllText(path, hml);
        string? extracted = FinderDocumentText.TryExtract(path, ".hml");
        Assert.NotNull(extracted);
        Assert.Contains("한글 XML 문서", extracted);
        Assert.Contains("E = mc²", extracted);
    }

    // ---- 7. Built-in Office Open XML Fallback (DOCX / XLSX / PPTX) + PreviewPane UI ----

    [Fact]
    public void OfficeDocument_ParsesDocxXlsxAndPptx()
    {
        // DOCX with sdt, w:ins, w:del, heading inheritance, vMerge table, footnote
        byte[] docx = MakeZip(
            ("word/styles.xml", Utf8("""
                <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:style w:styleId="Heading1"><w:name w:val="heading 1"/></w:style>
                  <w:style w:styleId="CustomTitle"><w:basedOn w:val="Heading1"/></w:style>
                </w:styles>
                """)),
            ("word/footnotes.xml", Utf8("""
                <w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:footnote w:id="1"><w:p><w:r><w:t>워드 각주</w:t></w:r></w:p></w:footnote>
                </w:footnotes>
                """)),
            ("word/document.xml", Utf8("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:sdt><w:sdtContent>
                      <w:p><w:pPr><w:pStyle w:val="CustomTitle"/></w:pPr><w:r><w:t>보고서 제목</w:t></w:r></w:p>
                    </w:sdtContent></w:sdt>
                    <w:p>
                      <w:r><w:t>본문 </w:t></w:r>
                      <w:ins><w:r><w:t>추가됨</w:t></w:r></w:ins>
                      <w:del><w:r><w:t>삭제됨</w:t></w:r></w:del>
                      <w:r><w:footnoteReference w:id="1"/></w:r>
                    </w:p>
                    <w:tbl>
                      <w:tr>
                        <w:tc><w:tcPr><w:vMerge w:val="restart"/></w:tcPr><w:p><w:r><w:t>세로병합</w:t></w:r></w:p></w:tc>
                        <w:tc><w:p><w:r><w:t>1행</w:t></w:r></w:p></w:tc>
                      </w:tr>
                      <w:tr>
                        <w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc>
                        <w:tc><w:p><w:r><w:t>2행</w:t></w:r></w:p></w:tc>
                      </w:tr>
                    </w:tbl>
                  </w:body>
                </w:document>
                """)));

        HwpDoc? docxModel = OfficeDocument.Read(docx);
        Assert.NotNull(docxModel);
        var docxParas = docxModel.Blocks.OfType<HwpPara>().ToList();
        Assert.True(((HwpRun)docxParas[0].Runs[0]).Bold);
        string bodyText = string.Concat(docxParas[1].Runs.OfType<HwpRun>().Select(r => r.Text));
        Assert.Contains("본문 추가됨", bodyText);
        Assert.DoesNotContain("삭제됨", bodyText);
        Assert.Single(docxModel.Footnotes);
        HwpTable docxTbl = docxModel.Blocks.OfType<HwpTable>().Single();
        Assert.Equal(2, docxTbl.Rows[0][0].RowSpan);

        // XLSX with Korean date built-in numFmtId=31, float precision cleanup, furigana rPh exclusion, merged cell
        byte[] xlsx = MakeZip(
            ("xl/workbook.xml", Utf8("""
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="예산표" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """)),
            ("xl/_rels/workbook.xml.rels", Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """)),
            ("xl/sharedStrings.xml", Utf8("""
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>고시일</t><rPh sb="0" eb="1"><t>후리가나</t></rPh></si>
                  <si><t>금액</t></si>
                </sst>
                """)),
            ("xl/styles.xml", Utf8("""
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <cellXfs count="2">
                    <xf numFmtId="0"/>
                    <xf numFmtId="31"/>
                  </cellXfs>
                </styleSheet>
                """)),
            ("xl/worksheets/sheet1.xml", Utf8("""
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                    <row r="2"><c r="A2" s="1"><v>42734</v></c><c r="B2"><v>132.30000000000001</v></c></row>
                  </sheetData>
                </worksheet>
                """)));

        HwpDoc? xlsxModel = OfficeDocument.Read(xlsx);
        Assert.NotNull(xlsxModel);
        HwpTable sheetTbl = xlsxModel.Blocks.OfType<HwpTable>().Single();
        Assert.Equal("고시일", CellText(sheetTbl.Rows[0][0]));
        Assert.DoesNotContain("후리가나", CellText(sheetTbl.Rows[0][0]));
        Assert.Equal("2016-12-30", CellText(sheetTbl.Rows[1][0]));
        Assert.Equal("132.3", CellText(sheetTbl.Rows[1][1]));

        // PPTX with sldIdLst ordering, hidden slide skipped, speaker notes
        byte[] pptx = MakeZip(
            ("ppt/presentation.xml", Utf8("""
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst><p:sldId id="256" r:id="rId1"/><p:sldId id="257" r:id="rId2"/></p:sldIdLst>
                </p:presentation>
                """)),
            ("ppt/_rels/presentation.xml.rels", Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="slide" Target="slides/slide1.xml"/>
                  <Relationship Id="rId2" Type="slide" Target="slides/slide2.xml"/>
                </Relationships>
                """)),
            ("ppt/slides/_rels/slide1.xml.rels", Utf8("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdN" Type="notesSlide" Target="../notesSlides/notesSlide1.xml"/>
                </Relationships>
                """)),
            ("ppt/slides/slide1.xml", Utf8("""
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld><p:spTree>
                    <p:sp>
                      <p:nvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                      <p:txBody><a:p><a:r><a:t>발표 제목</a:t></a:r></a:p></p:txBody>
                    </p:sp>
                    <p:sp>
                      <p:nvSpPr><p:nvPr><p:ph type="sldNum"/></p:nvPr></p:nvSpPr>
                      <p:txBody><a:p><a:r><a:t>99</a:t></a:r></a:p></p:txBody>
                    </p:sp>
                  </p:spTree></p:cSld>
                </p:sld>
                """)),
            ("ppt/notesSlides/notesSlide1.xml", Utf8("""
                <p:notes xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld><p:spTree>
                    <p:sp><p:nvSpPr><p:nvPr><p:ph type="body"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>핵심 발표 메모</a:t></a:r></a:p></p:txBody></p:sp>
                  </p:spTree></p:cSld>
                </p:notes>
                """)),
            ("ppt/slides/slide2.xml", Utf8("""
                <p:sld show="0" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>숨긴 슬라이드</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld>
                </p:sld>
                """)));

        HwpDoc? pptxModel = OfficeDocument.Read(pptx);
        Assert.NotNull(pptxModel);
        string pptxText = string.Concat(pptxModel.Blocks.OfType<HwpPara>().SelectMany(p => p.Runs.OfType<HwpRun>()).Select(r => r.Text));
        Assert.Contains("발표 제목", pptxText);
        Assert.Contains("핵심 발표 메모", pptxText);
        Assert.DoesNotContain("숨긴 슬라이드", pptxText);
        Assert.DoesNotContain("99", pptxText);

        static string CellText(HwpCell cell) =>
            string.Concat(cell.Blocks.OfType<HwpPara>().SelectMany(p => p.Runs.OfType<HwpRun>()).Select(r => r.Text));
    }

    [Fact]
    public void PreviewPane_ShowsBuiltInOfficeFallbackWithDocumentToolbarAndZoom()
    {
        using var dir = new TempDirectory();
        string oldCache = OfficeDocument.CacheFolder;
        OfficeDocument.CacheFolder = dir.File("office-cache");
        try
        {
            byte[] docx = MakeZip(
                ("word/document.xml", Utf8("""
                    <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                      <w:body>
                        <w:p><w:r><w:t>첫째 페이지 내용</w:t></w:r></w:p>
                        <w:p><w:pPr><w:pageBreakBefore/></w:pPr><w:r><w:t>둘째 페이지 내용</w:t></w:r></w:p>
                      </w:body>
                    </w:document>
                    """)));
            string path = dir.File("plan.docx");
            File.WriteAllBytes(path, docx);

            PreviewPane? pane = null;
            Run(() =>
            {
                var form = new Form { Width = 540, Height = 640, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir office preview" };
                pane = new PreviewPane { Dock = DockStyle.Fill, UseShellHandlerForOffice = false };
                form.Controls.Add(pane);
                return form;
            }, async _ =>
            {
                await pane!.ShowAsync(ShellLocation.Parse(path)!, 1);

                Assert.Equal(PreviewKind.Document, pane.Kind);
                Assert.Contains("내장 문서 뷰어", pane.Detail);
                var view = Assert.IsType<HwpView>(pane.View);
                await WaitUntil(() => view.Loaded, "office HTML preview loaded");
                Assert.Equal(2, view.SheetCount);
                Assert.Contains("첫째 페이지 내용", view.Document!.Body!.InnerText);
                Assert.Contains("둘째 페이지 내용", view.Document!.Body!.InnerText);

                // Document toolbar is visible for HwpView and supports zoom + sheet stepping.
                view.ActualSize();
                Assert.Equal(100, view.ZoomPercent);
                view.ZoomBy(1.25);
                Assert.Equal(125, view.ZoomPercent);
                view.StepSheet(1);
                Assert.Equal(1, view.CurrentSheet);
            });
        }
        finally
        {
            OfficeDocument.CacheFolder = oldCache;
        }
    }

    // ---- Helpers for building a genuine distribution HWP 5.x binary ----

    private static byte[] BuildDistributionHwp(string paragraphText, string equationScript)
    {
        byte[] fileHeader = new byte[256];
        Encoding.ASCII.GetBytes("HWP Document File").CopyTo(fileHeader, 0);
        BitConverter.GetBytes(0x01u | 0x04u).CopyTo(fileHeader, 36); // compressed + distribution

        byte[] docInfo = Deflate(BuildRecord(17, 0, new byte[68]));

        // Section records: PARA_HEADER + PARA_TEXT + CTRL_HEADER("eqed") + EQEDIT
        var textBytes = new List<byte>();
        textBytes.AddRange(Encoding.Unicode.GetBytes(paragraphText));
        textBytes.AddRange(BitConverter.GetBytes((ushort)11));
        textBytes.AddRange(new byte[14]);
        textBytes.AddRange(BitConverter.GetBytes((ushort)13));

        var eqPayload = new List<byte>();
        eqPayload.AddRange(BitConverter.GetBytes(0u));
        eqPayload.AddRange(BitConverter.GetBytes((ushort)equationScript.Length));
        eqPayload.AddRange(Encoding.Unicode.GetBytes(equationScript));

        byte[] sectionPlain =
        [
            .. BuildRecord(66, 0, new byte[22]),
            .. BuildRecord(67, 1, [.. textBytes]),
            .. BuildRecord(71, 1, Encoding.ASCII.GetBytes("deqe")),
            .. BuildRecord(88, 2, [.. eqPayload]),
        ];

        byte[] compressedSection = Deflate(sectionPlain);
        // Pad to 16-byte AES block boundary
        int paddedLen = ((compressedSection.Length + 15) / 16) * 16;
        byte[] plainAligned = new byte[paddedLen];
        compressedSection.CopyTo(plainAligned, 0);

        // Construct 256-byte DISTRIBUTE_DOC_DATA payload with known seed & AES key
        byte[] decryptedPayload = new byte[256];
        BitConverter.GetBytes(0x12345678u).CopyTo(decryptedPayload, 0);
        byte[] aesKey = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        int keyOffset = 4 + (decryptedPayload[0] & 0x0F);
        aesKey.CopyTo(decryptedPayload, keyOffset);

        // XOR is symmetric, so calling DecryptDistributePayload encrypts it!
        byte[] encryptedPayload = HwpCrypto.DecryptDistributePayload(decryptedPayload);
        byte[] cipherBody = HwpCrypto.EncryptAes128Ecb(plainAligned, aesKey);
        byte[] viewSection = [.. BuildRecord(HwpCrypto.TagDistributeDocData, 0, encryptedPayload), .. cipherBody];

        return BuildCompoundFile(fileHeader, docInfo, "ViewText", viewSection);
    }

    private static byte[] BuildRecord(int tag, int level, byte[] payload)
    {
        var bytes = new List<byte>();
        uint head = (uint)tag | ((uint)level << 10) | ((uint)Math.Min(payload.Length, 0xFFF) << 20);
        bytes.AddRange(BitConverter.GetBytes(head));
        if (payload.Length >= 0xFFF)
            bytes.AddRange(BitConverter.GetBytes((uint)payload.Length));
        bytes.AddRange(payload);
        return [.. bytes];
    }

    private static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            d.Write(data);
        return ms.ToArray();
    }

    private static byte[] BuildCompoundFile(byte[] fileHeader, byte[] docInfo, string storageName, byte[] section)
    {
        const uint noStream = 0xFFFFFFFF;
        const uint endOfChain = 0xFFFFFFFE;
        byte[][] streams = [Pad(fileHeader), Pad(docInfo), Pad(section)];
        var fat = new uint[128];
        Array.Fill(fat, noStream);
        fat[0] = 0xFFFFFFFD;
        fat[1] = endOfChain;
        uint next = 2;
        var starts = new uint[streams.Length];
        for (int s = 0; s < streams.Length; s++)
        {
            starts[s] = next;
            int sectors = streams[s].Length / 512;
            for (int i = 0; i < sectors; i++, next++)
                fat[next] = i == sectors - 1 ? endOfChain : next + 1;
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
        BitConverter.GetBytes(endOfChain).CopyTo(head, 60);
        BitConverter.GetBytes(endOfChain).CopyTo(head, 68);
        for (int i = 0; i < 109; i++)
            BitConverter.GetBytes(i == 0 ? 0u : noStream).CopyTo(head, 76 + (i * 4));

        writer.Write(head);
        foreach (uint entry in fat)
            writer.Write(entry);

        writer.Write(Entry("Root Entry", 5, noStream, noStream, 2, endOfChain, 0));
        writer.Write(Entry(storageName, 1, noStream, noStream, 4, 0, 0));
        writer.Write(Entry("FileHeader", 2, 3, 1, noStream, starts[0], fileHeader.Length));
        writer.Write(Entry("DocInfo", 2, noStream, noStream, noStream, starts[1], docInfo.Length));
        writer.Write(new byte[512 - (4 * 128)]);

        uint secondDirectory = next;
        fat[1] = secondDirectory;
        fat[secondDirectory] = endOfChain;
        foreach (byte[] stream in streams)
            writer.Write(stream);

        writer.Write(Entry("Section0", 2, noStream, noStream, noStream, starts[2], section.Length));
        writer.Write(new byte[512 - 128]);

        byte[] result = file.ToArray();
        for (int i = 0; i < fat.Length; i++)
            BitConverter.GetBytes(fat[i]).CopyTo(result, 512 + (i * 4));
        return result;

        static byte[] Pad(byte[] data)
        {
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

    private static int FindBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }
}
