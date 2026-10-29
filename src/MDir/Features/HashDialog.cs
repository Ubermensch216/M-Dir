using System.Text;

namespace MDir.Features;

/// <summary>
/// Shows the checksums of the given files, computed in the background one after another. A checksum pasted into the
/// box (in any common format) is checked against every file, so a download can be verified at a glance.
/// </summary>
internal sealed class HashDialog : Form
{
    private const string Waiting = "대기 중";

    private readonly IReadOnlyList<string> _paths;
    private readonly IClipboardService _clipboard;
    private readonly string?[] _hashes;
    private readonly string[] _states;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        UseCompatibleStateImageBehavior = false,
    };

    private readonly ComboBox _algorithm = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly TextBox _expected = new() { Dock = DockStyle.Fill };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private CancellationTokenSource _cancel = new();
    private Task _running = Task.CompletedTask;

    public HashDialog(IReadOnlyList<string> paths, IClipboardService clipboard)
    {
        _paths = paths;
        _clipboard = clipboard;
        _hashes = new string?[paths.Count];
        _states = new string[paths.Count];
        Array.Fill(_states, Waiting);

        Text = "파일 해시";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 420);
        MinimumSize = new Size(640, 300);

        _list.Columns.Add("파일", 220);
        _list.Columns.Add("크기", 90);
        _list.Columns.Add("해시", 420);
        _list.Columns.Add("상태", 130);
        foreach (string path in paths)
        {
            var item = new ListViewItem(Path.GetFileName(path)) { Tag = path };
            item.SubItems.Add(SafeSize(path));
            item.SubItems.Add(string.Empty);
            item.SubItems.Add(Waiting);
            _list.Items.Add(item);
        }

        _algorithm.Items.AddRange([.. Enum.GetValues<HashAlgorithmKind>().Select(kind => (object)FileHasher.Name(kind))]);
        _algorithm.SelectedIndex = 0;

        var copy = new Button { Text = "해시 복사", AutoSize = true };
        var copyAll = new Button { Text = "모두 복사", AutoSize = true };
        var close = new Button { Text = "닫기", DialogResult = DialogResult.OK, AutoSize = true };
        copy.Click += (_, _) => CopySelected();
        copyAll.Click += (_, _) => CopyAll();
        AcceptButton = CancelButton = close;

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        top.Controls.Add(new Label { Text = "알고리즘", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        top.Controls.Add(_algorithm);
        top.Controls.Add(copy);
        top.Controls.Add(copyAll);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(top, 0, 0);
        layout.SetColumnSpan(top, 2);
        layout.Controls.Add(_list, 0, 1);
        layout.SetColumnSpan(_list, 2);
        layout.Controls.Add(new Label { Text = "비교할 해시", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_expected, 1, 2);
        layout.Controls.Add(_summary, 0, 3);
        layout.SetColumnSpan(_summary, 2);
        layout.Controls.Add(close, 1, 4);
        close.Anchor = AnchorStyles.Right;
        Controls.Add(layout);

        _algorithm.SelectedIndexChanged += (_, _) => Restart();
        _expected.TextChanged += (_, _) => RefreshStates();
        Shown += (_, _) => Restart();
        FormClosing += (_, _) => _cancel.Cancel();
    }

    internal HashAlgorithmKind Algorithm => (HashAlgorithmKind)_algorithm.SelectedIndex;

    internal IReadOnlyList<string?> Hashes => _hashes;

    internal IReadOnlyList<string> States => _states;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string ExpectedText
    {
        get => _expected.Text;
        set => _expected.Text = value;
    }

    internal void SetAlgorithm(HashAlgorithmKind kind) => _algorithm.SelectedIndex = (int)kind;

    /// <summary>Completes when every file has its checksum (or an error). Tests await it.</summary>
    internal Task Completion => _running;

    /// <summary>Starts over (after a change of algorithm): the running computation is cancelled first.</summary>
    internal void Restart()
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
        Array.Clear(_hashes);
        Array.Fill(_states, Waiting);
        _running = ComputeAllAsync(Algorithm, _cancel.Token);
    }

    /// <summary>One line per file, <c>hash *name</c> as <c>sha256sum</c> writes it (so the output can be verified with that tool).</summary>
    internal string AllText() => string.Join("\r\n", _paths.Select((path, i) => _hashes[i] is { } hash ? $"{hash} *{Path.GetFileName(path)}" : null).OfType<string>());

    internal void CopyAll()
    {
        if (AllText() is { Length: > 0 } text)
            _clipboard.SetText(text);
    }

    internal void CopySelected()
    {
        if (_list.SelectedIndices.Count > 0 && _hashes[_list.SelectedIndices[0]] is { } hash)
            _clipboard.SetText(hash);
    }

    private async Task ComputeAllAsync(HashAlgorithmKind kind, CancellationToken cancellation)
    {
        for (int i = 0; i < _paths.Count; i++)
        {
            int index = i;
            SetState(index, "계산 중…");
            try
            {
                string hash = await FileHasher.ComputeAsync(_paths[index], kind, new Progress<double>(value =>
                {
                    if (!cancellation.IsCancellationRequested && _hashes[index] is null)
                        SetState(index, $"계산 중 {value:P0}");
                }), cancellation);
                if (cancellation.IsCancellationRequested)
                    return;

                _hashes[index] = hash;
                _list.Items[index].SubItems[2].Text = hash;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _states[index] = "오류: " + ex.Message;
                _list.Items[index].SubItems[3].Text = _states[index];
                continue;
            }

            RefreshStates();
        }

        RefreshStates();
    }

    private void SetState(int index, string state)
    {
        _states[index] = state;
        if (index < _list.Items.Count)
            _list.Items[index].SubItems[3].Text = state;
    }

    /// <summary>Marks each finished file as matching or not when there is a checksum to compare with.</summary>
    private void RefreshStates()
    {
        bool comparing = FileHasher.ExtractHash(_expected.Text) is not null;
        int matches = 0, mismatches = 0, pending = 0;
        for (int i = 0; i < _paths.Count; i++)
        {
            if (_hashes[i] is not { } hash)
            {
                if (!_states[i].StartsWith("오류")) pending++;
                continue;
            }

            string state = !comparing ? "완료" : FileHasher.Matches(hash, _expected.Text) ? "✔ 일치" : "✘ 불일치";
            if (comparing)
            {
                if (state.StartsWith('✔')) matches++; else mismatches++;
            }

            SetState(i, state);
            _list.Items[i].BackColor = !comparing ? SystemColors.Window : state.StartsWith('✔') ? Color.FromArgb(220, 245, 220) : Color.FromArgb(250, 220, 220);
        }

        _summary.Text = comparing
            ? $"일치 {matches}개 · 불일치 {mismatches}개{(pending > 0 ? $" · 계산 중 {pending}개" : string.Empty)}"
            : _expected.TextLength > 0 ? "붙여넣은 글에서 해시를 찾지 못했습니다." : string.Empty;
    }

    private static string SafeSize(string path)
    {
        try
        {
            return PreviewPane.FormatSize(new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cancel.Cancel();

        base.Dispose(disposing);
    }
}
