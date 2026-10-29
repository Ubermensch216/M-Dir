namespace MDir.Features;

/// <summary>
/// Renames many files at once from a mask, with a live preview of every new name. Problems (invalid or duplicate
/// names, names already taken) are marked in red and those items are left alone. The last rename can be undone.
/// </summary>
internal sealed class MultiRenameDialog : Form
{
    private readonly IReadOnlyList<string> _paths;
    private readonly TextBox _nameMask = new() { Text = "[N]", Dock = DockStyle.Fill };
    private readonly TextBox _extensionMask = new() { Text = "[E]", Dock = DockStyle.Fill };
    private readonly NumericUpDown _start = new() { Minimum = 0, Maximum = 999999999, Value = 1, Width = 80 };
    private readonly NumericUpDown _step = new() { Minimum = 1, Maximum = 99999, Value = 1, Width = 60 };
    private readonly NumericUpDown _digits = new() { Minimum = 1, Maximum = 12, Value = 1, Width = 50 };
    private readonly TextBox _dateFormat = new() { Text = "yyyyMMdd", Width = 100 };
    private readonly TextBox _find = new() { Dock = DockStyle.Fill };
    private readonly TextBox _replace = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _ignoreCase = new() { Text = "대소문자 무시", Checked = true, AutoSize = true };
    private readonly CheckBox _regex = new() { Text = "정규식", AutoSize = true };
    private readonly ComboBox _nameCase = CreateCaseBox();
    private readonly ComboBox _extensionCase = CreateCaseBox();
    private readonly Button _apply = new() { Text = "이름 바꾸기", AutoSize = true };
    private readonly Button _undo = new() { Text = "되돌리기", AutoSize = true, Enabled = false };
    private readonly Label _summary = new() { AutoSize = true };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        UseCompatibleStateImageBehavior = false,
    };

    private IReadOnlyList<RenamePlanItem> _plan = [];
    private RenameResult? _lastResult;

    public MultiRenameDialog(IReadOnlyList<string> paths)
    {
        _paths = paths;
        Text = "여러 파일 이름 바꾸기";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(860, 620);
        MinimumSize = new Size(700, 480);

        _list.Columns.Add("현재 이름", 300);
        _list.Columns.Add("새 이름", 300);
        _list.Columns.Add("상태", 200);

        var counter = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        counter.Controls.AddRange([Caption("시작"), _start, Caption("증가"), _step, Caption("자릿수"), _digits, Caption("날짜 형식 [D]"), _dateFormat]);
        var cases = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        cases.Controls.AddRange([Caption("이름"), _nameCase, Caption("확장자"), _extensionCase]);
        var replaceOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        replaceOptions.Controls.AddRange([_ignoreCase, _regex]);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control control)
        {
            int row = grid.RowCount++;
            grid.Controls.Add(Caption(label), 0, row);
            grid.Controls.Add(control, 1, row);
        }

        Row("이름 마스크", _nameMask);
        Row("확장자 마스크", _extensionMask);
        Row("번호 [C]", counter);
        Row("찾기", _find);
        Row("바꾸기", _replace);
        Row(string.Empty, replaceOptions);
        Row("대소문자", cases);

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = SystemColors.GrayText,
            Text = "[N] 이름  [E] 확장자  [C] 번호 ([C:3] = 3자리)  [D] 수정한 날짜  [P] 상위 폴더 이름  [[ = 문자 '['",
        };

        var close = new Button { Text = "닫기", DialogResult = DialogResult.OK, AutoSize = true };
        AcceptButton = null;
        CancelButton = close;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([close, _undo, _apply]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(grid, 0, 0);
        layout.Controls.Add(hint, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(_summary, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);

        foreach (Control control in new Control[] { _nameMask, _extensionMask, _find, _replace, _dateFormat })
            control.TextChanged += (_, _) => UpdatePreview();
        foreach (NumericUpDown number in new[] { _start, _step, _digits })
            number.ValueChanged += (_, _) => UpdatePreview();
        foreach (CheckBox box in new[] { _ignoreCase, _regex })
            box.CheckedChanged += (_, _) => UpdatePreview();
        _nameCase.SelectedIndexChanged += (_, _) => UpdatePreview();
        _extensionCase.SelectedIndexChanged += (_, _) => UpdatePreview();
        _apply.Click += (_, _) => Apply();
        _undo.Click += (_, _) => UndoLast();

        UpdatePreview();
    }

    /// <summary>The options as set in the dialog.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal RenameOptions Options
    {
        get => new(_nameMask.Text, _extensionMask.Text, (int)_start.Value, (int)_step.Value, (int)_digits.Value, _dateFormat.Text,
            _find.Text, _replace.Text, _ignoreCase.Checked, _regex.Checked, (CaseMode)_nameCase.SelectedIndex, (CaseMode)_extensionCase.SelectedIndex);
        set
        {
            _nameMask.Text = value.NameMask;
            _extensionMask.Text = value.ExtensionMask;
            _start.Value = Math.Clamp(value.CounterStart, 0, (int)_start.Maximum);
            _step.Value = Math.Clamp(value.CounterStep, 1, (int)_step.Maximum);
            _digits.Value = Math.Clamp(value.CounterDigits, 1, (int)_digits.Maximum);
            _dateFormat.Text = value.DateFormat;
            _find.Text = value.Find;
            _replace.Text = value.Replace;
            _ignoreCase.Checked = value.IgnoreCase;
            _regex.Checked = value.UseRegex;
            _nameCase.SelectedIndex = (int)value.NameCase;
            _extensionCase.SelectedIndex = (int)value.ExtensionCase;
            UpdatePreview();
        }
    }

    internal IReadOnlyList<RenamePlanItem> Plan => _plan;

    internal ListView PreviewList => _list;

    internal bool CanApply => _apply.Enabled;

    internal bool CanUndo => _undo.Enabled;

    internal string SummaryText => _summary.Text;

    /// <summary>Renames the items that are ready. Items with problems are left as they are.</summary>
    internal RenameResult Apply()
    {
        RenameResult result = RenameExecutor.Apply(_plan);
        _lastResult = result;
        _undo.Enabled = result.Applied.Count > 0;

        // The renamed files have new names now: the next preview starts from those.
        var renamed = result.Applied.ToDictionary(pair => pair.From, pair => pair.To, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _currentPaths.Count; i++)
        {
            if (renamed.TryGetValue(_currentPaths[i], out string? to))
                _currentPaths[i] = to;
        }

        UpdatePreview();
        _summary.Text = $"{result.Applied.Count}개 이름을 바꿨습니다." + (result.Failed.Count > 0 ? $" 실패 {result.Failed.Count}개: {result.Failed[0].Error}" : string.Empty);
        Applied?.Invoke(this, EventArgs.Empty);
        return result;
    }

    internal RenameResult? UndoLast()
    {
        if (_lastResult is not { } last)
            return null;

        RenameResult undone = RenameExecutor.Undo(last);
        var back = undone.Applied.ToDictionary(pair => pair.From, pair => pair.To, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _currentPaths.Count; i++)
        {
            if (back.TryGetValue(_currentPaths[i], out string? to))
                _currentPaths[i] = to;
        }

        _lastResult = null;
        _undo.Enabled = false;
        UpdatePreview();
        _summary.Text = $"{undone.Applied.Count}개 이름을 되돌렸습니다." + (undone.Failed.Count > 0 ? $" 실패 {undone.Failed.Count}개" : string.Empty);
        Applied?.Invoke(this, EventArgs.Empty);
        return undone;
    }

    /// <summary>Raised after names changed on disk (a rename or an undo), so the file lists can refresh.</summary>
    internal event EventHandler? Applied;

    private List<string>? _pathsInUse;

    private List<string> _currentPaths => _pathsInUse ??= [.. _paths];

    private void UpdatePreview()
    {
        _plan = RenamePlanner.Plan(_currentPaths, Options);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (RenamePlanItem item in _plan)
        {
            var row = new ListViewItem(Path.GetFileName(item.Source));
            row.SubItems.Add(item.NewName);
            row.SubItems.Add(Describe(item.Status));
            row.ForeColor = item.Status switch
            {
                RenameStatus.Ready => SystemColors.WindowText,
                RenameStatus.Unchanged => SystemColors.GrayText,
                _ => Color.Firebrick,
            };
            _list.Items.Add(row);
        }

        _list.EndUpdate();
        int ready = _plan.Count(item => item.WillRename);
        int problems = _plan.Count(item => item.Status is not (RenameStatus.Ready or RenameStatus.Unchanged));
        _apply.Enabled = ready > 0;
        _summary.Text = $"바꿀 항목 {ready}개" + (problems > 0 ? $" · 문제 {problems}개 (건너뜀)" : string.Empty);
    }

    internal static string Describe(RenameStatus status) => status switch
    {
        RenameStatus.Ready => "변경",
        RenameStatus.Unchanged => "변경 없음",
        RenameStatus.InvalidName => "사용할 수 없는 이름",
        RenameStatus.DuplicateName => "새 이름이 겹침",
        RenameStatus.TargetExists => "같은 이름이 이미 있음",
        _ => string.Empty,
    };

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) };

    private static ComboBox CreateCaseBox()
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        box.Items.AddRange(["바꾸지 않음", "모두 소문자", "모두 대문자", "단어 첫 글자 대문자", "첫 글자만 대문자"]);
        box.SelectedIndex = 0;
        return box;
    }
}
