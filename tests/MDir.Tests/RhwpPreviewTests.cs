using System.IO.Compression;
using System.Text;
using MDir.Features;
using MDir.Features.Hwp;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Page-accurate HWP/HWPX preview powered by the minimal <c>rhwp-preview.exe</c> helper.</summary>
public class RhwpPreviewTests
{
    private static byte[] ValidTwoPageHwpx()
    {
        const string container = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <ocf:container xmlns:ocf="urn:oasis:names:tc:opendocument:xmlns:container">
              <ocf:rootfiles>
                <ocf:rootfile full-path="Contents/content.hpf" media-type="application/hwpml-package+xml"/>
              </ocf:rootfiles>
            </ocf:container>
            """;

        const string hpf = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <opf:package xmlns:opf="http://www.idpf.org/2007/opf/" version="" unique-identifier="" id="">
              <opf:metadata><opf:title>fixture</opf:title><opf:language>ko</opf:language></opf:metadata>
              <opf:manifest>
                <opf:item id="header" href="Contents/header.xml" media-type="application/xml"/>
                <opf:item id="section0" href="Contents/section0.xml" media-type="application/xml"/>
              </opf:manifest>
              <opf:spine><opf:itemref idref="header"/><opf:itemref idref="section0"/></opf:spine>
            </opf:package>
            """;

        const string header = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <hh:head xmlns:hh="http://www.hancom.co.kr/hwpml/2011/head" version="1.4" secCnt="1">
              <hh:refList>
                <hh:fontfaces itemCnt="1">
                  <hh:fontface lang="HANGUL" fontCnt="1">
                    <hh:font id="0" face="맑은 고딕" type="TTF" isEmbedded="0"/>
                  </hh:fontface>
                </hh:fontfaces>
                <hh:charProperties itemCnt="1">
                  <hh:charPr id="0" height="1200" textColor="#000000" shadeColor="none">
                    <hh:fontRef hangul="0"/>
                  </hh:charPr>
                </hh:charProperties>
                <hh:paraProperties itemCnt="1">
                  <hh:paraPr id="0"><hh:align horizontal="LEFT"/></hh:paraPr>
                </hh:paraProperties>
              </hh:refList>
            </hh:head>
            """;

        const string section = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <hs:sec xmlns:hs="http://www.hancom.co.kr/hwpml/2011/section" xmlns:hp="http://www.hancom.co.kr/hwpml/2011/paragraph">
              <hp:p paraPrIDRef="0" pageBreak="0">
                <hp:run charPrIDRef="0">
                  <hp:secPr><hp:pagePr width="59528" height="84188"><hp:margin left="8504" right="8504" top="5669" bottom="4252" header="4252" footer="4252"/></hp:pagePr></hp:secPr>
                  <hp:t>사업 계획서 1페이지</hp:t>
                </hp:run>
              </hp:p>
              <hp:p paraPrIDRef="0" pageBreak="1">
                <hp:run charPrIDRef="0"><hp:t>둘째 페이지 본문</hp:t></hp:run>
              </hp:p>
            </hs:sec>
            """;

        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "mimetype", "application/hwp+zip", CompressionLevel.NoCompression);
            WriteEntry(zip, "META-INF/container.xml", container, CompressionLevel.Optimal);
            WriteEntry(zip, "Contents/content.hpf", hpf, CompressionLevel.Optimal);
            WriteEntry(zip, "Contents/header.xml", header, CompressionLevel.Optimal);
            WriteEntry(zip, "Contents/section0.xml", section, CompressionLevel.Optimal);
        }

        return buffer.ToArray();

        static void WriteEntry(ZipArchive zip, string name, string text, CompressionLevel level)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, level);
            using Stream s = entry.Open();
            s.Write(new UTF8Encoding(false).GetBytes(text));
        }
    }

    private static bool IsPng(string file)
    {
        if (!File.Exists(file))
            return false;

        byte[] head = new byte[8];
        using FileStream fs = File.OpenRead(file);
        return fs.Read(head, 0, 8) == 8 && head.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    }

    [Fact]
    public void RhwpDocument_RendersHwpxPagesLazily_AndReusesCache()
    {
        string? exe = RhwpLocator.FindExe();
        if (exe is null)
            return;

        using var dir = new TempDirectory();
        string cache = dir.File("cache");
        string path = dir.File("plan.hwpx");
        File.WriteAllBytes(path, ValidTwoPageHwpx());

        using var job = new RhwpJob();
        using RhwpDocument doc = RhwpDocument.Open(path, job, cache)!;
        Assert.NotNull(doc);
        Assert.Equal(2, doc.PageCount);
        Assert.True(doc.HasWorker);
        Assert.True(doc.HasPage(0));
        Assert.False(doc.HasPage(1));
        Assert.True(IsPng(doc.PageFile(0)));

        Assert.True(doc.RenderPage(1, job));
        Assert.True(doc.HasPage(1));
        Assert.True(IsPng(doc.PageFile(1)));

        doc.Dispose();

        // Re-opening an unchanged file hits the disk cache without spawning a worker.
        using var job2 = new RhwpJob();
        using RhwpDocument cached = RhwpDocument.Open(path, job2, cache)!;
        Assert.NotNull(cached);
        Assert.Equal(2, cached.PageCount);
        Assert.False(cached.HasWorker);
        Assert.True(cached.HasPage(0));
        Assert.True(cached.HasPage(1));
    }

    [Fact]
    public void RhwpJob_CancelStopsOpenQuickly()
    {
        using var job = new RhwpJob();
        job.Cancel();
        Assert.True(job.IsCancelled);
        Assert.Null(RhwpDocument.Open("nonexistent.hwpx", job));
    }

    [Fact]
    public void PreviewPane_ShowsHwpxWithRhwpView_AndFallsBackWhenDisabled()
    {
        if (RhwpLocator.FindExe() is null)
            return;

        using var dir = new TempDirectory();
        string cache = dir.File("cache");
        string path = dir.File("보고서.hwpx");
        File.WriteAllBytes(path, ValidTwoPageHwpx());

        PreviewPane? pane = null;
        Run(() =>
        {
            var form = new Form { Width = 540, Height = 640, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir rhwp preview" };
            pane = new PreviewPane { Dock = DockStyle.Fill, RhwpCacheFolder = cache };
            form.Controls.Add(pane);
            return form;
        }, async _ =>
        {
            await pane!.ShowAsync(ShellLocation.Parse(path)!, 1);

            Assert.Equal(PreviewKind.Document, pane.Kind);
            Assert.Contains("2쪽", pane.Detail);
            Assert.Contains("내장 한글 뷰어", pane.Detail);
            var view = Assert.IsType<RhwpView>(pane.View);
            Assert.Equal(2, view.PageCount);
            Assert.InRange(view.ZoomPercent, 20, 200);

            // Background worker renders page 1 lazily.
            await WaitUntil(() => File.Exists(Path.Combine(cache, Directory.GetDirectories(cache).Select(Path.GetFileName).Single()!, "p1.png")), "page 1 rendered in background");

            // When disabled, PreviewPane falls back to the built-in HTML HwpView.
            pane.UseRhwp = false;
            await pane.ShowAsync(ShellLocation.Parse(path)!, 1);
            Assert.Equal(PreviewKind.Document, pane.Kind);
            var fallback = Assert.IsType<HwpView>(pane.View);
            await WaitUntil(() => fallback.Loaded, "fallback HwpView loaded");
        });
    }
}
