using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MDir.Commands;
using MDir.Features;
using MDir.Finder;
using MDir.Settings;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>The M-Finder panel: the pipe protocol, finding M-Finder, and the panel in the real main window.</summary>
public class FinderTests(ITestOutputHelper output)
{
    /// <summary>Reads from one buffer and writes to another, like the two directions of a pipe.</summary>
    private sealed class DuplexStream(byte[] incoming) : Stream
    {
        private readonly MemoryStream _in = new(incoming);

        public MemoryStream Written { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }

    private static byte[] Frame(byte type, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[5 + payload.Length];
        BitConverter.GetBytes((uint)payload.Length).CopyTo(frame, 0);
        frame[4] = type;
        payload.CopyTo(frame, 5);
        return frame;
    }

    [Fact]
    public async Task Call_SendsOneJsonFrame_SkipsBinaryChunks_AndReturnsTheEndResult()
    {
        byte[] reply = [.. Frame(FinderProtocol.Binary, "xx"), .. Frame(FinderProtocol.End, """{"ok": true, "n": 7}""")];
        var stream = new DuplexStream(reply);

        JsonElement result = await FinderProtocol.CallAsync(stream, new { op = "ping" });

        Assert.Equal(7, result.GetProperty("n").GetInt32());
        byte[] sent = stream.Written.ToArray();
        Assert.Equal((uint)(sent.Length - 5), BitConverter.ToUInt32(sent, 0));
        Assert.Equal(FinderProtocol.Json, sent[4]);
        Assert.Equal("""{"op":"ping"}""", Encoding.UTF8.GetString(sent, 5, sent.Length - 5));
    }

    [Fact]
    public async Task ErrorFrame_BecomesFinderException_AndATruncatedReplyAnIOException()
    {
        var error = new DuplexStream(Frame(FinderProtocol.Error, """{"error": "bad regex"}"""));
        FinderException ex = await Assert.ThrowsAsync<FinderException>(() => FinderProtocol.CallAsync(error, new { op = "search" }));
        Assert.Equal("bad regex", ex.Message);

        var truncated = new DuplexStream(Frame(FinderProtocol.End, "{}")[..3]);
        await Assert.ThrowsAnyAsync<IOException>(() => FinderProtocol.CallAsync(truncated, new { op = "status" }));
    }

    [Fact]
    public void ParsePage_ReadsRows_AndToleratesMissingValues()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"ok": true, "total": 1234, "rev": 9, "results": [
              {"name": "a.txt", "path": "C:\\data", "is_dir": false, "size": 2048, "date_modified": 1700000000.5},
              {"name": "C:", "path": "", "is_dir": true, "size": null, "date_modified": null}
            ]}
            """);

        FinderPage page = FinderProtocol.ParsePage(doc.RootElement);

        Assert.Equal(1234, page.Total);
        Assert.Equal(9, page.Revision);
        Assert.Equal(@"C:\data\a.txt", page.Rows[0].FullPath);
        Assert.Equal(2048, page.Rows[0].Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000500).LocalDateTime, page.Rows[0].Modified);
        Assert.True(page.Rows[1].IsDirectory);
        Assert.Null(page.Rows[1].Size);
        Assert.Null(page.Rows[1].Modified);
        Assert.Equal("C:", page.Rows[1].FullPath);

        using JsonDocument old = JsonDocument.Parse("""{"ok": true, "objects": 5, "status": "5 objects"}""");
        FinderStatus status = FinderProtocol.ParseStatus(old.RootElement);
        Assert.True(status.Ready); // an older M-Finder without "ready" answers only once it has an index
        Assert.Equal(5, status.Objects);
    }

    [Fact]
    public async Task Client_TalksToAPipeServer_KeepsTheConnection_AndReportsAMissingServer()
    {
        string name = "M-Dir-Test-" + Guid.NewGuid().ToString("N");
        var requests = new List<string>();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            for (int i = 0; i < 2; i++)
            {
                (byte type, byte[] payload) = await FinderProtocol.ReadFrameAsync(server);
                Assert.Equal(FinderProtocol.Json, type);
                string request = Encoding.UTF8.GetString(payload);
                requests.Add(request);
                string reply = request.Contains("\"search\"")
                    ? """{"ok": true, "total": 1, "rev": 3, "results": [{"name": "x.txt", "path": "D:\\", "is_dir": false, "size": 1, "date_modified": 0}]}"""
                    : """{"ok": true, "ready": false, "building": true, "objects": 10, "status": "Indexing", "rev": 3}""";
                await FinderProtocol.WriteFrameAsync(server, FinderProtocol.End, Encoding.UTF8.GetBytes(reply));
            }
        });

        using var client = new FinderClient(name);
        FinderStatus status = await client.StatusAsync();
        FinderPage page = await client.SearchAsync(new FinderQuery("x", "size", Ascending: false, MatchCase: true), 200, 50);
        await serving;

        Assert.False(status.Ready);
        Assert.True(status.Building);
        Assert.Equal(@"D:\x.txt", Assert.Single(page.Rows).FullPath);
        using JsonDocument search = JsonDocument.Parse(requests[1]);
        Assert.Equal("x", search.RootElement.GetProperty("query").GetString());
        Assert.Equal("size", search.RootElement.GetProperty("sort").GetString());
        Assert.False(search.RootElement.GetProperty("asc").GetBoolean());
        Assert.True(search.RootElement.GetProperty("case").GetBoolean());
        Assert.Equal(200, search.RootElement.GetProperty("offset").GetInt32());

        using var nobody = new FinderClient("M-Dir-Test-none-" + Guid.NewGuid().ToString("N")) { ConnectTimeoutMs = 100 };
        await Assert.ThrowsAsync<FinderUnavailableException>(nobody.StatusAsync);
        Assert.False(await nobody.PingAsync());
    }

    [Fact]
    public void Launcher_FindsTheExeInAFolder_AndRejectsAFolderWithoutM_Finder()
    {
        using var dir = new TempDirectory();
        Assert.Null(FinderLauncher.FromPath(dir.Path));
        Assert.Null(FinderLauncher.FromPath(dir.File("missing.exe")));

        File.WriteAllText(dir.File("M-Finder.exe"), string.Empty);
        FinderProgram program = FinderLauncher.FromPath(dir.Path)!;
        Assert.Equal(dir.File("M-Finder.exe"), program.FileName);
        Assert.Equal(["gui", "--headless"], program.Arguments);
        Assert.Equal(program.FileName, FinderLauncher.Locate(dir.File("M-Finder.exe"))!.FileName);
    }

    [Fact]
    public void Workspace_KeepsTheFinderPanel_AndTheToolbarHasItsButtonAfterTheLayouts()
    {
        using var dir = new TempDirectory();
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(dir.Path)!);
        var workspace = new Workspace { Panes = basis.Panes, FinderVisible = true, FinderSplit = 2500 };
        string file = dir.File("w.mdr");
        workspace.Save(file);
        Workspace loaded = Workspace.Load(file)!;
        Assert.True(loaded.FinderVisible);
        Assert.Equal(2500, loaded.FinderSplit);

        int layouts = ToolbarLayout.Default.ToList().IndexOf(ToolbarLayout.Layouts);
        Assert.Equal("view.finder", ToolbarLayout.Default[layouts + 1]);
    }

    /// <summary>Answers searches from a fixed list of files: every row whose name contains the text.</summary>
    private sealed class FakeBackend(IReadOnlyList<string> paths) : IFinderBackend
    {
        public int Searches { get; private set; }

        public Task<FinderStatus> StatusAsync() => Task.FromResult(new FinderStatus(true, false, paths.Count, "ready", 1));

        public Task<FinderPage> SearchAsync(FinderQuery query, int offset, int count)
        {
            Searches++;
            FinderRow[] rows = [.. paths
                .Where(p => Path.GetFileName(p).Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                .Select(p => new FinderRow(Path.GetFileName(p), Path.GetDirectoryName(p)!, false, new FileInfo(p).Length, File.GetLastWriteTime(p)))];
            return Task.FromResult(new FinderPage(rows.Length, 1, [.. rows.Skip(offset).Take(count)]));
        }

        public Task<IReadOnlyList<string>> FiltersAsync() => Task.FromResult<IReadOnlyList<string>>(["Picture", "Document"]);

        public Task QuitAsync() => Task.CompletedTask;
    }

    [Fact]
    public void FinderPanel_SitsLeftOfThePreview_SearchesAndPreviewsTheClickedResult()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("alpha.txt"), "alpha text");
        File.WriteAllText(dir.File("beta.txt"), "beta text");
        Directory.CreateDirectory(dir.File("sub"));
        File.WriteAllText(Path.Combine(dir.File("sub"), "gamma.txt"), "gamma text");
        string[] files = [dir.File("alpha.txt"), dir.File("beta.txt"), Path.Combine(dir.File("sub"), "gamma.txt")];

        Run(() => new MainForm(null, Workspace.CreateDefault(ShellLocation.Parse(dir.Path)!), null, null)
        {
            Size = new Size(1500, 800),
            FinderBackendOverride = new FakeBackend(files),
        }, async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "the folder is listed");

            // The toolbar button follows the layout drop-down and carries the M-Finder picture.
            ToolStripItem button = form.Toolbar!.Items.Cast<ToolStripItem>().Single(item => item.Tag is Command { Id: "view.finder" });
            Assert.NotNull(button.Image);
            Assert.IsType<ToolStripDropDownButton>(form.Toolbar.Items[form.Toolbar.Items.IndexOf(button) - 1]);

            form.ToggleFinder();
            form.TogglePreview();
            FinderPane finder = form.FinderPane!;
            PreviewPane preview = form.PreviewPane!;
            Assert.True(form.FinderVisible);
            await WaitUntil(() => finder.Total == 3, "the empty search lists everything");

            // Side by side: panes | M-Finder | preview.
            Rectangle finderBounds = finder.Bounds, previewBounds = preview.Bounds;
            Assert.True(finderBounds.Right <= previewBounds.Left, $"finder {finderBounds} is left of preview {previewBounds}");
            Assert.True(form.LayoutPanel.Bounds.Right <= finderBounds.Left, "the panes are left of the finder");
            Assert.Equal(finderBounds.Top, previewBounds.Top);
            Assert.Equal(finderBounds.Height, previewBounds.Height);

            finder.SearchBox.Text = "gam";
            await WaitUntil(() => finder.Total == 1, "the search narrowed the list");

            // Clicking a result shows it in the preview.
            finder.List.SelectedIndices.Add(0);
            await WaitUntil(() => preview.Kind == PreviewKind.Text && ((TextBox)preview.View!).Text == "gamma text", "the result is previewed");
            await Task.Delay(300);
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "finder-and-preview.png")}");

            // Selecting in a pane hands the preview back to the pane.
            // Folders sort first: sub, alpha.txt, beta.txt.
            form.Panes[0].Browser.SelectItem(1);
            await WaitUntil(() => preview.Kind == PreviewKind.Text && ((TextBox)preview.View!).Text == "alpha text", "the pane's selection is previewed again");

            form.ToggleFinder();
            Assert.False(finder.Visible);
            Assert.False(form.FinderVisible);
            Assert.False(form.CaptureWorkspace().FinderVisible);
        });
    }
}
