using System.ComponentModel;
using System.Globalization;
using MDir.Localization;
using MDir.Features.Hwp;
using MDir.Shell;

namespace MDir.Features;

/// <summary>What the preview pane is showing.</summary>
internal enum PreviewKind
{
    /// <summary>Nothing (the pane is cleared).</summary>
    None,

    /// <summary>A sentence: no selection, several items selected, an error.</summary>
    Message,

    /// <summary>Facts about a file or folder that cannot be previewed.</summary>
    Info,

    /// <summary>The built-in image viewer.</summary>
    Image,

    /// <summary>The built-in text viewer.</summary>
    Text,

    /// <summary>A shell preview handler (Office, PDF, HTML...).</summary>
    Handler,

    /// <summary>The media player (video and audio files).</summary>
    Media,

    /// <summary>The built-in Hancom (.hwp/.hwpx) document viewer.</summary>
    Document,
}

/// <summary>
/// The preview of the selected item. Images and plain-text files use built-in viewers (zoom and rotate; encoding
/// detection); other file types are shown by the shell's preview handler when one is registered, like Explorer's
/// preview pane; anything else gets a page of facts. Loading is asynchronous and a newer selection cancels an
/// older one still loading.
/// </summary>
internal sealed class PreviewPane : Panel
{
    /// <summary>Images larger than this are not decoded (they would stall the window); they get the info page.</summary>
    private const long MaxImageBytes = 64L * 1024 * 1024;
    private const int FolderCountLimit = 100_000;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".emf", ".wmf",
    };

    private readonly Label _title = new() { Dock = DockStyle.Top, AutoSize = false, Height = 22, Padding = new Padding(6, 3, 6, 0), AutoEllipsis = true };
    private readonly Label _detail = new() { Dock = DockStyle.Top, AutoSize = false, Height = 20, Padding = new Padding(6, 0, 6, 0), AutoEllipsis = true, ForeColor = SystemColors.GrayText };
    private readonly ToolStrip _imageBar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Visible = false, CanOverflow = false };
    private readonly ToolStrip _textBar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Visible = false, CanOverflow = false };
    private readonly ToolStrip _documentBar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Visible = false, CanOverflow = false };
    private readonly ToolStripLabel _pageLabel = new() { AutoSize = true };
    private readonly ToolStripButton _wrapButton = new("줄 바꿈") { CheckOnClick = true, ToolTipText = "긴 줄을 창 너비에서 줄 바꿈", DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripTextBox _findBox = new() { AutoSize = false, Width = 150, ToolTipText = "찾을 글 (Enter: 다음, Shift+Enter: 이전)" };
    private readonly ToolStripButton _slideshowButton = new("슬라이드쇼") { CheckOnClick = true, ToolTipText = "몇 초마다 다음 그림을 보여 줍니다", DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly Panel _content = new() { Dock = DockStyle.Fill };

    /// <summary>The line between the title block and the preview.</summary>
    private readonly Panel _headerLine = new() { Dock = DockStyle.Top, Height = 1 };
    private Color _frameColor = SystemColors.ControlDark;
    private Control? _view;
    private RhwpJob? _rhwpJob;
    private int _version;

    public PreviewPane()
    {
        // A 1 px outline is drawn in the padding, so the pane stands out from the file lists and the M-Finder panel beside it.
        Padding = new Padding(1);
        ApplyTheme();

        _title.Font = new Font(Font, FontStyle.Bold);
        var findPrevious = new ToolStripButton("▲", null, (_, _) => FindNext(_findBox.Text, forward: false)) { ToolTipText = "이전 찾기", DisplayStyle = ToolStripItemDisplayStyle.Text };
        var findNext = new ToolStripButton("▼", null, (_, _) => FindNext(_findBox.Text, forward: true)) { ToolTipText = "다음 찾기", DisplayStyle = ToolStripItemDisplayStyle.Text };
        _wrapButton.CheckedChanged += (_, _) => SetWordWrap(_wrapButton.Checked);
        _findBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                FindNext(_findBox.Text, forward: !e.Shift);
                e.Handled = e.SuppressKeyPress = true;
            }
        };
        _textBar.Items.AddRange([_wrapButton, new ToolStripSeparator(), new ToolStripLabel("찾기"), _findBox, findPrevious, findNext]);
        _slideshowButton.CheckedChanged += (_, _) => SlideshowChanged?.Invoke(this, EventArgs.Empty);
        _imageBar.Items.AddRange(
        [
            new ToolStripButton("◀", null, (_, _) => NavigateRequested?.Invoke(this, -1)) { ToolTipText = "이전 항목 (←)", DisplayStyle = ToolStripItemDisplayStyle.Text },
            new ToolStripButton("▶", null, (_, _) => NavigateRequested?.Invoke(this, 1)) { ToolTipText = "다음 항목 (→)", DisplayStyle = ToolStripItemDisplayStyle.Text },
            _slideshowButton,
            new ToolStripSeparator(),
            CreateButton("맞춤", "창에 맞춤 (더블클릭: 100%와 전환)", (view) => view.FitToWindow()),
            CreateButton("100%", "실제 크기", (view) => view.ActualSize()),
            CreateButton("＋", "확대 (마우스 휠)", (view) => view.ZoomBy(1.25)),
            CreateButton("－", "축소 (마우스 휠)", (view) => view.ZoomBy(0.8)),
            CreateButton("↶", "왼쪽으로 회전", (view) => view.Rotate(clockwise: false)),
            CreateButton("↷", "오른쪽으로 회전", (view) => view.Rotate(clockwise: true)),
        ]);

        _documentBar.Items.AddRange(
        [
            CreateDocumentButton("◀", "이전 쪽 (PageUp)", view => view.StepPage(-1)),
            _pageLabel,
            CreateDocumentButton("▶", "다음 쪽 (PageDown)", view => view.StepPage(1)),
            new ToolStripSeparator(),
            CreateDocumentButton("맞춤", "창 너비에 맞춤", view => view.FitToWindow()),
            CreateDocumentButton("100%", "실제 크기", view => view.ActualSize()),
            CreateDocumentButton("＋", "확대 (Ctrl+마우스 휠)", view => view.ZoomBy(1.25)),
            CreateDocumentButton("－", "축소 (Ctrl+마우스 휠)", view => view.ZoomBy(0.8)),
        ]);

        // Z-order: fill last, so the bars dock first (top to bottom: title, detail, image bar, text bar, document bar).
        Controls.Add(_content);
        Controls.Add(_documentBar);
        Controls.Add(_textBar);
        Controls.Add(_imageBar);
        Controls.Add(_headerLine);
        Controls.Add(_detail);
        Controls.Add(_title);
        _content.BringToFront();
    }

    /// <summary>
    /// Colours the frame and the title block from the window's current background and text colours, so they suit both
    /// the light and the dark theme: the outline is a clear step towards the text colour, the title block a faint one.
    /// </summary>
    public void ApplyTheme()
    {
        _frameColor = Mix(BackColor, ForeColor, 0.30);
        Color header = Mix(BackColor, ForeColor, 0.07);
        _title.BackColor = _detail.BackColor = header;
        _headerLine.BackColor = _frameColor;
        Invalidate();
    }

    internal Color FrameColor => _frameColor;

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        from.R + (int)((to.R - from.R) * amount),
        from.G + (int)((to.G - from.G) * amount),
        from.B + (int)((to.B - from.B) * amount));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_frameColor);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Invalidate();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        ApplyTheme();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        ApplyTheme();
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
    }

    /// <summary>Raised with -1 or +1 when the user asks for the previous or next item (the window moves the selection).</summary>
    public event EventHandler<int>? NavigateRequested;

    /// <summary>Raised when the slideshow button was switched.</summary>
    public event EventHandler? SlideshowChanged;

    /// <summary>The slideshow button is on: the window shows the next image every few seconds.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SlideshowActive
    {
        get => _slideshowButton.Checked;
        set => _slideshowButton.Checked = value;
    }

    internal bool TextBarVisible => _textBar.Visible;

    internal bool WordWrapOn => _view is TextBox { WordWrap: true };

    /// <summary>True for the image types the built-in viewer opens (by file extension).</summary>
    internal static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>Wraps long lines of the text viewer at the window width, or scrolls them sideways.</summary>
    public void SetWordWrap(bool wrap)
    {
        if (_wrapButton.Checked != wrap)
            _wrapButton.Checked = wrap;

        if (_view is TextBox box)
        {
            box.WordWrap = wrap;
            box.ScrollBars = wrap ? ScrollBars.Vertical : ScrollBars.Both;
        }
    }

    /// <summary>
    /// Selects the next (or previous) occurrence of <paramref name="text"/> in the text viewer, ignoring case, and
    /// wraps around at the end. Returns false when there is no text viewer or the text does not occur.
    /// </summary>
    public bool FindNext(string text, bool forward)
    {
        if (_view is not TextBox box || text.Length == 0)
            return false;

        string content = box.Text;
        int index = forward
            ? content.IndexOf(text, Math.Min(box.SelectionStart + box.SelectionLength, content.Length), StringComparison.OrdinalIgnoreCase)
            : content.AsSpan(0, Math.Min(box.SelectionStart, content.Length)).LastIndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            // Nothing further on: start again from the other end.
            index = forward ? content.IndexOf(text, StringComparison.OrdinalIgnoreCase) : content.LastIndexOf(text, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;
        }

        box.Select(index, text.Length);
        box.ScrollToCaret();
        return true;
    }

    public PreviewKind Kind { get; private set; }

    /// <summary>Use <c>rhwp-preview.exe</c> when available; tests may set false to exercise the HTML fallback.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool UseRhwp { get; set; } = true;

    /// <summary>Override cache folder for <see cref="RhwpDocument"/> (tests).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string? RhwpCacheFolder { get; set; }

    /// <summary>The control showing the preview (an <see cref="ImageView"/>, a text box, a handler host...), or null.</summary>
    internal Control? View => _view;

    public string Title => _title.Text;

    /// <summary>The line under the title: kind, size, how it is shown.</summary>
    public string Detail => _detail.Text;

    /// <summary>
    /// Previews the item. <paramref name="selectedCount"/> is how many items are selected (the preview needs
    /// exactly one; <paramref name="item"/> is the first selected item).
    /// </summary>
    public async Task ShowAsync(ShellLocation? item, int selectedCount)
    {
        int version = ++_version;
        ClearView();

        if (selectedCount == 0 || item is null)
        {
            ShowMessage(Loc.T("선택한 항목이 없습니다."));
            return;
        }

        if (selectedCount > 1)
        {
            ShowMessage(Loc.F("{0:N0}개 항목이 선택되었습니다.", selectedCount));
            return;
        }

        string? path = item.FileSystemPath;
        if (path is null)
        {
            ShowInfo(item.Name, item.GetTypeName() ?? "항목", [("이름", item.DisplayName)]);
            return;
        }

        try
        {
            if (Directory.Exists(path))
                await ShowFolderAsync(item, path, version);
            else if (File.Exists(path))
                await ShowFileAsync(item, path, version);
            else
                ShowMessage(Loc.T("항목을 찾을 수 없습니다."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (version == _version)
                ShowMessage(Loc.F("미리 볼 수 없습니다: {0}", ex.Message));
        }
    }

    /// <summary>Unloads whatever is shown (a preview handler keeps the file open until then) and cancels a load in progress.</summary>
    public void Clear()
    {
        _version++;
        ClearView();
    }

    private void ClearView()
    {
        _rhwpJob?.Cancel();
        _rhwpJob = null;
        SetView(null, PreviewKind.None);
        _title.Text = string.Empty;
        _detail.Text = string.Empty;
        _imageBar.Visible = false;
        _textBar.Visible = false;
        _documentBar.Visible = false;
    }

    private async Task ShowFileAsync(ShellLocation item, string path, int version)
    {
        var file = new FileInfo(path);
        string size = FormatSize(file.Length);
        string name = file.Name;
        string type = item.GetTypeName() ?? "파일";

        if (ImageExtensions.Contains(file.Extension) && file.Length <= MaxImageBytes)
        {
            Bitmap? bitmap = await LoadOrNull(() => ImageView.Load(path));
            if (version != _version)
            {
                bitmap?.Dispose();
                return;
            }

            if (bitmap is not null)
            {
                var view = new ImageView { Dock = DockStyle.Fill };
                view.SetImage(bitmap);
                SetView(view, PreviewKind.Image);
                SetHeader(name, Loc.F("{0} · {1}×{2} · {3} · 내장 뷰어", type, bitmap.Width, bitmap.Height, size));
                _imageBar.Visible = true;
                return;
            }

            ShowInfo(name, type, FileFacts(file, type), Loc.T("이미지를 열 수 없습니다."));
            return;
        }

        // Hancom documents are rendered page-accurately by rhwp-preview when available; files it cannot open (or
        // when the helper is absent) fall back to the built-in HWP/HWPX reader, then to the shell preview handler.
        if (HwpDocument.IsHwpPath(path))
        {
            if (UseRhwp)
            {
                var job = _rhwpJob = new RhwpJob();
                string? cacheFolder = RhwpCacheFolder;
                RhwpDocument? rhwp = await LoadOrNull(() => RhwpDocument.Open(path, job, cacheFolder));
                if (version != _version)
                {
                    rhwp?.Dispose();
                    job.Dispose();
                    return;
                }

                if (rhwp is not null)
                {
                    _rhwpJob = null;
                    var view = new RhwpView(rhwp, job) { Dock = DockStyle.Fill };
                    view.PageChanged += (_, _) => UpdateDocumentBar(view);
                    SetView(view, PreviewKind.Document);
                    UpdateDocumentBar(view);
                    _documentBar.Visible = true;
                    SetHeader(name, $"{type} · {size} · {Loc.F("{0}쪽 · ", rhwp.PageCount)}{Loc.T("내장 한글 뷰어")}");
                    return;
                }

                job.Dispose();
                if (ReferenceEquals(_rhwpJob, job))
                    _rhwpJob = null;
            }

            HwpPreview? hwp = await LoadOrNull(() => HwpDocument.Preview(path));
            if (version != _version)
                return;

            if (hwp is not null)
            {
                SetView(new HwpView(hwp) { Dock = DockStyle.Fill }, PreviewKind.Document);
                string sheets = hwp.Sheets > 1 ? Loc.F("{0}쪽 구획 · ", hwp.Sheets) : string.Empty;
                SetHeader(name, $"{type} · {size} · {sheets}내장 한글 뷰어{(hwp.Truncated ? " · 앞부분만 표시" : string.Empty)}");
                return;
            }
        }

        if (MediaView.IsMedia(path))
        {
            var player = new MediaView { Dock = DockStyle.Fill };
            SetView(player, PreviewKind.Media);
            if (player.TryOpen(path))
            {
                SetHeader(name, Loc.F("{0} · {1} · 미디어 플레이어 (재생 단추로 시작)", type, size));
                return;
            }

            SetView(null, PreviewKind.None);
        }
        else if (TextFile.IsKnownText(path))
        {
            TextPreviewData? text = await LoadOrNull(() => TextFile.Read(path));
            if (version != _version)
                return;

            if (text is { IsBinary: false })
            {
                ShowText(name, type, size, text);
                return;
            }
        }
        else if (PreviewHandlerHost.HasHandler(path))
        {
            // Handlers must be driven from this (UI) thread.
            var host = new PreviewHandlerHost { Dock = DockStyle.Fill };
            SetView(host, PreviewKind.Handler);
            if (host.TryShow(path))
            {
                SetHeader(name, $"{type} · {size} · {host.HandlerName ?? "미리보기 처리기"}");
                return;
            }

            SetView(null, PreviewKind.None);
        }
        else if (await LoadOrNull(() => TextFile.Read(path)) is { IsBinary: false } sniffed && version == _version)
        {
            // Unknown extension, but the content is text.
            ShowText(name, type, size, sniffed);
            return;
        }

        if (version == _version)
            ShowInfo(name, type, FileFacts(file, type), Loc.T("이 형식은 미리 볼 수 없습니다."));
    }

    private async Task ShowFolderAsync(ShellLocation item, string path, int version)
    {
        string type = item.GetTypeName() ?? "폴더";
        var directory = new DirectoryInfo(path);
        int count = await Task.Run(() =>
        {
            try
            {
                return directory.EnumerateFileSystemInfos().Take(FolderCountLimit + 1).Count();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return -1;
            }
        });
        if (version != _version)
            return;

        string items = count < 0 ? Loc.T("읽을 수 없음") : count > FolderCountLimit ? Loc.F("{0:N0}개 이상", FolderCountLimit) : Loc.F("{0:N0}개", count);
        ShowInfo(directory.Name.Length > 0 ? directory.Name : path, type,
        [
            ("이름", directory.Name.Length > 0 ? directory.Name : path),
            ("종류", type),
            ("항목", items),
            ("수정한 날짜", directory.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)),
            ("위치", path),
        ]);
    }

    private void ShowText(string name, string type, string size, TextPreviewData data)
    {
        bool wrap = _wrapButton.Checked;
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = wrap,
            ScrollBars = wrap ? ScrollBars.Vertical : ScrollBars.Both,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            HideSelection = false,
            Font = new Font("Consolas", 10f),
            Text = data.Text,
        };
        box.Select(0, 0);
        SetView(box, PreviewKind.Text);
        _textBar.Visible = true;
        SetHeader(name, $"{type} · {size} · {data.EncodingName}{(data.Truncated ? $" · 앞부분 {FormatSize(TextFile.DefaultLimit)}만 표시" : string.Empty)} · 내장 뷰어");
    }

    private void ShowMessage(string message)
    {
        var label = new Label { Dock = DockStyle.Fill, Text = message, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText };
        SetView(label, PreviewKind.Message);
    }

    private void ShowInfo(string name, string type, IReadOnlyList<(string Label, string Value)> facts, string? note = null)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(10) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (note is not null)
        {
            table.RowCount++;
            var noteLabel = new Label { Text = note, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 3, 3, 10) };
            table.Controls.Add(noteLabel, 0, 0);
            table.SetColumnSpan(noteLabel, 2);
        }

        foreach ((string label, string value) in facts)
        {
            int row = table.RowCount++;
            table.Controls.Add(new Label { Text = Loc.T(label), AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 3, 12, 3) }, 0, row);
            table.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(2000, 0), Margin = new Padding(3) }, 1, row);
        }

        SetView(table, PreviewKind.Info);
        SetHeader(name, type);
    }

    private static IReadOnlyList<(string, string)> FileFacts(FileInfo file, string type) =>
    [
        ("이름", file.Name),
        ("종류", type),
        ("크기", Loc.F("{0} ({1:N0}바이트)", FormatSize(file.Length), file.Length)),
        ("수정한 날짜", file.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)),
        ("위치", file.DirectoryName ?? string.Empty),
    ];

    private void SetHeader(string title, string detail)
    {
        _title.Text = title;
        _detail.Text = detail;
    }

    private void SetView(Control? view, PreviewKind kind)
    {
        if (_view is { } old)
        {
            _content.Controls.Remove(old);

            // A handler host unloads the preview (and releases the file) when disposed.
            old.Dispose();
        }

        _view = view;
        Kind = kind;
        if (view is not null)
            _content.Controls.Add(view);
    }

    /// <summary>Runs a load on a worker thread; failures that mean "cannot be shown" become null.</summary>
    private static Task<T?> LoadOrNull<T>(Func<T?> load) where T : class =>
        Task.Run(() =>
        {
            try
            {
                return load();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException
                or System.Runtime.InteropServices.ExternalException)
            {
                return null;
            }
        });

    internal static string FormatSize(long bytes)
    {
        string[] units = [Loc.T("바이트"), "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0}{units[0]}" : $"{value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private void UpdateDocumentBar(RhwpView view) =>
        _pageLabel.Text = Loc.F("{0} / {1}쪽", view.CurrentPage + 1, view.PageCount);

    private ToolStripButton CreateButton(string text, string toolTip, Action<ImageView> action) =>
        new(text, null, (_, _) =>
        {
            if (_view is ImageView view)
                action(view);
        })
        { ToolTipText = toolTip, DisplayStyle = ToolStripItemDisplayStyle.Text };

    private ToolStripButton CreateDocumentButton(string text, string toolTip, Action<RhwpView> action) =>
        new(text, null, (_, _) =>
        {
            if (_view is RhwpView view)
                action(view);
        })
        { ToolTipText = toolTip, DisplayStyle = ToolStripItemDisplayStyle.Text };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _version++;
            _rhwpJob?.Cancel();
            _rhwpJob?.Dispose();
            _rhwpJob = null;
            SetView(null, PreviewKind.None);
        }

        base.Dispose(disposing);
    }
}
