using MDir.Settings;

namespace MDir.Features;

/// <summary>
/// Edits the colour rules: an ordered list (check box = enabled, order = priority) with the selected rule's pattern,
/// text colour, background colour and bold below it. Rules are previewed in their own colours in the list.
/// Import reads Q-Dir's <c>[Filter2]</c> table or M-Dir's own format; export writes either.
/// </summary>
internal sealed class ColorFilterDialog : Form
{
    private const string IniFilter = "INI 파일 (*.ini;*.mdr)|*.ini;*.mdr|모든 파일 (*.*)|*.*";

    private readonly List<RuleModel> _rules;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        UseCompatibleStateImageBehavior = false,
    };

    private readonly TextBox _pattern = new() { Dock = DockStyle.Fill };
    private readonly Button _textColor = new() { Text = "글자색…", Dock = DockStyle.Fill };
    private readonly Button _backColor = new() { Text = "배경색…", Dock = DockStyle.Fill };
    private readonly Button _clearText = new() { Text = "없음", AutoSize = true };
    private readonly Button _clearBack = new() { Text = "없음", AutoSize = true };
    private readonly CheckBox _bold = new() { Text = "굵게", AutoSize = true };
    private bool _loading;
    private int _selected = -1;

    public ColorFilterDialog(ColorRuleSet rules)
    {
        _rules = [.. rules.Rules.Select(RuleModel.From)];
        Text = "색상 필터 편집";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(680, 560);
        MinimumSize = new Size(560, 480);

        _list.Columns.Add("패턴", 260);
        _list.Columns.Add("글자색", 80);
        _list.Columns.Add("배경색", 80);
        _list.Columns.Add("굵게", 50);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        buttons.Controls.Add(CreateButton("추가", (_, _) => AddRule("*.new")));
        buttons.Controls.Add(CreateButton("삭제", (_, _) => DeleteSelected()));
        buttons.Controls.Add(CreateButton("위로", (_, _) => MoveSelected(-1)));
        buttons.Controls.Add(CreateButton("아래로", (_, _) => MoveSelected(1)));
        buttons.Controls.Add(CreateButton("기본값", (_, _) => ResetToDefaults()));
        buttons.Controls.Add(CreateButton("가져오기…", (_, _) => ImportFromFile()));
        buttons.Controls.Add(CreateButton("내보내기…", (_, _) => ExportToFile()));

        var details = new GroupBox { Text = "선택한 규칙", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 4 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "패턴", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        grid.Controls.Add(_pattern, 1, 0);
        grid.SetColumnSpan(_pattern, 2);
        grid.Controls.Add(new Label { Text = "글자", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        grid.Controls.Add(_textColor, 1, 1);
        grid.Controls.Add(_clearText, 2, 1);
        grid.Controls.Add(new Label { Text = "배경", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        grid.Controls.Add(_backColor, 1, 2);
        grid.Controls.Add(_clearBack, 2, 2);
        grid.Controls.Add(_bold, 1, 3);
        details.Controls.Add(grid);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Text = "패턴은 ; 로 구분한 와일드카드입니다. #DIR = 폴더, #Hidden = 숨김 항목. 위쪽 규칙이 먼저 적용되고, 비어 있는 속성만 아래쪽 규칙이 채웁니다. "
                + "#BG / #BG-A 는 목록 배경색(비활성 / 활성 패널)이며 글자색 칸이 배경색입니다.",
        };

        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        var closing = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        closing.Controls.Add(cancel);
        closing.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_list, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(details, 0, 2);
        layout.Controls.Add(hint, 0, 3);
        layout.Controls.Add(closing, 0, 4);
        Controls.Add(layout);

        _list.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _list.SelectedIndices.Count > 0)
            {
                _selected = _list.SelectedIndices[0];
                LoadSelected();
            }
        };
        _list.ItemChecked += OnItemChecked;
        _pattern.TextChanged += (_, _) => EditSelected(rule => rule.Pattern = _pattern.Text);
        _textColor.Click += (_, _) => PickColor(rule => rule.Text, (rule, color) => rule.Text = color);
        _backColor.Click += (_, _) => PickColor(rule => rule.Background, (rule, color) => rule.Background = color);
        _clearText.Click += (_, _) => EditSelected(rule => rule.Text = null);
        _clearBack.Click += (_, _) => EditSelected(rule => rule.Background = null);
        _bold.CheckedChanged += (_, _) => EditSelected(rule => rule.Bold = _bold.Checked);

        Rebuild(selectIndex: 0);
    }

    /// <summary>The rules as edited so far (what OK would return).</summary>
    public ColorRuleSet Result
    {
        get
        {
            // The check boxes are the truth for "enabled" (their change event needs a window handle).
            for (int i = 0; i < _rules.Count && i < _list.Items.Count; i++)
                _rules[i].Enabled = _list.Items[i].Checked;

            return new ColorRuleSet(_rules.Select(rule => rule.ToRule()));
        }
    }

    internal int SelectedIndex => _selected;

    internal int RuleCount => _rules.Count;

    internal ListView RuleList => _list;

    internal void SelectRule(int index)
    {
        if (index < 0 || index >= _rules.Count)
            return;

        // Tracked here too: the list only reports selection once it has a window handle.
        _selected = index;
        _list.Items[index].Selected = true;
        _list.Items[index].EnsureVisible();
        LoadSelected();
    }

    internal void AddRule(string pattern)
    {
        int at = SelectedIndex >= 0 ? SelectedIndex + 1 : _rules.Count;
        _rules.Insert(at, new RuleModel { Pattern = pattern, Enabled = true, Text = Color.Black });
        Rebuild(at);
        _pattern.Focus();
        _pattern.SelectAll();
    }

    internal void DeleteSelected()
    {
        int index = SelectedIndex;
        if (index < 0)
            return;

        _rules.RemoveAt(index);
        Rebuild(Math.Min(index, _rules.Count - 1));
    }

    internal void MoveSelected(int step)
    {
        int index = SelectedIndex;
        int target = index + step;
        if (index < 0 || target < 0 || target >= _rules.Count)
            return;

        (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
        Rebuild(target);
    }

    internal void ResetToDefaults()
    {
        _rules.Clear();
        _rules.AddRange(ColorRuleSet.CreateDefault().Rules.Select(RuleModel.From));
        Rebuild(0);
    }

    /// <summary>Replaces the rules with those in <paramref name="ini"/> (Q-Dir <c>[Filter2]</c> or M-Dir <c>[ColorFilter]</c>); false when it has none.</summary>
    internal bool Import(IniFile ini)
    {
        ColorRuleSet? imported = ColorRuleSet.ImportQDir(ini) ?? ColorRuleSet.Read(ini);
        if (imported is null)
            return false;

        _rules.Clear();
        _rules.AddRange(imported.Rules.Select(RuleModel.From));
        Rebuild(0);
        return true;
    }

    private void ImportFromFile()
    {
        using var dialog = new OpenFileDialog { Title = "색상 규칙 가져오기", Filter = IniFilter };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            if (!Import(IniFile.Load(dialog.FileName)))
                MessageBox.Show(this, "이 파일에서 색상 규칙([Filter2] 또는 [ColorFilter])을 찾을 수 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"파일을 읽지 못했습니다.\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportToFile()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "색상 규칙 내보내기",
            Filter = "M-Dir 형식 (*.ini)|*.ini|Q-Dir 형식 (*.ini, 배경색·굵게 제외)|*.ini",
            DefaultExt = "ini",
            AddExtension = true,
            FileName = "color-filter.ini",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            ColorRuleSet rules = Result;
            if (dialog.FilterIndex == 2)
            {
                rules.ExportQDir().Save(dialog.FileName);
            }
            else
            {
                var ini = new IniFile();
                rules.Write(ini);
                ini.Save(dialog.FileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"파일을 저장하지 못했습니다.\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PickColor(Func<RuleModel, Color?> get, Action<RuleModel, Color?> set)
    {
        if (SelectedRule is not { } rule)
            return;

        using var dialog = new ColorDialog { FullOpen = true, Color = get(rule) ?? Color.Black };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            EditSelected(r => set(r, dialog.Color));
    }

    private RuleModel? SelectedRule => SelectedIndex is >= 0 and var index && index < _rules.Count ? _rules[index] : null;

    private void EditSelected(Action<RuleModel> edit)
    {
        if (_loading || SelectedRule is not { } rule)
            return;

        edit(rule);
        RefreshItem(SelectedIndex);
        ShowRule(rule);
    }

    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (!_loading && e.Item.Index is >= 0 and var index && index < _rules.Count)
            _rules[index].Enabled = e.Item.Checked;
    }

    private void LoadSelected()
    {
        if (SelectedRule is { } rule)
            ShowRule(rule);
        else
            ShowRule(null);
    }

    /// <summary>Fills the edit boxes from <paramref name="rule"/> without treating that as an edit.</summary>
    private void ShowRule(RuleModel? rule)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            foreach (Control control in new Control[] { _pattern, _textColor, _backColor, _clearText, _clearBack, _bold })
                control.Enabled = rule is not null;

            if (_pattern.Text != (rule?.Pattern ?? string.Empty))
                _pattern.Text = rule?.Pattern ?? string.Empty;
            _bold.Checked = rule?.Bold ?? false;
            Swatch(_textColor, rule?.Text, "글자색…");
            Swatch(_backColor, rule?.Background, "배경색…");
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private static void Swatch(Button button, Color? color, string emptyText)
    {
        button.BackColor = color ?? SystemColors.Control;
        button.ForeColor = color is { } c ? (c.GetBrightness() > 0.55f ? Color.Black : Color.White) : SystemColors.ControlText;
        button.Text = color is null ? emptyText : ColorRuleSet.FormatColor(color);
        button.UseVisualStyleBackColor = color is null;
    }

    private void Rebuild(int selectIndex)
    {
        _loading = true;
        try
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (RuleModel rule in _rules)
                _list.Items.Add(CreateItem(rule));
            _list.EndUpdate();
        }
        finally
        {
            _loading = false;
        }

        _selected = -1;
        if (_rules.Count > 0)
            SelectRule(Math.Clamp(selectIndex, 0, _rules.Count - 1));
        else
            ShowRule(null);
    }

    private static ListViewItem CreateItem(RuleModel rule)
    {
        var item = new ListViewItem(rule.Pattern) { Checked = rule.Enabled };
        item.SubItems.Add(ColorRuleSet.FormatColor(rule.Text));
        item.SubItems.Add(ColorRuleSet.FormatColor(rule.Background));
        item.SubItems.Add(rule.Bold ? "✔" : string.Empty);
        Preview(item, rule);
        return item;
    }

    private void RefreshItem(int index)
    {
        if (index < 0 || index >= _list.Items.Count)
            return;

        RuleModel rule = _rules[index];
        ListViewItem item = _list.Items[index];
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            item.Text = rule.Pattern;
            item.SubItems[1].Text = ColorRuleSet.FormatColor(rule.Text);
            item.SubItems[2].Text = ColorRuleSet.FormatColor(rule.Background);
            item.SubItems[3].Text = rule.Bold ? "✔" : string.Empty;
            Preview(item, rule);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>Shows the rule in its own colours (a #BG rule's colour is a background).</summary>
    private static void Preview(ListViewItem item, RuleModel rule)
    {
        bool background = rule.ToRule().IsBackgroundRule;
        item.ForeColor = !background && rule.Text is { } text ? text : SystemColors.WindowText;
        item.BackColor = background && rule.Text is { } back ? back : rule.Background ?? SystemColors.Window;
        item.Font = new Font(item.ListView?.Font ?? SystemFonts.DefaultFont, rule.Bold ? FontStyle.Bold : FontStyle.Regular);
    }

    private static Button CreateButton(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += onClick;
        return button;
    }

    private sealed class RuleModel
    {
        public string Pattern { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public Color? Text { get; set; }

        public Color? Background { get; set; }

        public bool Bold { get; set; }

        public static RuleModel From(ColorRule rule) => new()
        {
            Pattern = rule.Pattern,
            Enabled = rule.Enabled,
            Text = rule.Text,
            Background = rule.Background,
            Bold = rule.Bold,
        };

        public ColorRule ToRule() => new(Pattern, Enabled, Text, Background, Bold);
    }
}
