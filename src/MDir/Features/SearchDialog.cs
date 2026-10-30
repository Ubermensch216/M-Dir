using System.ComponentModel;
using MDir.Localization;

namespace MDir.Features;

/// <summary>
/// Searches a folder tree by name and content (<see cref="FileSearcher"/>). Double-clicking a result raises
/// <see cref="OpenRequested"/>; "Windows 검색" closes the dialog and asks the window to open Windows Search
/// (<see cref="WindowsSearchRequested"/>) with the same text.
/// </summary>
internal sealed class SearchDialog : Form
{
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _content = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _subfolders = new() { Text = "하위 폴더 포함", Checked = true, AutoSize = true };
    private readonly CheckBox _matchCase = new() { Text = "대소문자 구분", AutoSize = true };
    private readonly CheckBox _folders = new() { Text = "폴더도 찾기", Checked = true, AutoSize = true };
    private readonly Button _search = new() { Text = "검색", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly List<SearchHit> _hits = [];
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        UseCompatibleStateImageBehavior = false,
    };

    private CancellationTokenSource? _cancel;

    public SearchDialog(string folder)
    {
        Text = "검색";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 620);
        MinimumSize = new Size(700, 420);
        _folder.Text = folder;

        _list.Columns.Add("이름", 220);
        _list.Columns.Add("위치", 330);
        _list.Columns.Add("크기", 90, HorizontalAlignment.Right);
        _list.Columns.Add("수정한 날짜", 130);
        _list.Columns.Add("줄", 50, HorizontalAlignment.Right);

        var browse = new Button { Text = "찾아보기…", AutoSize = true };
        browse.Click += (_, _) => Browse();
        var windowsSearch = new Button { Text = "Windows 검색으로 열기", AutoSize = true };
        windowsSearch.Click += (_, _) => RequestWindowsSearch();
        var close = new Button { Text = "닫기", DialogResult = DialogResult.OK, AutoSize = true };
        CancelButton = close;
        AcceptButton = _search;
        _search.Click += (_, _) =>
        {
            if (_cancel is { IsCancellationRequested: false })
                _cancel.Cancel();
            else
                _ = SearchAsync();
        };
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
                OpenSelected();
        };

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label { Text = "위치", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        fields.Controls.Add(_folder, 1, 0);
        fields.Controls.Add(browse, 2, 0);
        fields.Controls.Add(new Label { Text = "이름", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        fields.Controls.Add(_name, 1, 1);
        fields.Controls.Add(new Label { Text = "내용", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        fields.Controls.Add(_content, 1, 2);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        options.Controls.AddRange([_subfolders, _matchCase, _folders, _search]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange([close, windowsSearch]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(fields, 0, 0);
        layout.Controls.Add(options, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);

        FormClosing += (_, _) => _cancel?.Cancel();
        Shown += (_, _) => _name.Focus();
    }

    /// <summary>Raised with the full path of the result the user opened.</summary>
    internal event EventHandler<string>? OpenRequested;

    /// <summary>Raised (and the dialog closed) when the user chose Windows Search: folder and text to search for.</summary>
    internal event EventHandler<(string Folder, string Query)>? WindowsSearchRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Root { get => _folder.Text; set => _folder.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string NamePattern { get => _name.Text; set => _name.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string ContentText { get => _content.Text; set => _content.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IncludeSubfolders { get => _subfolders.Checked; set => _subfolders.Checked = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool MatchCase { get => _matchCase.Checked; set => _matchCase.Checked = value; }

    internal IReadOnlyList<SearchHit> Hits => _hits;

    internal ListView ResultList => _list;

    internal string StatusText => _status.Text;

    internal bool IsSearching => _cancel is { IsCancellationRequested: false };

    internal async Task SearchAsync()
    {
        if (!Directory.Exists(_folder.Text))
        {
            _status.Text = Loc.T("위치 폴더를 찾을 수 없습니다.");
            return;
        }

        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _hits.Clear();
        _list.Items.Clear();
        _search.Text = Loc.T("중지");
        _status.Text = Loc.T("검색 중…");

        // Reports are posted to this thread and can arrive after the search ended; they only add rows or text.
        bool finished = false;
        var options = new SearchOptions(_folder.Text, _name.Text, _content.Text, _subfolders.Checked, _matchCase.Checked, _folders.Checked);
        var found = new Progress<SearchHit>(hit =>
        {
            if (!cancel.IsCancellationRequested)
                AddHit(hit);
        });
        var scanning = new Progress<string>(folder =>
        {
            if (!finished && !cancel.IsCancellationRequested)
                _status.Text = Loc.F("검색 중… {0:N0}개 찾음 — {1}", _hits.Count, folder);
        });

        try
        {
            SearchSummary summary = await FileSearcher.SearchAsync(options, found, scanning, cancel.Token);

            // The reports were posted to this thread before the task finished, so they have all been handled by now.
            finished = true;
            _status.Text = Loc.F("{0:N0}개 찾음 ({1:N0}개 폴더 검색)", summary.Hits, summary.FoldersScanned)
                + (summary.LimitReached ? Loc.T(" — 결과가 너무 많아 일부만 표시") : string.Empty);
        }
        catch (OperationCanceledException)
        {
            finished = true;
            _status.Text = Loc.F("중지됨 — {0:N0}개 찾음", _hits.Count);
        }
        finally
        {
            if (_cancel == cancel)
            {
                _search.Text = Loc.T("검색");
                _cancel = null;
            }
        }
    }

    private void AddHit(SearchHit hit)
    {
        _hits.Add(hit);

        string name = Path.GetFileName(hit.Path);
        var item = new ListViewItem(name) { Tag = hit };
        item.SubItems.Add(Path.GetDirectoryName(hit.Path) ?? string.Empty);
        item.SubItems.Add(hit.IsFolder ? string.Empty : PreviewPane.FormatSize(hit.Size));
        item.SubItems.Add(hit.Modified == default ? string.Empty : hit.Modified.ToString("yyyy-MM-dd HH:mm"));
        item.SubItems.Add(hit.Line > 0 ? hit.Line.ToString() : string.Empty);
        _list.Items.Add(item);
    }

    /// <summary>Opens the selected result in the file window (its folder, with the item selected).</summary>
    internal void OpenSelected()
    {
        if (_list.SelectedIndices.Count > 0)
            OpenResult(_list.SelectedIndices[0]);
    }

    /// <summary>Opens result number <paramref name="index"/> of the list.</summary>
    internal void OpenResult(int index)
    {
        if (index >= 0 && index < _hits.Count)
            OpenRequested?.Invoke(this, _hits[index].Path);
    }

    internal void RequestWindowsSearch()
    {
        string query = string.IsNullOrWhiteSpace(_content.Text) ? _name.Text.Trim('*', ' ') : _content.Text;
        WindowsSearchRequested?.Invoke(this, (_folder.Text, query));
        DialogResult = DialogResult.Yes;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "검색할 폴더" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _folder.Text = dialog.SelectedPath;
    }
}
