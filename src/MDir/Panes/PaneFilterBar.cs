using System.ComponentModel;
using MDir.Features;

namespace MDir.Panes;

/// <summary>
/// The filter strip of a pane: a pattern box (with the recent patterns), and the options of Q-Dir's file filter —
/// exclude instead of include, filter folders too, and wrap a single word as <c>*word*</c>.
/// Typing applies the filter after a short pause; Enter applies it at once and remembers the pattern.
/// </summary>
internal sealed class PaneFilterBar : Panel
{
    private readonly ComboBox _pattern = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(3, 3, 3, 3) };
    private readonly CheckBox _negative = new() { Text = "제외", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 3, 3, 3) };
    private readonly CheckBox _folders = new() { Text = "폴더도", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 3, 3) };
    private readonly CheckBox _auto = new() { Text = "자동 *", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 3, 3) };
    private readonly Button _clear = new() { Text = "✕", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 2, 3, 2), FlatStyle = FlatStyle.System };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    private readonly ToolTip _toolTip = new();
    private bool _updating;

    public PaneFilterBar()
    {
        Height = 30;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "필터", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 3, 3, 3) }, 0, 0);
        layout.Controls.Add(_pattern, 1, 0);
        layout.Controls.Add(_negative, 2, 0);
        layout.Controls.Add(_folders, 3, 0);
        layout.Controls.Add(_auto, 4, 0);
        layout.Controls.Add(_clear, 5, 0);
        Controls.Add(layout);

        _toolTip.SetToolTip(_pattern, "파일 이름 필터. 예: *.txt;*.md  (; 또는 , 로 구분, Enter로 기억)");
        _toolTip.SetToolTip(_negative, "일치하는 항목을 숨김 (제외 필터), 예: Thumbs.db;*.bak");
        _toolTip.SetToolTip(_folders, "폴더에도 필터를 적용 (끄면 폴더는 항상 표시)");
        _toolTip.SetToolTip(_auto, "패턴이 하나이고 와일드카드가 없으면 *패턴* 으로 검색");
        _toolTip.SetToolTip(_clear, "필터 지우기 (Esc)");

        _debounce.Tick += (_, _) => RaiseFilterChanged();
        _pattern.TextChanged += (_, _) =>
        {
            if (_updating)
                return;

            _debounce.Stop();
            _debounce.Start();
        };
        _pattern.KeyDown += OnPatternKeyDown;
        _pattern.SelectedIndexChanged += (_, _) => RaiseFilterChanged();
        _pattern.Leave += (_, _) => Commit();
        foreach (CheckBox box in new[] { _negative, _folders, _auto })
            box.CheckedChanged += (_, _) => RaiseFilterChanged();

        _clear.Click += (_, _) => ClearFilter();
    }

    /// <summary>Raised when the filter changed (after the typing pause, or at once for the option boxes and Enter).</summary>
    public event EventHandler? FilterChanged;

    /// <summary>Raised when a non-empty pattern was confirmed (Enter, picking a recent one, leaving the box).</summary>
    public event EventHandler<string>? PatternCommitted;

    /// <summary>Raised when Esc or the clear button emptied the filter and focus should go back to the list.</summary>
    public event EventHandler? Dismissed;

    /// <summary>The filter as shown. Assigning it updates the boxes without raising <see cref="FilterChanged"/>.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FileFilter Filter
    {
        get => new(_pattern.Text.Trim(), _auto.Checked, _negative.Checked, _folders.Checked);
        set
        {
            _updating = true;
            try
            {
                _pattern.Text = value.Pattern;
                _auto.Checked = value.AutoWildcard;
                _negative.Checked = value.Negative;
                _folders.Checked = value.FilterFolders;
            }
            finally
            {
                _updating = false;
            }

            _debounce.Stop();
        }
    }

    internal ComboBox PatternBox => _pattern;

    internal CheckBox NegativeBox => _negative;

    internal IReadOnlyList<string> History => [.. _pattern.Items.Cast<string>()];

    public void SetHistory(IEnumerable<string> patterns)
    {
        _updating = true;
        try
        {
            string text = _pattern.Text;
            _pattern.Items.Clear();
            _pattern.Items.AddRange([.. patterns.Cast<object>()]);
            _pattern.Text = text;
        }
        finally
        {
            _updating = false;
        }
    }

    public void FocusPattern()
    {
        _pattern.Focus();
        _pattern.SelectAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounce.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnPatternKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            RaiseFilterChanged();
            Commit();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            ClearFilter();
        }
    }

    private void ClearFilter()
    {
        _updating = true;
        try
        {
            _pattern.Text = string.Empty;
        }
        finally
        {
            _updating = false;
        }

        RaiseFilterChanged();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseFilterChanged()
    {
        _debounce.Stop();
        if (!_updating)
            FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Commit()
    {
        string text = _pattern.Text.Trim();
        if (text.Length > 0)
            PatternCommitted?.Invoke(this, text);
    }
}
