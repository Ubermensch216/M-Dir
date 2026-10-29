using System.ComponentModel;
using System.Globalization;
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
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private Control? _view;
    private int _version;

    public PreviewPane()
    {
        _title.Font = new Font(Font, FontStyle.Bold);
        _imageBar.Items.AddRange(
        [
            CreateButton("맞춤", "창에 맞춤 (더블클릭: 100%와 전환)", (view) => view.FitToWindow()),
            CreateButton("100%", "실제 크기", (view) => view.ActualSize()),
            CreateButton("＋", "확대 (마우스 휠)", (view) => view.ZoomBy(1.25)),
            CreateButton("－", "축소 (마우스 휠)", (view) => view.ZoomBy(0.8)),
            CreateButton("↶", "왼쪽으로 회전", (view) => view.Rotate(clockwise: false)),
            CreateButton("↷", "오른쪽으로 회전", (view) => view.Rotate(clockwise: true)),
        ]);

        // Z-order: fill last, so the bars dock first (top to bottom: title, detail, image bar).
        Controls.Add(_content);
        Controls.Add(_imageBar);
        Controls.Add(_detail);
        Controls.Add(_title);
        _content.BringToFront();
    }

    public PreviewKind Kind { get; private set; }

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
            ShowMessage("선택한 항목이 없습니다.");
            return;
        }

        if (selectedCount > 1)
        {
            ShowMessage($"{selectedCount:N0}개 항목이 선택되었습니다.");
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
                ShowMessage("항목을 찾을 수 없습니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (version == _version)
                ShowMessage($"미리 볼 수 없습니다: {ex.Message}");
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
        SetView(null, PreviewKind.None);
        _title.Text = string.Empty;
        _detail.Text = string.Empty;
        _imageBar.Visible = false;
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
                SetHeader(name, $"{type} · {bitmap.Width}×{bitmap.Height} · {size} · 내장 뷰어");
                _imageBar.Visible = true;
                return;
            }

            ShowInfo(name, type, FileFacts(file, type), "이미지를 열 수 없습니다.");
            return;
        }

        if (TextFile.IsKnownText(path))
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
            ShowInfo(name, type, FileFacts(file, type), "이 형식은 미리 볼 수 없습니다.");
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

        string items = count < 0 ? "읽을 수 없음" : count > FolderCountLimit ? $"{FolderCountLimit:N0}개 이상" : $"{count:N0}개";
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
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            Font = new Font("Consolas", 10f),
            Text = data.Text,
        };
        box.Select(0, 0);
        SetView(box, PreviewKind.Text);
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
            table.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 3, 12, 3) }, 0, row);
            table.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(2000, 0), Margin = new Padding(3) }, 1, row);
        }

        SetView(table, PreviewKind.Info);
        SetHeader(name, type);
    }

    private static IReadOnlyList<(string, string)> FileFacts(FileInfo file, string type) =>
    [
        ("이름", file.Name),
        ("종류", type),
        ("크기", $"{FormatSize(file.Length)} ({file.Length:N0}바이트)"),
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
    private static Task<T?> LoadOrNull<T>(Func<T> load) where T : class =>
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
        string[] units = ["바이트", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0}{units[0]}" : $"{value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private ToolStripButton CreateButton(string text, string toolTip, Action<ImageView> action) =>
        new(text, null, (_, _) =>
        {
            if (_view is ImageView view)
                action(view);
        })
        { ToolTipText = toolTip, DisplayStyle = ToolStripItemDisplayStyle.Text };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _version++;
            SetView(null, PreviewKind.None);
        }

        base.Dispose(disposing);
    }
}
