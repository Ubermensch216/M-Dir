namespace MDir.Features;

/// <summary>
/// Compares two folders and lists what differs. Entries can be copied to the other side one direction at a time, or
/// both ways at once (newer version wins, nothing is ever deleted). The shell asks before overwriting.
/// </summary>
internal sealed class CompareDialog : Form
{
    private readonly string _left;
    private readonly string _right;
    private readonly CheckBox _recursive = new() { Text = "하위 폴더 포함", Checked = true, AutoSize = true };
    private readonly CheckBox _content = new() { Text = "내용 비교 (느림)", AutoSize = true };
    private readonly CheckBox _showSame = new() { Text = "같은 항목도 표시", AutoSize = true };
    private readonly Button _compare = new() { Text = "비교", AutoSize = true };
    private readonly Label _summary = new() { AutoSize = true };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        UseCompatibleStateImageBehavior = false,
    };

    private CancellationTokenSource? _cancel;
    private CompareResult _result = new([], []);

    public CompareDialog(string left, string right)
    {
        _left = left;
        _right = right;
        Text = "폴더 비교";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(1000, 620);
        MinimumSize = new Size(780, 420);

        _list.Columns.Add("상태", 120);
        _list.Columns.Add("경로", 330);
        _list.Columns.Add("왼쪽", 200);
        _list.Columns.Add("오른쪽", 200);

        var toLeft = new Button { Text = "← 왼쪽으로 복사", AutoSize = true };
        var toRight = new Button { Text = "오른쪽으로 복사 →", AutoSize = true };
        var twoWay = new Button { Text = "양방향 동기화", AutoSize = true };
        var close = new Button { Text = "닫기", DialogResult = DialogResult.OK, AutoSize = true };
        toLeft.Click += (_, _) => _ = CopySelectedAsync(SyncDirection.RightToLeft);
        toRight.Click += (_, _) => _ = CopySelectedAsync(SyncDirection.LeftToRight);
        twoWay.Click += (_, _) => _ = SynchronizeAsync();
        _compare.Click += (_, _) => _ = CompareAsync();
        AcceptButton = null;
        CancelButton = close;

        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        paths.Controls.Add(new Label { Text = "왼쪽: " + left, AutoSize = true, AutoEllipsis = true }, 0, 0);
        paths.Controls.Add(new Label { Text = "오른쪽: " + right, AutoSize = true, AutoEllipsis = true }, 1, 0);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        options.Controls.AddRange([_recursive, _content, _showSame, _compare]);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange([close, twoWay, toRight, toLeft]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(paths, 0, 0);
        layout.Controls.Add(options, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(_summary, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);

        Shown += (_, _) => _ = CompareAsync();
        FormClosing += (_, _) => _cancel?.Cancel();
    }

    /// <summary>Makes the shell copy engine; tests replace it with a quiet one.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<FileOperations> CreateOperations { get; set; } = () => new FileOperations();

    /// <summary>Raised after files were copied, so the file lists can refresh.</summary>
    internal event EventHandler? FilesChanged;

    internal CompareResult Result => _result;

    internal ListView ResultList => _list;

    internal string SummaryText => _summary.Text;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Recursive { get => _recursive.Checked; set => _recursive.Checked = value; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ContentMode { get => _content.Checked; set => _content.Checked = value; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ShowSame { get => _showSame.Checked; set => _showSame.Checked = value; }

    internal async Task CompareAsync()
    {
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _compare.Enabled = false;
        _summary.Text = "비교 중…";
        try
        {
            var options = new CompareOptions(_recursive.Checked, _content.Checked ? CompareMode.Content : CompareMode.Quick, _showSame.Checked);

            // Progress reports are posted to this thread and can arrive after the result: they must not overwrite it.
            bool finished = false;
            var progress = new Progress<string>(folder =>
            {
                if (!finished && !cancel.IsCancellationRequested)
                    _summary.Text = "비교 중: " + folder;
            });
            CompareResult result = await FolderComparer.CompareAsync(_left, _right, options, progress, cancel.Token);
            finished = true;
            if (cancel.IsCancellationRequested)
                return;

            _result = result;
            Fill(result);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_cancel == cancel)
                _compare.Enabled = true;
        }
    }

    /// <summary>Copies the selected entries (all differing ones when nothing is selected) in one direction, overwriting after the shell's question.</summary>
    internal async Task CopySelectedAsync(SyncDirection direction)
    {
        CompareEntry[] chosen = _list.SelectedItems.Count > 0
            ? [.. _list.SelectedItems.Cast<ListViewItem>().Select(item => (CompareEntry)item.Tag!)]
            : [.. _result.Entries];
        await CopyAsync(SyncPlanner.PlanSelected(chosen, _left, _right, direction));
    }

    /// <summary>Makes the sides match both ways: new items cross over and the newer version of a changed file wins.</summary>
    internal async Task SynchronizeAsync() =>
        await CopyAsync(SyncPlanner.Plan(_result.Entries, _left, _right, SyncDirection.TwoWay));

    private async Task CopyAsync(IReadOnlyList<SyncCopy> copies)
    {
        if (copies.Count == 0)
        {
            _summary.Text = "복사할 항목이 없습니다.";
            return;
        }

        FileOperations operations = CreateOperations();
        var errors = new List<string>();

        // The shell shows progress and asks about overwriting; its calls belong on this thread.
        foreach (IGrouping<string, SyncCopy> group in copies.GroupBy(copy => copy.DestinationFolder, StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(group.Key);
            FileOperationResult result = operations.Copy([.. group.Select(copy => copy.Source)], group.Key);
            if (!result.Success && !result.Aborted && result.Error is { } error)
                errors.Add(error);
        }

        FilesChanged?.Invoke(this, EventArgs.Empty);
        await CompareAsync();
        if (errors.Count > 0)
            _summary.Text += $" · 복사 오류 {errors.Count}개: {errors[0]}";
    }

    private void Fill(CompareResult result)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (CompareEntry entry in result.Entries)
        {
            var item = new ListViewItem(Describe(entry.Status)) { Tag = entry };
            item.SubItems.Add(entry.RelativePath + (entry.IsFolder ? "\\" : string.Empty));
            item.SubItems.Add(Side(entry.Left));
            item.SubItems.Add(Side(entry.Right));
            item.ForeColor = entry.Status switch
            {
                CompareStatus.Same => SystemColors.GrayText,
                CompareStatus.OnlyLeft or CompareStatus.LeftNewer => Color.RoyalBlue,
                CompareStatus.OnlyRight or CompareStatus.RightNewer => Color.SeaGreen,
                _ => Color.Firebrick,
            };
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        _summary.Text = result.Entries.Count == 0
            ? "두 폴더가 같습니다."
            : $"왼쪽에만 {result.Count(CompareStatus.OnlyLeft)} · 오른쪽에만 {result.Count(CompareStatus.OnlyRight)} · 왼쪽이 최신 {result.Count(CompareStatus.LeftNewer)} · 오른쪽이 최신 {result.Count(CompareStatus.RightNewer)} · 다름 {result.Count(CompareStatus.Different)} · 종류 불일치 {result.Count(CompareStatus.TypeMismatch)}"
                + (result.Errors.Count > 0 ? $" · 읽지 못한 곳 {result.Errors.Count}" : string.Empty);
    }

    private static string Side(FileSystemInfo? item) => item switch
    {
        null => "—",
        DirectoryInfo directory => "<폴더> " + directory.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
        FileInfo file => $"{PreviewPane.FormatSize(file.Length)}  {file.LastWriteTime:yyyy-MM-dd HH:mm}",
        _ => string.Empty,
    };

    internal static string Describe(CompareStatus status) => status switch
    {
        CompareStatus.Same => "같음",
        CompareStatus.OnlyLeft => "왼쪽에만",
        CompareStatus.OnlyRight => "오른쪽에만",
        CompareStatus.LeftNewer => "왼쪽이 최신",
        CompareStatus.RightNewer => "오른쪽이 최신",
        CompareStatus.Different => "내용 다름",
        CompareStatus.TypeMismatch => "종류가 다름",
        _ => string.Empty,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cancel?.Cancel();

        base.Dispose(disposing);
    }
}
