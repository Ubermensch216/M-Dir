using MDir.Commands;

namespace MDir.Features;

/// <summary>
/// Edits the keyboard shortcuts: pick a command, press the new keys, assign. A key already used by another command
/// is taken over only after asking (the other command then loses it). Changes apply when OK is pressed.
/// </summary>
internal sealed class ShortcutDialog : Form
{
    private readonly CommandRegistry _registry;
    private readonly Dictionary<string, List<Keys>> _working;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        UseCompatibleStateImageBehavior = false,
    };

    private readonly TextBox _capture = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private Keys _pressed = Keys.None;

    /// <summary>Asks whether a key may be taken from another command; replaced by tests.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, bool> ConfirmTakeOver { get; set; }

    public ShortcutDialog(CommandRegistry registry)
    {
        _registry = registry;
        _working = registry.All.ToDictionary(command => command.Id, command => registry.GetShortcuts(command).ToList(), StringComparer.Ordinal);
        ConfirmTakeOver = message => MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        Text = "단축키 편집";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(700, 560);
        MinimumSize = new Size(560, 420);

        _list.Columns.Add("분류", 90);
        _list.Columns.Add("명령", 280);
        _list.Columns.Add("단축키", 260);

        var assign = new Button { Text = "지정", AutoSize = true };
        var remove = new Button { Text = "지우기", AutoSize = true };
        var reset = new Button { Text = "기본값", AutoSize = true };
        var resetAll = new Button { Text = "모두 기본값", AutoSize = true };
        assign.Click += (_, _) => AssignPressed();
        remove.Click += (_, _) => RemoveShortcuts(SelectedId);
        reset.Click += (_, _) => ResetShortcuts(SelectedId);
        resetAll.Click += (_, _) => ResetAll();

        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        row.Controls.AddRange([assign, remove, reset, resetAll]);

        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        var closing = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        closing.Controls.AddRange([cancel, ok]);
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_list, 0, 0);
        layout.Controls.Add(new Label { Text = "선택한 명령의 새 단축키 (아래 칸을 클릭하고 키를 누르세요)", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 1);
        layout.Controls.Add(_capture, 0, 2);
        layout.Controls.Add(row, 0, 3);
        layout.Controls.Add(_status, 0, 4);
        layout.Controls.Add(closing, 0, 5);
        Controls.Add(layout);

        // Every key press belongs to the capture box, including Tab and the arrow keys.
        _capture.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        _capture.KeyDown += OnCaptureKeyDown;
        _list.SelectedIndexChanged += (_, _) => _status.Text = string.Empty;

        Rebuild(selectId: null);
    }

    /// <summary>The table to apply: command id to its shortcuts.</summary>
    public IReadOnlyDictionary<string, Keys[]> Result => _working.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);

    internal ListView List => _list;

    internal string? SelectedId => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    internal string StatusText => _status.Text;

    internal IReadOnlyList<Keys> ShortcutsOf(string id) => _working[id];

    internal void Select(string id)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if ((string?)item.Tag == id)
            {
                item.Selected = true;
                item.EnsureVisible();
            }
        }
    }

    /// <summary>
    /// Gives <paramref name="keys"/> to a command (as its only shortcut). When another command uses the key, the
    /// user is asked; on yes it is taken from that command. Returns whether the key was assigned.
    /// </summary>
    internal bool Assign(string id, Keys keys)
    {
        if (id is null || !_working.ContainsKey(id))
            return false;

        foreach ((string otherId, List<Keys> otherKeys) in _working)
        {
            if (otherId == id || !otherKeys.Contains(keys))
                continue;

            string otherName = _registry.Get(otherId).DisplayText;
            if (!ConfirmTakeOver($"{CommandRegistry.FormatKeys(keys)} 키는 이미 '{otherName}'에 지정되어 있습니다.\n그 명령에서 이 키를 빼앗을까요?"))
            {
                _status.Text = $"{CommandRegistry.FormatKeys(keys)}: '{otherName}'에서 사용 중이라 지정하지 않았습니다.";
                return false;
            }

            otherKeys.Remove(keys);
        }

        _working[id] = [keys];
        _status.Text = $"{_registry.Get(id).DisplayText}: {CommandRegistry.FormatKeys(keys)}";
        Rebuild(id);
        return true;
    }

    internal void RemoveShortcuts(string? id)
    {
        if (id is null || !_working.TryGetValue(id, out List<Keys>? keys))
            return;

        keys.Clear();
        Rebuild(id);
    }

    internal void ResetShortcuts(string? id)
    {
        if (id is null || !_working.ContainsKey(id))
            return;

        _working[id] = [.. _registry.Get(id).DefaultShortcuts];

        // Getting a default back may collide with a key someone else took meanwhile: that one yields.
        foreach (Keys key in _working[id])
        {
            foreach ((string otherId, List<Keys> otherKeys) in _working)
            {
                if (otherId != id)
                    otherKeys.Remove(key);
            }
        }

        Rebuild(id);
    }

    internal void ResetAll()
    {
        foreach (Command command in _registry.All)
            _working[command.Id] = [.. command.DefaultShortcuts];

        Rebuild(SelectedId);
    }

    private void AssignPressed()
    {
        if (SelectedId is { } id && _pressed != Keys.None)
            Assign(id, _pressed);
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        // A lone modifier is not a shortcut yet.
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return;

        _pressed = e.KeyData;
        _capture.Text = CommandRegistry.FormatKeys(_pressed);
    }

    private void Rebuild(string? selectId)
    {
        selectId ??= SelectedId;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (Command command in _registry.All)
        {
            List<Keys> keys = _working[command.Id];
            bool changed = !keys.SequenceEqual(command.DefaultShortcuts);
            var item = new ListViewItem(command.Category) { Tag = command.Id, Font = changed ? new Font(_list.Font, FontStyle.Bold) : _list.Font };
            item.SubItems.Add(command.DisplayText);
            item.SubItems.Add(string.Join(", ", keys.Select(CommandRegistry.FormatKeys)));
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        if (selectId is not null)
            Select(selectId);
    }
}
