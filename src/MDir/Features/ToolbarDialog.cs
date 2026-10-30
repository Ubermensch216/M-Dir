using MDir.Commands;
using MDir.Localization;

namespace MDir.Features;

/// <summary>Edits which buttons the toolbar shows and in which order. <see cref="Result"/> holds the tokens of <see cref="ToolbarLayout"/>.</summary>
internal sealed class ToolbarDialog : Form
{
    private readonly CommandRegistry _commands;
    private readonly ListBox _available = new() { Dock = DockStyle.Fill, IntegralHeight = false, Sorted = false };
    private readonly ListBox _current = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _add = new() { Text = "추가 →", Dock = DockStyle.Top };
    private readonly Button _remove = new() { Text = "← 제거", Dock = DockStyle.Top };
    private readonly Button _up = new() { Text = "위로", Dock = DockStyle.Top };
    private readonly Button _down = new() { Text = "아래로", Dock = DockStyle.Top };
    private readonly Button _separator = new() { Text = "구분선 추가", Dock = DockStyle.Top };
    private readonly Button _reset = new() { Text = "기본값", Dock = DockStyle.Top };
    private readonly List<string> _tokens;

    public ToolbarDialog(CommandRegistry commands, IReadOnlyList<string> tokens)
    {
        _commands = commands;
        _tokens = [.. tokens];

        Text = "도구 모음 편집";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(720, 460);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "사용할 수 있는 명령", AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "도구 모음", AutoSize = true }, 2, 0);
        layout.Controls.Add(_available, 0, 1);
        layout.Controls.Add(_current, 2, 1);

        var buttons = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        foreach (Button button in new[] { _reset, _separator, _down, _up, _remove, _add })
        {
            button.Margin = new Padding(0, 0, 0, 6);
            button.Height = 30;
            buttons.Controls.Add(button);
        }

        layout.Controls.Add(buttons, 1, 1);

        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 90 };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        bottom.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(bottom, 0, 2);
        layout.SetColumnSpan(bottom, 3);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        _add.Click += (_, _) => AddSelected();
        _available.DoubleClick += (_, _) => AddSelected();
        _remove.Click += (_, _) => RemoveSelected();
        _current.DoubleClick += (_, _) => RemoveSelected();
        _up.Click += (_, _) => MoveSelected(-1);
        _down.Click += (_, _) => MoveSelected(1);
        _separator.Click += (_, _) => AddToken(ToolbarLayout.Separator);
        _reset.Click += (_, _) => Reset();
        FillAvailable();
        FillCurrent(select: 0);
    }

    /// <summary>The tokens as edited.</summary>
    public IReadOnlyList<string> Result => _tokens;

    /// <summary>Adds the command (or layout drop-down) selected in the left list after the selection on the right.</summary>
    public void AddSelected()
    {
        if (_available.SelectedItem is Entry entry)
            AddToken(entry.Token);
    }

    public void AddToken(string token)
    {
        // A button can be on the toolbar once; a separator any number of times.
        if (token != ToolbarLayout.Separator && _tokens.Contains(token))
        {
            _current.SelectedIndex = _tokens.IndexOf(token);
            return;
        }

        int at = _current.SelectedIndex >= 0 ? _current.SelectedIndex + 1 : _tokens.Count;
        _tokens.Insert(at, token);
        FillCurrent(select: at);
    }

    public void RemoveSelected()
    {
        int index = _current.SelectedIndex;
        if (index < 0)
            return;

        _tokens.RemoveAt(index);
        FillCurrent(select: Math.Min(index, _tokens.Count - 1));
    }

    public void MoveSelected(int step)
    {
        int index = _current.SelectedIndex;
        int target = index + step;
        if (index < 0 || target < 0 || target >= _tokens.Count)
            return;

        (_tokens[index], _tokens[target]) = (_tokens[target], _tokens[index]);
        FillCurrent(select: target);
    }

    public void Reset()
    {
        _tokens.Clear();
        _tokens.AddRange(ToolbarLayout.Default);
        FillCurrent(select: 0);
    }

    internal IReadOnlyList<string> CurrentTexts => [.. _current.Items.Cast<Entry>().Select(entry => entry.Text)];

    private void FillAvailable()
    {
        _available.Items.Add(new Entry(ToolbarLayout.Layouts, Loc.T("레이아웃 (드롭다운)")));
        foreach (Command command in _commands.All.OrderBy(command => command.CategoryText, StringComparer.CurrentCulture))
            _available.Items.Add(new Entry(command.Id, $"{command.CategoryText} · {command.DisplayText}"));
    }

    private void FillCurrent(int select)
    {
        _current.BeginUpdate();
        _current.Items.Clear();
        foreach (string token in _tokens)
            _current.Items.Add(new Entry(token, TextFor(token)));
        _current.EndUpdate();
        if (select >= 0 && select < _current.Items.Count)
            _current.SelectedIndex = select;
    }

    private string TextFor(string token) =>
        token == ToolbarLayout.Separator ? Loc.T("──────  구분선  ──────")
        : token == ToolbarLayout.Layouts ? Loc.T("레이아웃 (드롭다운)")
        : _commands.TryGet(token, out Command? command) && command is not null ? command.DisplayText
        : token;

    private sealed record Entry(string Token, string Text)
    {
        public override string ToString() => Text;
    }
}
