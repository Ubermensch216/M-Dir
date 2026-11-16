using MDir.Localization;
using MDir.Settings;

namespace MDir.Finder;

/// <summary>Settings of the built-in M-Finder: what to index, what to leave out, and the optional external M-Finder.</summary>
internal sealed class FinderOptionsDialog : Form
{
    private readonly FinderOptions _options;
    private readonly TextBox _roots = Lines();
    private readonly TextBox _excludeFolders = Lines();
    private readonly TextBox _excludePatterns = Lines();
    private readonly TextBox _external = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _useExternal = new() { Text = "내장 검색 대신 외부 M-Finder 사용", AutoSize = true };
    private readonly CheckBox _useMft = new() { Text = "NTFS 드라이브를 MFT로 빠르게 색인 (관리자 권한 필요)", AutoSize = true };
    private readonly ComboBox _consent = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _monitor = new() { Text = "파일이 바뀌면 색인을 바로 갱신", AutoSize = true };
    private readonly CheckBox _folderSizes = new() { Text = "폴더 크기를 백그라운드에서 계산", AutoSize = true };
    private readonly CheckBox _hidden = new() { Text = "숨김 파일·폴더 제외", AutoSize = true };
    private readonly CheckBox _system = new() { Text = "시스템 파일·폴더 제외", AutoSize = true };
    private readonly CheckBox _rebuild = new() { Text = "확인하면 색인을 처음부터 다시 만들기", AutoSize = true };
    private readonly NumericUpDown _contentMax = new() { Minimum = 1, Maximum = 4096, Width = 80 };

    public FinderOptionsDialog(FinderOptions options)
    {
        _options = options;
        Text = "M-Finder 설정";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 640);
        MinimumSize = new Size(520, 560);

        _consent.Items.AddRange(["처음 쓸 때 묻기", "허용 (UAC 확인 창 표시)", "허용 안 함 (폴더를 직접 훑음)"]);
        _consent.SelectedIndex = (int)options.MftConsent;

        var addRoot = new Button { Text = "폴더 추가...", AutoSize = true };
        addRoot.Click += (_, _) => AddFolder(_roots);
        var addExclude = new Button { Text = "폴더 추가...", AutoSize = true };
        addExclude.Click += (_, _) => AddFolder(_excludeFolders);
        var browse = new Button { Text = "찾아보기...", AutoSize = true };
        browse.Click += (_, _) => BrowseExternal();

        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Apply();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control control, SizeType rows = SizeType.AutoSize, float height = 0)
        {
            layout.RowStyles.Add(new RowStyle(rows, height));
            layout.Controls.Add(control);
            control.Dock = DockStyle.Fill;
        }

        Add(Hint("색인할 폴더 (한 줄에 하나, 비워 두면 모든 로컬 드라이브)"));
        Add(_roots, SizeType.Percent, 18);
        Add(Row(addRoot));
        Add(_useMft);
        Add(Row(new Label { Text = "관리자 권한:", AutoSize = true, Padding = new Padding(16, 6, 0, 0) }, _consent));
        Add(_monitor);
        Add(_folderSizes);
        Add(_hidden);
        Add(_system);
        Add(Hint("제외할 폴더 (한 줄에 하나)"));
        Add(_excludeFolders, SizeType.Percent, 14);
        Add(Row(addExclude));
        Add(Hint("제외할 이름 (와일드카드, 한 줄에 하나. 예: *.tmp)"));
        Add(_excludePatterns, SizeType.Percent, 12);
        Add(Row(new Label { Text = "본문 검색(content:)이 건너뛸 파일 크기(MB):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _contentMax));
        Add(Hint("기본은 M-Dir에 내장된 검색입니다. 따로 설치한 M-Finder를 쓰고 싶을 때만 아래를 켜세요."));
        Add(_useExternal);
        var externalRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        externalRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        externalRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        externalRow.Controls.Add(_external);
        externalRow.Controls.Add(browse);
        Add(externalRow);
        Add(_rebuild);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(layout);
        Controls.Add(buttons);

        Populate(options);
    }

    /// <summary>The user asked for the index to be built again from scratch.</summary>
    public bool RebuildRequested => _rebuild.Checked;

    private static TextBox Lines() => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, MinimumSize = new Size(0, 48) };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 8, 0, 2) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange(controls);
        return row;
    }

    private void Populate(FinderOptions o)
    {
        _roots.Text = string.Join(Environment.NewLine, o.Roots);
        _excludeFolders.Text = string.Join(Environment.NewLine, o.ExcludeFolders);
        _excludePatterns.Text = string.Join(Environment.NewLine, o.ExcludePatterns);
        _external.Text = o.ExternalPath;
        _useExternal.Checked = o.UseExternal;
        _useMft.Checked = o.UseMft;
        _monitor.Checked = o.Monitor;
        _folderSizes.Checked = o.FolderSizes;
        _hidden.Checked = o.ExcludeHidden;
        _system.Checked = o.ExcludeSystem;
        _contentMax.Value = Math.Clamp(o.ContentMaxMb, 1, 4096);
    }

    private static List<string> ReadLines(TextBox box) =>
        [.. box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Trim('"')).Where(l => l.Length > 0)];

    private void Apply()
    {
        _options.Roots = ReadLines(_roots);
        _options.ExcludeFolders = ReadLines(_excludeFolders);
        _options.ExcludePatterns = ReadLines(_excludePatterns);
        _options.ExternalPath = _external.Text.Trim().Trim('"');
        _options.UseExternal = _useExternal.Checked && _options.ExternalPath.Length > 0;
        _options.UseMft = _useMft.Checked;
        _options.MftConsent = (FinderMftConsent)Math.Max(0, _consent.SelectedIndex);
        _options.Monitor = _monitor.Checked;
        _options.FolderSizes = _folderSizes.Checked;
        _options.ExcludeHidden = _hidden.Checked;
        _options.ExcludeSystem = _system.Checked;
        _options.ContentMaxMb = (int)_contentMax.Value;
    }

    private void AddFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = Loc.T("폴더 선택") };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        target.Text = target.Text.Length == 0 ? dialog.SelectedPath : target.Text.TrimEnd() + Environment.NewLine + dialog.SelectedPath;
    }

    private void BrowseExternal()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Loc.T("M-Finder 위치 지정"),
            Filter = Loc.T("M-Finder (M-Finder.exe, __main__.py)|M-Finder.exe;MFinder.exe;__main__.py|프로그램 (*.exe)|*.exe|모든 파일 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _external.Text = dialog.FileName;
    }
}
