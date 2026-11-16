using System.IO.Compression;
using System.Security.Principal;
using System.Text;
using MDir.Finder;
using MDir.Finder.Engine;
using MDir.Settings;

namespace MDir.Tests;

/// <summary>The built-in M-Finder: index, search syntax, scanning, watching, content search, NTFS reading and the elevated helper's protocol.</summary>
public class FinderEngineTests
{
    // ---- helpers ----

    private static FinderIndex SampleIndex()
    {
        var idx = new FinderIndex();
        (int vol, int root) = idx.AddRoot(@"C:\", "folder");
        int docs = idx.Add("Docs", root, FinderIndex.DirectoryAttribute, vol: vol);
        int sub = idx.Add("Reports", docs, FinderIndex.DirectoryAttribute, vol: vol);
        idx.Add("Budget 2024.xlsx", sub, 0x20, 2048, 1_700_000_000, vol: vol);
        idx.Add("budget-notes.txt", docs, 0x20, 10, 1_600_000_000, vol: vol);
        idx.Add("Photo.JPG", root, 0x20, 5_000_000, 1_650_000_000, vol: vol);
        idx.Add("setup.exe", root, 0x2 | 0x20, 900, 1_500_000_000, vol: vol);
        idx.Add("empty", root, FinderIndex.DirectoryAttribute, vol: vol);
        return idx;
    }

    private static string[] Names(FinderIndex idx, string query, FinderFlags flags = default, string filter = "")
    {
        List<int> ids = FinderSearch.Search(idx, query, flags, default, filter)!;
        return [.. ids.Select(i => idx.Names[i])];
    }

    // ---- search syntax ----

    [Fact]
    public void Search_PlainTermsAreCaseInsensitiveSubstringsOfTheName_AndSpaceMeansAnd()
    {
        FinderIndex idx = SampleIndex();
        Assert.Equal(["Budget 2024.xlsx", "budget-notes.txt"], Names(idx, "budget"));
        Assert.Equal(["Budget 2024.xlsx"], Names(idx, "budget 2024"));
        Assert.Equal(["Photo.JPG"], Names(idx, "photo"));
        Assert.Empty(Names(idx, "nothing"));
    }

    [Fact]
    public void Search_OrNotGroupingAndQuotes()
    {
        FinderIndex idx = SampleIndex();
        Assert.Equal(["Photo.JPG", "setup.exe"], Names(idx, "photo|setup"));
        Assert.Equal(["budget-notes.txt"], Names(idx, "budget !xlsx"));
        Assert.Equal(["Budget 2024.xlsx", "Photo.JPG"], Names(idx, "<xlsx|jpg> !notes"));
        Assert.Equal(["Budget 2024.xlsx"], Names(idx, "\"budget 2024\""));
    }

    [Fact]
    public void Search_WildcardsMatchTheWholeName()
    {
        FinderIndex idx = SampleIndex();
        Assert.Equal(["budget-notes.txt"], Names(idx, "*.txt"));
        Assert.Equal(["setup.exe"], Names(idx, "se?up.*"));
        Assert.Empty(Names(idx, "bud*z"));
    }

    [Fact]
    public void Search_FunctionsExtFolderFileSizeDepthParentAttrib()
    {
        FinderIndex idx = SampleIndex();
        Assert.Equal(["Budget 2024.xlsx", "budget-notes.txt"], Names(idx, "ext:xlsx;txt"));
        Assert.Equal(["Docs", "Reports", "empty"], Names(idx, "folder:"));
        Assert.Equal(["Budget 2024.xlsx", "budget-notes.txt", "Photo.JPG", "setup.exe"], Names(idx, "file:"));
        Assert.Equal(["Photo.JPG"], Names(idx, "size:>1mb"));
        Assert.Equal(["Budget 2024.xlsx"], Names(idx, "size:1kb..4kb"));
        Assert.Equal(["Docs", "Photo.JPG", "setup.exe", "empty"], Names(idx, "depth:1"));
        Assert.Equal(["Budget 2024.xlsx"], Names(idx, @"parent:C:\Docs\Reports"));
        Assert.Equal(["setup.exe"], Names(idx, "attrib:H"));
        Assert.Equal(["empty"], Names(idx, "empty:"));
    }

    [Fact]
    public void Search_MacrosAndFilters()
    {
        FinderIndex idx = SampleIndex();
        Assert.Equal(["Photo.JPG"], Names(idx, "pic:"));
        Assert.Equal(["Budget 2024.xlsx", "budget-notes.txt"], Names(idx, "doc:"));
        Assert.Equal(["Photo.JPG"], Names(idx, "", filter: "pic:"));
        Assert.Equal(["Budget 2024.xlsx", "budget-notes.txt"], Names(idx, "budget", filter: "doc:"));
    }

    [Fact]
    public void Search_DatesAndModifiers()
    {
        FinderIndex idx = SampleIndex();
        Assert.Equal(["Budget 2024.xlsx"], Names(idx, "dm:2023-11"));
        Assert.Equal(["budget-notes.txt", "Photo.JPG", "setup.exe"], Names(idx, "file: dm:<2023"));
        Assert.Equal(["Photo.JPG"], Names(idx, "case:Photo"));
        Assert.Empty(Names(idx, "case:photo"));
        Assert.Equal(["setup.exe"], Names(idx, "ww:setup"));
        Assert.Empty(Names(idx, "ww:set"));
        Assert.Equal(["Photo.JPG"], Names(idx, @"regex:^p.*\.jpg$"));
        Assert.Equal(["budget-notes.txt"], Names(idx, @"path:docs\budget-"));
        Assert.Equal(["Photo.JPG"], Names(idx, "photo", new FinderFlags(Case: false)));
        Assert.Empty(Names(idx, "photo", new FinderFlags(Case: true)));
    }

    [Fact]
    public void Search_BadInputIsReported()
    {
        FinderIndex idx = SampleIndex();
        Assert.Throws<FinderQueryException>(() => Names(idx, "size:abc"));
        Assert.Throws<FinderQueryException>(() => Names(idx, "dm:someday"));
        Assert.Throws<FinderQueryException>(() => Names(idx, "regex:("));
        Assert.Throws<FinderQueryException>(() => Names(idx, "attrib:Q"));
        Assert.Throws<FinderQueryException>(() => Names(idx, "len:"));
    }

    [Fact]
    public void Numeric_AndDateIntervals()
    {
        Assert.Equal((1024d, 1024d), FinderQueryParser.Numeric("1kb", s => (1024, 1024)));
        (double lo, double hi) = FinderQueryParser.Numeric(">5", s => (5, 5));
        Assert.True(lo > 5 && double.IsPositiveInfinity(hi));
        (double a, double b) = FinderQueryParser.DateAtom("2024-02", new DateTime(2025, 1, 1));
        Assert.Equal(new DateTimeOffset(new DateTime(2024, 2, 1)).ToUnixTimeSeconds(), a);
        Assert.True(b < new DateTimeOffset(new DateTime(2024, 3, 1)).ToUnixTimeSeconds());
        (double w0, double w1) = FinderQueryParser.DateAtom("thisweek", new DateTime(2025, 1, 8)); // a Wednesday
        Assert.Equal(new DateTimeOffset(new DateTime(2025, 1, 6)).ToUnixTimeSeconds(), w0);
        Assert.True(w1 < new DateTimeOffset(new DateTime(2025, 1, 13)).ToUnixTimeSeconds());
    }

    [Fact]
    public void Sort_ByNameKeepsFoldersFirst_AndBySizeAndPath()
    {
        FinderIndex idx = SampleIndex();
        List<int> all = FinderSearch.Search(idx, "", default)!;
        string[] byName = [.. FinderSearch.Sort(idx, all, "name")!.Select(i => idx.Names[i])];
        Assert.Equal(["Docs", "empty", "Reports", "Budget 2024.xlsx", "budget-notes.txt", "Photo.JPG", "setup.exe"], byName);
        string[] byNameDesc = [.. FinderSearch.Sort(idx, all, "name", ascending: false)!.Select(i => idx.Names[i])];
        Assert.Equal(["Reports", "empty", "Docs", "setup.exe", "Photo.JPG", "budget-notes.txt", "Budget 2024.xlsx"], byNameDesc);
        Assert.Equal("Photo.JPG", idx.Names[FinderSearch.Sort(idx, all, "size", ascending: false)![0]]);
        Assert.Equal("Budget 2024.xlsx", idx.Names[FinderSearch.Sort(idx, all, "path")!.First(i => !idx.IsDir(i) && idx.Parent[i] == idx.ChildOf(idx.ChildOf(0, "Docs")!.Value, "Reports"))]);
        Assert.Throws<FinderQueryException>(() => FinderSearch.Sort(idx, all, "bogus"));
    }

    [Fact]
    public void Search_CancelledReturnsNull()
    {
        FinderIndex idx = SampleIndex();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(FinderSearch.Search(idx, "budget", default, cts.Token));
    }

    // ---- index ----

    [Fact]
    public void Index_SavesAndLoadsAndDropsRemovedEntries()
    {
        using var dir = new TempDirectory();
        FinderIndex idx = SampleIndex();
        idx.Signature = "sig";
        idx.Remove(idx.ChildOf(0, "setup.exe")!.Value);
        string file = dir.File("a.mfi");
        idx.Save(file);

        FinderIndex loaded = FinderIndex.Load(file);
        Assert.Equal("sig", loaded.Signature);
        Assert.Equal(idx.LiveCount(), loaded.Count);
        Assert.Equal(["Budget 2024.xlsx", "budget-notes.txt"], Names(loaded, "budget"));
        Assert.Equal(@"C:\Docs\Reports\Budget 2024.xlsx", loaded.FullPath(loaded.Names.IndexOf("Budget 2024.xlsx")));
        Assert.Empty(Names(loaded, "setup"));
        Assert.Equal(1_700_000_000, loaded.MTime[loaded.Names.IndexOf("Budget 2024.xlsx")]);

        File.WriteAllText(file, "garbage");
        Assert.Throws<FinderDatabaseException>(() => FinderIndex.Load(file));
        Assert.Throws<FinderDatabaseException>(() => FinderIndex.Load(dir.File("missing")));
    }

    [Fact]
    public void Index_FolderSizesAreSummedUp()
    {
        FinderIndex idx = SampleIndex();
        Assert.True(idx.ComputeFolderSizes());
        int docs = idx.ChildOf(0, "Docs")!.Value;
        Assert.Equal(2048 + 10, idx.Size[docs]);
        Assert.Equal(["Docs", "Reports"], Names(idx, "folder: size:>1kb"));
    }

    [Fact]
    public void Index_ResolveAndRemoveTree()
    {
        FinderIndex idx = SampleIndex();
        Assert.NotNull(idx.Resolve(@"C:\Docs\Reports\budget 2024.XLSX"));
        Assert.Null(idx.Resolve(@"C:\Docs\Nope"));
        Assert.Null(idx.Resolve(@"D:\Docs"));
        int removed = idx.RemoveTree(idx.Resolve(@"C:\Docs")!.Value);
        Assert.Equal(4, removed);
        Assert.Empty(Names(idx, "budget"));
    }

    // ---- scanning and watching ----

    private static TempDirectory SampleTree()
    {
        var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File(@"sub\deep"));
        File.WriteAllText(dir.File("alpha.txt"), "hello world");
        File.WriteAllText(dir.File(@"sub\beta.log"), "second");
        File.WriteAllText(dir.File(@"sub\deep\gamma.txt"), "third");
        File.WriteAllText(dir.File("skip.tmp"), "x");
        return dir;
    }

    [Fact]
    public void Scanner_IndexesATreeWithSizesAndDates_AndHonoursExcludes()
    {
        using TempDirectory dir = SampleTree();
        var idx = new FinderIndex();
        FinderScanner.ScanFolder(idx, dir.Path, new FinderExcludes(patterns: ["*.tmp"], folders: [dir.File("sub")]), null, default);
        Assert.Equal(["alpha.txt"], Names(idx, "file:"));

        var all = new FinderIndex();
        FinderScanner.ScanFolder(all, dir.Path, null, null, default);
        Assert.Equal(["alpha.txt", "beta.log", "gamma.txt", "skip.tmp"], Names(all, "file:").Order().ToArray());
        int alpha = all.Names.IndexOf("alpha.txt");
        Assert.Equal(11, all.Size[alpha]);
        Assert.True(all.MTime[alpha] > 1_500_000_000);
        Assert.Equal(dir.File("alpha.txt"), all.FullPath(alpha));
        Assert.Equal(["gamma.txt"], Names(all, @"parent:" + dir.File(@"sub\deep")));
    }

    [Fact]
    public void ChangeApplier_FollowsCreateRenameModifyAndDelete()
    {
        using TempDirectory dir = SampleTree();
        var idx = new FinderIndex();
        int vol = FinderScanner.ScanFolder(idx, dir.Path, null, null, default);
        var applier = new FinderChangeApplier(idx, vol);

        File.WriteAllText(dir.File("new.txt"), "n");
        Assert.True(applier.Apply(FinderChangeApplier.Change.Added, "new.txt"));
        Assert.Equal(["new.txt"], Names(idx, "new"));

        File.Move(dir.File("new.txt"), dir.File("renamed.txt"));
        Assert.True(applier.Apply(FinderChangeApplier.Change.RenamedOld, "new.txt"));
        Assert.True(applier.Apply(FinderChangeApplier.Change.RenamedNew, "renamed.txt"));
        Assert.Empty(Names(idx, "new.txt"));
        Assert.Equal(["renamed.txt"], Names(idx, "renamed"));

        long rev = idx.Rev;
        Assert.True(applier.Apply(FinderChangeApplier.Change.Modified, "renamed.txt"));
        Assert.True(idx.Rev > rev);

        Directory.CreateDirectory(dir.File(@"moved\inner"));
        File.WriteAllText(dir.File(@"moved\inner\file.dat"), "d");
        Assert.True(applier.Apply(FinderChangeApplier.Change.Added, "moved"));
        Assert.Equal(["file.dat"], Names(idx, "file.dat"));

        Assert.True(applier.Apply(FinderChangeApplier.Change.Removed, @"sub"));
        Assert.Empty(Names(idx, "gamma"));
        Assert.False(applier.Apply(FinderChangeApplier.Change.Removed, "never-existed"));
    }

    [Fact]
    public async Task FolderWatcher_PicksUpANewFile()
    {
        using TempDirectory dir = SampleTree();
        var idx = new FinderIndex();
        int vol = FinderScanner.ScanFolder(idx, dir.Path, null, null, default);
        using var watcher = new FinderFolderWatcher(idx, vol, null, _ => { });
        watcher.Start();
        File.WriteAllText(dir.File("watched.txt"), "w");
        for (int i = 0; i < 50 && Names(idx, "watched").Length == 0; i++)
            await Task.Delay(100);
        Assert.Equal(["watched.txt"], Names(idx, "watched"));
    }

    // ---- content search ----

    [Fact]
    public void Content_SearchesTextUtf16AndTheAnsiCodePage_WithAsciiCaseFolding()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("plain.txt"), "The Quick Brown Fox");
        File.WriteAllText(dir.File("wide.txt"), "한글 문서 안의 낱말", Encoding.Unicode);
        File.WriteAllText(dir.File("other.txt"), "nothing here");
        var idx = new FinderIndex();
        FinderScanner.ScanFolder(idx, dir.Path, null, null, default);

        Assert.Equal(["plain.txt"], Names(idx, "content:quick"));
        Assert.Empty(Names(idx, "case: content:quick"));
        Assert.Equal(["plain.txt"], Names(idx, "case: content:Quick"));
        Assert.Equal(["wide.txt"], Names(idx, "content:낱말"));
        Assert.Equal(["plain.txt"], Names(idx, @"regex: content:q\w+k"));
        Assert.Equal(["plain.txt"], Names(idx, "content:fox ext:txt !wide"));
    }

    [Fact]
    public void Content_SearchesTextInsideOfficeAndOpenDocumentFiles()
    {
        using var dir = new TempDirectory();
        Zip(dir.File("a.docx"), ("word/document.xml", "<w:document><w:body><w:p><w:r><w:t>안녕 세계 &amp; docx</w:t></w:r></w:p></w:body></w:document>"));
        Zip(dir.File("b.xlsx"), ("xl/sharedStrings.xml", "<sst><si><t>수입 합계</t></si></sst>"), ("xl/worksheets/sheet1.xml", "<worksheet/>"));
        Zip(dir.File("c.pptx"), ("ppt/slides/slide1.xml", "<p:sld><a:p><a:r><a:t>발표 제목</a:t></a:r></a:p></p:sld>"));
        Zip(dir.File("d.odt"), ("content.xml", "<office:document-content><text:p>본문 문단</text:p></office:document-content>"));
        Zip(dir.File("e.hwpx"), ("Contents/section0.xml", "<hs:sec><hp:p><hp:run><hp:t>한글문서 첫째</hp:t></hp:run></hp:p></hs:sec>"), ("Contents/section1.xml", "<hs:sec><hp:p><hp:run><hp:t>둘째 구역</hp:t></hp:run></hp:p></hs:sec>"));
        var idx = new FinderIndex();
        FinderScanner.ScanFolder(idx, dir.Path, null, null, default);

        Assert.Equal(["a.docx"], Names(idx, "content:세계"));
        Assert.Equal(["a.docx"], Names(idx, "content:& docx"));
        Assert.Equal(["b.xlsx"], Names(idx, "content:합계"));
        Assert.Equal(["c.pptx"], Names(idx, "content:제목"));
        Assert.Equal(["d.odt"], Names(idx, "content:문단"));
        Assert.Equal(["e.hwpx"], Names(idx, "content:첫째"));
        Assert.Equal(["e.hwpx"], Names(idx, "content:구역"));
    }

    private static void Zip(string path, params (string Name, string Text)[] entries)
    {
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    [Fact]
    public void Content_ReadsTextOfSimpleAndCompositeFontPdfs()
    {
        // A plain font with literal strings, and a Type0 font whose ToUnicode map gives the Korean text.
        string content1 = "BT /F1 12 Tf 72 700 Td (Hello \\(PDF\\) World) Tj ET";
        string content2 = "BT /F2 12 Tf 72 650 Td <00010002> Tj [<0003> -400 <0001>] TJ ET";
        string cmap = "/CIDInit begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 2 beginbfchar <0001> <D55C> <0002> <AE00> endbfchar 1 beginbfrange <0003> <0003> <C124> endbfrange endcmap";
        var sb = new StringBuilder("%PDF-1.4\n");
        sb.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        sb.Append("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >> endobj\n");
        sb.Append("3 0 obj << /Type /Page /Parent 2 0 R /Contents [4 0 R 8 0 R] >> endobj\n");
        sb.Append($"4 0 obj << /Length {content1.Length} >> stream\n{content1}\nendstream endobj\n");
        sb.Append("5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n");
        sb.Append("6 0 obj << /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /ToUnicode 7 0 R >> endobj\n");
        sb.Append($"7 0 obj << /Length {cmap.Length} >> stream\n{cmap}\nendstream endobj\n");
        sb.Append($"8 0 obj << /Length {content2.Length} >> stream\n{content2}\nendstream endobj\n");
        sb.Append("trailer << /Root 1 0 R >>\n%%EOF");
        string text = FinderPdfText.TryExtract(Encoding.Latin1.GetBytes(sb.ToString()))!;
        Assert.Contains("Hello (PDF) World", text);
        Assert.Contains("한글", text);
        Assert.Contains("설 한", text.Replace("\n", " "));

        Assert.Null(FinderPdfText.TryExtract("not a pdf"u8.ToArray()));
        Assert.Null(FinderPdfText.TryExtract(Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Encrypt 2 0 R >> endobj")));
    }

    [Fact]
    public void Content_FindsTextInsideACompressedPdfContentStream()
    {
        using var dir = new TempDirectory();
        byte[] raw = Encoding.Latin1.GetBytes("BT /F1 12 Tf (compressed needle) Tj ET");
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        byte[] deflated = packed.ToArray();
        using var pdf = new MemoryStream();
        void Add(byte[] bytes) => pdf.Write(bytes);
        void AddText(string text) => Add(Encoding.Latin1.GetBytes(text));
        AddText("%PDF-1.5\n1 0 obj << /Type /Page /Contents 2 0 R /Resources << /Font << /F1 3 0 R >> >> >> endobj\n");
        AddText($"2 0 obj << /Length {deflated.Length} /Filter /FlateDecode >> stream\n");
        Add(deflated);
        AddText("\nendstream endobj\n3 0 obj << /Type /Font /Subtype /Type1 >> endobj\n%%EOF");
        File.WriteAllBytes(dir.File("doc.pdf"), pdf.ToArray());
        var idx = new FinderIndex();
        FinderScanner.ScanFolder(idx, dir.Path, null, null, default);
        Assert.Equal(["doc.pdf"], Names(idx, "content:needle"));
        Assert.Empty(Names(idx, "content:haystack"));
    }

    // ---- NTFS ----

    /// <summary>A volume made of a list of files, with a journal that hands out queued records.</summary>
    private sealed class FakeVolume : IVolumeAccess
    {
        public const ulong Root = 5;
        public List<MftEntry> Entries { get; } =
        [
            new(3, 5, 0x20, "$MFT"),
            new(30, Root, FinderIndex.DirectoryAttribute, "docs"),
            new(31, 30, 0x20, "a.txt"),
            new(32, Root, 0x20, "B.TXT"),
            new(33, Root, 0x20, "junk.tmp"),
            new(40, 99, 0x20, "orphan.txt"),
        ];

        public Queue<List<UsnRecord>> Journal { get; } = new();
        public List<MftMeta> Meta { get; } = [];
        public bool JournalActive { get; set; } = true;
        public bool Created { get; private set; }
        public long Next { get; set; } = 1000;

        public UsnJournalInfo QueryJournal()
        {
            if (!JournalActive)
                throw new VolumeException(FinderNtfs.ErrorJournalNotActive, "no journal");
            return new UsnJournalInfo(77, 0, Next, 0);
        }

        public void CreateJournal()
        {
            Created = true;
            JournalActive = true;
        }

        public IEnumerable<MftEntry> EnumMft(long highUsn) => Entries;

        public (long NextUsn, List<UsnRecord> Records) ReadJournal(long startUsn, ulong journalId) =>
            (startUsn + 1, Journal.Count > 0 ? Journal.Dequeue() : []);

        public IEnumerable<List<MftMeta>> ScanMetadata(int[]? wanted, CancellationToken cancel)
        {
            yield return [.. Meta.Where(m => wanted is null || (m.Record < wanted.Length && wanted[m.Record] >= 0))];
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Ntfs_IndexVolume_LinksParentsDropsMetafilesAndOrphansAndExcludes()
    {
        var fake = new FakeVolume { JournalActive = false };
        var idx = new FinderIndex();
        int vol = FinderNtfsIndexer.IndexVolume(idx, @"Z:\", fake, new FinderExcludes(patterns: ["*.tmp"]), null, default);

        Assert.True(fake.Created);
        Assert.Equal("ntfs", idx.Roots[vol].Kind);
        Assert.Equal(77UL, idx.Roots[vol].JournalId);
        Assert.Equal(1000, idx.Roots[vol].NextUsn);
        Assert.Equal(["a.txt", "B.TXT"], Names(idx, "txt"));
        Assert.Empty(Names(idx, "orphan"));
        Assert.Empty(Names(idx, "junk"));
        Assert.Empty(Names(idx, "$mft"));
        Assert.Equal(@"Z:\docs\a.txt", idx.FullPath(idx.Names.IndexOf("a.txt")));
        Assert.Equal(@"Z:\B.TXT", idx.FullPath(idx.Names.IndexOf("B.TXT")));
    }

    [Fact]
    public void Ntfs_ExcludedFoldersTakeTheirContentsAlong()
    {
        var idx = new FinderIndex();
        FinderNtfsIndexer.IndexVolume(idx, @"Z:\", new FakeVolume(), new FinderExcludes(folders: [@"Z:\docs"]), null, default);
        Assert.Empty(Names(idx, "a.txt"));
        Assert.Equal(["B.TXT"], Names(idx, "b.txt"));
    }

    [Fact]
    public void Ntfs_UsnRecordsCreateRenameModifyAndDelete()
    {
        var idx = new FinderIndex();
        int vol = FinderNtfsIndexer.IndexVolume(idx, @"Z:\", new FakeVolume(), null, null, default);

        Assert.True(FinderNtfsIndexer.ApplyUsnRecord(idx, vol, new UsnRecord(50, 30, 1, FinderNtfs.ReasonFileCreate, 0x20, "new.doc")));
        Assert.Equal(@"Z:\docs\new.doc", idx.FullPath(idx.Names.IndexOf("new.doc")));

        Assert.True(FinderNtfsIndexer.ApplyUsnRecord(idx, vol, new UsnRecord(50, FakeVolume.Root, 2, FinderNtfs.ReasonRenameNewName, 0x20, "moved.doc")));
        Assert.Equal(@"Z:\moved.doc", idx.FullPath(idx.Names.IndexOf("moved.doc")));
        Assert.Empty(Names(idx, "new.doc"));

        int moved = idx.Names.IndexOf("moved.doc");
        idx.Size[moved] = 5;
        idx.MTime[moved] = 9;
        Assert.True(FinderNtfsIndexer.ApplyUsnRecord(idx, vol, new UsnRecord(50, FakeVolume.Root, 3, FinderNtfs.ReasonDataExtend, 0x20, "moved.doc")));
        Assert.Equal(0.0, idx.MTime[moved]);

        Assert.False(FinderNtfsIndexer.ApplyUsnRecord(idx, vol, new UsnRecord(60, 98, 4, FinderNtfs.ReasonFileCreate, 0x20, "no-parent.txt")));
        Assert.False(FinderNtfsIndexer.ApplyUsnRecord(idx, vol, new UsnRecord(10, 5, 5, FinderNtfs.ReasonFileCreate, 0x20, "metafile")));

        Assert.True(FinderNtfsIndexer.ApplyUsnRecord(idx, vol, new UsnRecord(50, FakeVolume.Root, 6, FinderNtfs.ReasonFileDelete, 0x20, "moved.doc")));
        Assert.Empty(Names(idx, "moved.doc"));
        Assert.False(FinderNtfsIndexer.ApplyUsnRecord(idx, vol, new UsnRecord(50, FakeVolume.Root, 7, FinderNtfs.ReasonFileDelete, 0x20, "moved.doc")));
    }

    [Fact]
    public void Ntfs_FillMetadataTakesSizesAndDatesFromTheMft()
    {
        var fake = new FakeVolume();
        fake.Meta.Add(new MftMeta(31, 123, 1.0, 1_700_000_000, 2.0));
        fake.Meta.Add(new MftMeta(30, -1, 1.0, 1_600_000_000, 2.0));
        fake.Meta.Add(new MftMeta(32, 0, 1.0, 0, 2.0));
        var idx = new FinderIndex();
        int vol = FinderNtfsIndexer.IndexVolume(idx, @"Z:\", fake, null, null, default);
        Assert.Equal(3, FinderNtfsIndexer.FillMetadata(idx, vol, fake, default));
        int a = idx.Names.IndexOf("a.txt");
        Assert.Equal(123, idx.Size[a]);
        Assert.Equal(1_700_000_000, idx.MTime[a]);
        Assert.Equal(0.001, idx.MTime[idx.Names.IndexOf("B.TXT")]);
        Assert.Equal(-1, idx.Size[idx.Names.IndexOf("docs")]);
    }

    [Fact]
    public async Task Ntfs_UsnMonitorAppliesJournalRecordsInTheBackground()
    {
        var fake = new FakeVolume();
        var idx = new FinderIndex();
        int vol = FinderNtfsIndexer.IndexVolume(idx, @"Z:\", fake, null, null, default);
        fake.Journal.Enqueue([new UsnRecord(70, FakeVolume.Root, 1, FinderNtfs.ReasonFileCreate, 0x20, "live.txt")]);
        bool stale = false;
        using var monitor = new FinderUsnMonitor(idx, vol, null, _ => stale = true, _ => fake, TimeSpan.FromMilliseconds(20));
        monitor.Start();
        for (int i = 0; i < 100 && Names(idx, "live").Length == 0; i++)
            await Task.Delay(20);
        Assert.Equal(["live.txt"], Names(idx, "live"));
        Assert.False(stale);
    }

    [Fact]
    public async Task Ntfs_UsnMonitorReportsAJournalThatWasReplaced()
    {
        var fake = new FakeVolume();
        var idx = new FinderIndex();
        int vol = FinderNtfsIndexer.IndexVolume(idx, @"Z:\", fake, null, null, default);
        idx.Roots[vol].JournalId = 1; // the volume now reports journal 77
        int stale = -1;
        using var monitor = new FinderUsnMonitor(idx, vol, null, v => stale = v, _ => fake, TimeSpan.FromMilliseconds(20));
        monitor.Start();
        for (int i = 0; i < 100 && stale < 0; i++)
            await Task.Delay(20);
        Assert.Equal(vol, stale);
    }

    [Fact]
    public void Ntfs_ParsesUsnRecordsFromTheirBinaryForm()
    {
        byte[] buffer = [.. new byte[8], .. UsnRecordBytes(31, 30, 99, FinderNtfs.ReasonFileCreate, 0x20, "한글.txt"), .. UsnRecordBytes(32, 5, 100, FinderNtfs.ReasonFileDelete, 0x10, "dir")];
        UsnRecord[] records = [.. FinderNtfs.ParseUsnRecords(buffer, 8)];
        Assert.Equal(2, records.Length);
        Assert.Equal(new UsnRecord(31, 30, 99, FinderNtfs.ReasonFileCreate, 0x20, "한글.txt"), records[0]);
        Assert.Equal("dir", records[1].Name);
        Assert.Empty(FinderNtfs.ParseUsnRecords(new byte[20]));
    }

    private static byte[] UsnRecordBytes(ulong frn, ulong parent, long usn, uint reason, uint attrs, string name)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int length = (60 + nameBytes.Length + 7) & ~7;
        byte[] r = new byte[length];
        BitConverter.TryWriteBytes(r.AsSpan(0), length);
        BitConverter.TryWriteBytes(r.AsSpan(4), (ushort)2);
        BitConverter.TryWriteBytes(r.AsSpan(8), frn);
        BitConverter.TryWriteBytes(r.AsSpan(16), parent);
        BitConverter.TryWriteBytes(r.AsSpan(24), usn);
        BitConverter.TryWriteBytes(r.AsSpan(40), reason);
        BitConverter.TryWriteBytes(r.AsSpan(52), attrs);
        BitConverter.TryWriteBytes(r.AsSpan(56), (ushort)nameBytes.Length);
        BitConverter.TryWriteBytes(r.AsSpan(58), (ushort)60);
        nameBytes.CopyTo(r, 60);
        return r;
    }

    /// <summary>One 1024-byte MFT record with a $STANDARD_INFORMATION and an unnamed resident $DATA.</summary>
    private static byte[] MftRecord(ushort flags, long size, long mtimeFileTime)
    {
        byte[] rec = new byte[1024];
        "FILE"u8.CopyTo(rec);
        BitConverter.TryWriteBytes(rec.AsSpan(4), (ushort)0x30); // update sequence offset
        BitConverter.TryWriteBytes(rec.AsSpan(6), (ushort)3); // number + 2 sectors
        BitConverter.TryWriteBytes(rec.AsSpan(0x14), (ushort)0x38); // first attribute
        BitConverter.TryWriteBytes(rec.AsSpan(0x16), flags);
        int pos = 0x38;
        // $STANDARD_INFORMATION, resident
        BitConverter.TryWriteBytes(rec.AsSpan(pos), 0x10);
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 4), 0x60);
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 0x10), 0x48);
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 0x14), (ushort)0x18);
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 0x18), 116444736000000000L + 10_000_000L); // created: epoch + 1 s
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 0x20), mtimeFileTime);
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 0x30), 116444736000000000L + 30_000_000L); // accessed
        pos += 0x60;
        // $DATA, resident, unnamed
        BitConverter.TryWriteBytes(rec.AsSpan(pos), 0x80);
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 4), 0x20);
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 0x10), (uint)size);
        BitConverter.TryWriteBytes(rec.AsSpan(pos + 0x14), (ushort)0x18);
        pos += 0x20;
        BitConverter.TryWriteBytes(rec.AsSpan(pos), 0xFFFFFFFF);
        // The update sequence array: number 0xABCD, saved bytes 'x' 'y' for the two sector ends.
        rec[0x30] = 0xCD;
        rec[0x31] = 0xAB;
        rec[0x32] = (byte)'x';
        rec[0x33] = (byte)'x';
        rec[0x34] = (byte)'y';
        rec[0x35] = (byte)'y';
        rec[510] = 0xCD;
        rec[511] = 0xAB;
        rec[1022] = 0xCD;
        rec[1023] = 0xAB;
        return rec;
    }

    [Fact]
    public void Mft_ApplyFixupsRestoresSectorEndsAndRejectsTornRecords()
    {
        byte[] rec = MftRecord(1, 5, 116444736000000000L);
        Assert.True(FinderMftParser.ApplyFixups(rec, 0, 1024, 512));
        Assert.Equal((byte)'x', rec[510]);
        Assert.Equal((byte)'y', rec[1023]);

        byte[] torn = MftRecord(1, 5, 116444736000000000L);
        torn[511] = 0;
        Assert.False(FinderMftParser.ApplyFixups(torn, 0, 1024, 512));
    }

    [Fact]
    public void Mft_ParseRecordReadsSizeAndTimes_AndSkipsUnusedOrExtensionRecords()
    {
        byte[] rec = MftRecord(1, 321, 116444736000000000L + 50_000_000L);
        Assert.True(FinderMftParser.ApplyFixups(rec, 0, 1024, 512));
        (long size, double c, double m, double a) = FinderMftParser.ParseRecord(rec, 0, 1024)!.Value;
        Assert.Equal(321, size);
        Assert.Equal(1.0, c);
        Assert.Equal(5.0, m);
        Assert.Equal(3.0, a);

        byte[] folder = MftRecord(1 | 2, 0, 116444736000000000L);
        Assert.Equal(-1, FinderMftParser.ParseRecord(folder, 0, 1024)!.Value.Size);
        Assert.Null(FinderMftParser.ParseRecord(MftRecord(0, 1, 0), 0, 1024)); // not in use
        byte[] extension = MftRecord(1, 1, 0);
        BitConverter.TryWriteBytes(extension.AsSpan(0x20), 5UL); // a base record number: this is an extension record
        Assert.Null(FinderMftParser.ParseRecord(extension, 0, 1024));
    }

    [Fact]
    public void Mft_ScanMetadataReadsRecordsOfAnImageThroughItsRuns()
    {
        // A tiny volume image: boot sector, then an $MFT of four records (record 0 describes it) at cluster 2.
        const int Cluster = 1024;
        byte[] image = new byte[Cluster * 8];
        "NTFS    "u8.CopyTo(image.AsSpan(3));
        BitConverter.TryWriteBytes(image.AsSpan(0x0B), (ushort)512);
        image[0x0D] = 2; // sectors per cluster
        BitConverter.TryWriteBytes(image.AsSpan(0x30), 2L); // $MFT at cluster 2
        image[0x40] = unchecked((byte)(sbyte)-10); // 2^10-byte records

        byte[] mft0 = MftRecord(1, 0, 0);
        int pos = 0x38 + 0x60; // replace the $DATA with a non-resident one describing the $MFT's own run
        Array.Clear(mft0, pos, 0x40);
        BitConverter.TryWriteBytes(mft0.AsSpan(pos), 0x80);
        BitConverter.TryWriteBytes(mft0.AsSpan(pos + 4), 0x48);
        mft0[pos + 8] = 1; // non-resident
        BitConverter.TryWriteBytes(mft0.AsSpan(pos + 0x20), (ushort)0x40); // run list offset
        BitConverter.TryWriteBytes(mft0.AsSpan(pos + 0x30), 4096L); // data size: four records
        byte[] runs = [0x11, 0x04, 0x02, 0x00]; // 4 clusters at cluster 2
        runs.CopyTo(mft0, pos + 0x40);
        BitConverter.TryWriteBytes(mft0.AsSpan(pos + 0x48), 0xFFFFFFFF);
        mft0.CopyTo(image, 2 * Cluster);
        MftRecord(1, 77, 116444736000000000L + 20_000_000L).CopyTo(image, (2 * Cluster) + 1024);
        MftRecord(1 | 2, 0, 116444736000000000L).CopyTo(image, (2 * Cluster) + 2048);

        byte[] Read(long offset, int length) => image.AsSpan((int)offset, Math.Min(length, image.Length - (int)offset)).ToArray();
        List<MftMeta> all = [.. FinderMftParser.ScanMetadata(Read, null, default).SelectMany(b => b)];
        Assert.Contains(all, m => m.Record == 1 && m.Size == 77 && m.MTime == 2.0);
        Assert.Contains(all, m => m.Record == 2 && m.Size == -1);

        int[] wanted = [-1, -1, 5, -1];
        List<MftMeta> few = [.. FinderMftParser.ScanMetadata(Read, wanted, default).SelectMany(b => b)];
        Assert.Equal([2], few.Select(m => m.Record));
    }

    // ---- the elevated helper's protocol ----

    [Fact]
    public async Task Helper_ServesVolumeRequestsOverItsPipe_AndRefusesAWrongToken()
    {
        var fake = new FakeVolume();
        fake.Meta.Add(new MftMeta(31, 9, 1, 2, 3));
        fake.Journal.Enqueue([new UsnRecord(31, 30, 7, FinderNtfs.ReasonFileCreate, 0x20, "x.txt")]);
        Func<string, IVolumeAccess> original = FinderVolumeHelper.VolumeFactory;
        FinderVolumeHelper.VolumeFactory = _ => new Shared(fake);
        string pipe = "M-Dir-test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        Task server = Task.Run(() => FinderVolumeHelper.Listen(pipe, "secret", WindowsIdentity.GetCurrent().User!, stop.Token));
        try
        {
            using (IVolumeAccess volume = FinderVolumeHelper.OpenAt(pipe, "secret", @"Z:\"))
            {
                UsnJournalInfo info = volume.QueryJournal();
                Assert.Equal(new UsnJournalInfo(77, 0, 1000, 0), info);
                MftEntry[] entries = [.. volume.EnumMft(info.NextUsn)];
                Assert.Equal(fake.Entries.Count, entries.Length);
                Assert.Equal(new MftEntry(31, 30, 0x20, "a.txt"), entries[2]);

                (long next, List<UsnRecord> records) = volume.ReadJournal(500, 77);
                Assert.Equal(501, next);
                Assert.Equal("x.txt", Assert.Single(records).Name);

                int[] wanted = new int[40];
                Array.Fill(wanted, -1);
                wanted[31] = 1;
                MftMeta meta = Assert.Single(volume.ScanMetadata(wanted, default).SelectMany(b => b));
                Assert.Equal(new MftMeta(31, 9, 1, 2, 3), meta);

                fake.JournalActive = false;
                VolumeException error = Assert.Throws<VolumeException>(() => volume.QueryJournal());
                Assert.Equal(FinderNtfs.ErrorJournalNotActive, error.Code);
                fake.JournalActive = true;
                Assert.Equal(77UL, volume.QueryJournal().JournalId); // the connection is still usable after an error
            }

            Assert.ThrowsAny<IOException>(() => FinderVolumeHelper.OpenAt(pipe, "wrong", @"Z:\"));
        }
        finally
        {
            stop.Cancel();
            FinderVolumeHelper.VolumeFactory = original;
            await Task.WhenAny(server, Task.Delay(2000));
        }
    }

    /// <summary>Lets the helper's per-connection dispose leave the shared fake volume alone.</summary>
    private sealed class Shared(IVolumeAccess inner) : IVolumeAccess
    {
        public UsnJournalInfo QueryJournal() => inner.QueryJournal();
        public void CreateJournal() => inner.CreateJournal();
        public IEnumerable<MftEntry> EnumMft(long highUsn) => inner.EnumMft(highUsn);
        public (long NextUsn, List<UsnRecord> Records) ReadJournal(long startUsn, ulong journalId) => inner.ReadJournal(startUsn, journalId);
        public IEnumerable<List<MftMeta>> ScanMetadata(int[]? wanted, CancellationToken cancel) => inner.ScanMetadata(wanted, cancel);
        public void Dispose()
        {
        }
    }

    // ---- the engine as a backend ----

    private static FinderOptions FolderOptions(string root) => new() { Roots = [root], UseMft = false, Monitor = false, FolderSizes = true };

    private static async Task<FinderPage> SearchWhenReady(FinderEngine engine, FinderQuery query)
    {
        for (int i = 0; i < 100; i++)
        {
            if ((await engine.StatusAsync()).Ready)
                return await engine.SearchAsync(query, 0, 50);
            await Task.Delay(50);
        }

        throw new TimeoutException("The engine never became ready.");
    }

    [Fact]
    public async Task Engine_BuildsAnIndexAnswersSearchesAndReloadsItFromDisk()
    {
        using TempDirectory dir = SampleTree();
        using var data = new TempDirectory();
        string database = data.File("index.mfi");

        using (var engine = new FinderEngine(FolderOptions(dir.Path), database))
        {
            engine.Start();
            FinderPage page = await SearchWhenReady(engine, new FinderQuery("txt"));
            Assert.Equal(2, page.Total);
            Assert.Equal(["alpha.txt", "gamma.txt"], page.Rows.Select(r => r.Name).Order().ToArray());
            FinderRow alpha = page.Rows.Single(r => r.Name == "alpha.txt");
            Assert.Equal(dir.Path, alpha.Folder);
            Assert.Equal(11, alpha.Size);
            Assert.NotNull(alpha.Modified);

            FinderPage folders = await engine.SearchAsync(new FinderQuery("", Filter: "Folder"), 0, 50);
            Assert.All(folders.Rows, r => Assert.True(r.IsDirectory));
            Assert.Equal(2, folders.Total);

            FinderPage sorted = await engine.SearchAsync(new FinderQuery("", "size", false), 0, 50);
            Assert.Equal("alpha.txt", sorted.Rows.First(r => !r.IsDirectory).Name);

            FinderPage paged = await engine.SearchAsync(new FinderQuery("txt"), 1, 50);
            Assert.Equal(2, paged.Total);
            Assert.Single(paged.Rows);

            Assert.Contains("Folder", await engine.FiltersAsync());
            await engine.Started;
            FinderException error = await Assert.ThrowsAsync<FinderException>(() => engine.SearchAsync(new FinderQuery("size:oops"), 0, 10));
            Assert.Contains("size", error.Message);
        }

        Assert.True(File.Exists(database));
        using var again = new FinderEngine(FolderOptions(dir.Path), database);
        again.Start();
        await again.Started;
        FinderStatus status = await again.StatusAsync();
        Assert.True(status.Ready);
        Assert.Contains("저장된", status.Text);
        Assert.Equal(2, (await again.SearchAsync(new FinderQuery("txt"), 0, 10)).Total);
    }

    [Fact]
    public async Task Engine_BuildsAgainWhenTheOptionsChange()
    {
        using TempDirectory dir = SampleTree();
        using var data = new TempDirectory();
        string database = data.File("index.mfi");
        using (var engine = new FinderEngine(FolderOptions(dir.Path), database))
        {
            engine.Start();
            await SearchWhenReady(engine, new FinderQuery("txt"));
            await engine.Started;
        }

        FinderOptions changed = FolderOptions(dir.Path);
        changed.ExcludePatterns = ["*.txt"];
        using var second = new FinderEngine(changed, database);
        second.Start();
        FinderPage page = await SearchWhenReady(second, new FinderQuery("txt"));
        await second.Started;
        Assert.Equal(0, page.Total);
        Assert.DoesNotContain("저장된", (await second.StatusAsync()).Text);
    }

    [Fact]
    public async Task Engine_IndexesNtfsThroughTheMftWhenAVolumeIsAvailable_AndFallsBackToWalkingOtherwise()
    {
        using var data = new TempDirectory();
        var options = new FinderOptions { Roots = [@"Z:\"], UseMft = true, Monitor = false, FolderSizes = false, MftConsent = FinderMftConsent.Allow };
        var fake = new FakeVolume();
        fake.Meta.Add(new MftMeta(31, 42, 1, 1_700_000_000, 1));
        using (var engine = new FinderEngine(options, data.File("a.mfi")) { VolumeOverride = _ => fake })
        {
            engine.Start();
            FinderPage page = await SearchWhenReady(engine, new FinderQuery("a.txt"));
            Assert.Equal(@"Z:\docs", Assert.Single(page.Rows).Folder);
            await engine.Started;
            for (int i = 0; i < 100 && engine.Index.MTime[engine.Index.Names.IndexOf("a.txt")] == 0.0; i++)
                await Task.Delay(30);
            Assert.Equal(1_700_000_000, engine.Index.MTime[engine.Index.Names.IndexOf("a.txt")]);
            Assert.Equal(42, engine.Index.Size[engine.Index.Names.IndexOf("a.txt")]);
        }

        // No access to the volume: the root is walked instead (and Z:\ does not exist here, so nothing is found, but nothing breaks).
        using var walking = new FinderEngine(options, data.File("b.mfi")) { VolumeOverride = _ => throw new IOException("denied") };
        walking.Start();
        FinderPage empty = await SearchWhenReady(walking, new FinderQuery("a.txt"));
        Assert.Equal(0, empty.Total);
    }

    [Fact]
    public async Task Engine_AsksForElevationOnlyOnceAndRemembersADenial()
    {
        using var data = new TempDirectory();
        int asked = 0;
        var options = new FinderOptions { Roots = [@"Z:\", @"Y:\"], UseMft = true, Monitor = false, FolderSizes = false };
        using var engine = new FinderEngine(options, data.File("c.mfi"), () =>
        {
            asked++;
            return false;
        });
        engine.Start();
        await SearchWhenReady(engine, new FinderQuery("x"));
        await engine.Started;
        Assert.True(asked <= 1);
    }

    // ---- options ----

    [Fact]
    public void Options_RoundTripThroughTheIniFile_AndOnlyStoreWhatDiffersFromTheDefaults()
    {
        var options = new FinderOptions
        {
            Roots = [@"D:\Work", @"E:\"],
            UseMft = false,
            MftConsent = FinderMftConsent.Deny,
            ExcludeHidden = true,
            ExcludeFolders = [@"D:\Work\node_modules"],
            ExcludePatterns = ["*.tmp", "~$*"],
            ContentMaxMb = 8,
            ExternalPath = @"C:\Tools\M-Finder.exe",
        };
        var ini = new IniFile();
        options.Write(ini);
        FinderOptions back = FinderOptions.Read(IniFile.Parse(ini.ToText()));
        Assert.Equal(options.Roots, back.Roots);
        Assert.False(back.UseMft);
        Assert.Equal(FinderMftConsent.Deny, back.MftConsent);
        Assert.True(back.ExcludeHidden);
        Assert.Equal(options.ExcludeFolders, back.ExcludeFolders);
        Assert.Equal(options.ExcludePatterns, back.ExcludePatterns);
        Assert.Equal(8, back.ContentMaxMb);
        Assert.Equal(options.ExternalPath, back.ExternalPath);
        Assert.Equal(options.Signature, back.Signature);

        // An external M-Finder path alone (as older versions stored it) does not switch the search away from the built-in one.
        Assert.False(back.UsesExternal);
        back.UseExternal = true;
        Assert.True(back.UsesExternal);
        var legacy = FinderOptions.Read(IniFile.Parse("[Finder]\nPath=D:\\Tools\\M-Finder.exe\n"));
        Assert.Equal(@"D:\Tools\M-Finder.exe", legacy.ExternalPath);
        Assert.False(legacy.UsesExternal);
        var enabled = new IniFile();
        back.Write(enabled);
        Assert.True(FinderOptions.Read(IniFile.Parse(enabled.ToText())).UsesExternal);

        var defaults = new IniFile();
        new FinderOptions().Write(defaults);
        Assert.False(defaults.HasSection("Finder"));
        Assert.NotEqual(new FinderOptions().Signature, options.Signature);
    }
}
