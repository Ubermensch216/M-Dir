using System.Globalization;
using System.Runtime.InteropServices;
using MDir.Integration;
using MDir.Localization;
using MDir.Shell;

namespace MDir.Finder;

/// <summary>
/// The M-Finder search panel: a search box with M-Finder's filters and match options over a virtual result list.
/// Searching is done by a running M-Finder (see <see cref="IFinderBackend"/>); the list asks for the rows of a page
/// only when they scroll into view, so a million results cost no more than a hundred.
/// </summary>
internal sealed class FinderPane : Panel
{
    private const int PageSize = FinderClient.PageSize;
    private const int SearchDelayMs = 150;
    private const int ColumnName = 0, ColumnFolder = 1, ColumnSize = 2, ColumnModified = 3;
    private static readonly string[] SortKeys = ["name", "path", "size", "mtime"];

    private readonly IFinderBackend _backend;
    private readonly Func<Task<string?>> _ensureRunning;
    private readonly TextBox _searchBox = new() { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle };
    private readonly ToolStrip _options = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, CanOverflow = false, RenderMode = ToolStripRenderMode.System };
    private readonly ToolStripComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 110 };
    private readonly ToolStripButton _matchCase = Toggle("Aa", "대소문자 구분");
    private readonly ToolStripButton _wholeWord = Toggle("ab", "단어 단위로 일치");
    private readonly ToolStripButton _matchPath = Toggle(@"\", "전체 경로에서 찾기");
    private readonly ToolStripButton _regex = Toggle(".*", "정규식");
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        VirtualMode = true,
        FullRowSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
        AllowColumnReorder = false,
        ShowItemToolTips = true,
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 22, Padding = new Padding(4, 3, 4, 0), AutoEllipsis = true };
    private readonly LinkLabel _locate = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 22, Padding = new Padding(4, 3, 4, 0), Visible = false };
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = SearchDelayMs };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 1000 };
    private readonly ImageList _icons = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly Dictionary<string, int> _iconIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, FinderRow[]> _pages = [];
    private readonly HashSet<int> _pendingPages = [];

    private FinderQuery _query = new(string.Empty);
    private int _generation;
    private int _total;
    private long _revision = -1;
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastStartAttempt = DateTime.MinValue;
    private int _sortColumn = ColumnName;
    private bool _ascending = true;
    private bool _connected;
    private bool _connecting;
    private bool _ready;
    private bool _active;
    private bool _darkMode;
    private bool _fillingFilters;
    private int _lastSelected = -1;

    /// <param name="ensureRunning">Makes sure an M-Finder answers (starting one if needed); returns null, or why it could not.</param>
    public FinderPane(IFinderBackend backend, Func<Task<string?>> ensureRunning)
    {
        _backend = backend;
        _ensureRunning = ensureRunning;

        _list.Columns.AddRange(
        [
            new ColumnHeader { Width = 200 },
            new ColumnHeader { Width = 260 },
            new ColumnHeader { Width = 80, TextAlign = HorizontalAlignment.Right },
            new ColumnHeader { Width = 140 },
        ]);
        _list.SmallImageList = _icons;
        _list.RetrieveVirtualItem += (_, e) => e.Item = CreateItem(e.ItemIndex);
        _list.ColumnClick += (_, e) => SortBy(e.Column);
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.VirtualItemsSelectionRangeChanged += (_, _) => OnSelectionChanged();
        _list.ItemActivate += (_, _) => ActivateSelected(showInFolder: ModifierKeys.HasFlag(Keys.Control));
        _list.ItemDrag += (_, _) => StartDrag();
        _list.KeyDown += OnListKeyDown;
        _list.HandleCreated += (_, _) => ApplyListTheme();
        _list.ContextMenuStrip = BuildContextMenu();

        _searchBox.TextChanged += (_, _) => ScheduleSearch();
        _searchBox.KeyDown += OnSearchKeyDown;
        _filter.SelectedIndexChanged += (_, _) =>
        {
            if (!_fillingFilters)
                RunSearch();
        };
        foreach (ToolStripButton toggle in new[] { _matchCase, _wholeWord, _matchPath, _regex })
            toggle.CheckedChanged += (_, _) => RunSearch();

        _options.Items.AddRange([_filter, new ToolStripSeparator(), _matchCase, _wholeWord, _matchPath, _regex]);
        _locate.LinkClicked += (_, _) => LocateRequested?.Invoke(this, EventArgs.Empty);

        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RunSearch();
        };
        _pollTimer.Tick += async (_, _) => await PollAsync();

        // Z-order: the list fills what the bars leave (top to bottom: search box, options; status at the bottom).
        Controls.Add(_list);
        Controls.Add(_options);
        Controls.Add(_searchBox);
        Controls.Add(_locate);
        Controls.Add(_status);
        _list.BringToFront();

        ApplyLanguage();
        SetStatus(string.Empty);
    }

    /// <summary>The selected result changed (null: nothing or more than one item is selected).</summary>
    public event EventHandler<FinderRow?>? SelectionChanged;

    /// <summary>Enter or double-click on a result.</summary>
    public event EventHandler<FinderRow>? OpenRequested;

    /// <summary>"Show in folder" (Ctrl+Enter): open the result's folder in the active pane with the item selected.</summary>
    public event EventHandler<FinderRow>? ShowInFolderRequested;

    /// <summary>"Preview" from the context menu: show the result in the preview pane (opening it if it is hidden).</summary>
    public event EventHandler<FinderRow>? PreviewRequested;

    /// <summary>The user wants to tell M-Dir where M-Finder is.</summary>
    public event EventHandler? LocateRequested;

    internal TextBox SearchBox => _searchBox;

    internal ListView List => _list;

    internal int Total => _total;

    internal string StatusText => _status.Text;

    internal bool IsConnected => _connected;

    /// <summary>The query the list shows.</summary>
    internal FinderQuery CurrentQuery => _query;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DarkMode
    {
        get => _darkMode;
        set
        {
            _darkMode = value;
            ApplyListTheme();
        }
    }

    /// <summary>The one selected result whose rows are loaded, or null.</summary>
    public FinderRow? SelectedRow => _list.SelectedIndices.Count == 1 ? RowAt(_list.SelectedIndices[0]) : null;

    /// <summary>Connects (starting M-Finder if needed) and searches; called when the panel is shown.</summary>
    public async Task ActivateAsync()
    {
        _active = true;
        if (!_connected)
            await ConnectAsync();
        else
            _pollTimer.Start();
    }

    /// <summary>Stops polling while the panel is hidden.</summary>
    public void Deactivate()
    {
        _active = false;
        _pollTimer.Stop();
        _searchTimer.Stop();
    }

    /// <summary>Forgets the connection state so the next <see cref="ActivateAsync"/> tries again (after the location changed).</summary>
    public void Reset()
    {
        _connected = false;
        _ready = false;
        _lastStartAttempt = DateTime.MinValue;
    }

    public void FocusSearch()
    {
        _searchBox.Focus();
        _searchBox.SelectAll();
    }

    /// <summary>Texts in the current language (the column headers and tooltips are not reached by <see cref="Loc.Apply(Control)"/>).</summary>
    public void ApplyLanguage()
    {
        _searchBox.PlaceholderText = Loc.T("M-Finder 검색 (Everything 문법)");
        _list.Columns[ColumnName].Text = Loc.T("이름");
        _list.Columns[ColumnFolder].Text = Loc.T("폴더");
        _list.Columns[ColumnSize].Text = Loc.T("크기");
        _list.Columns[ColumnModified].Text = Loc.T("수정한 날짜");
        _matchCase.ToolTipText = Loc.T("대소문자 구분");
        _wholeWord.ToolTipText = Loc.T("단어 단위로 일치");
        _matchPath.ToolTipText = Loc.T("전체 경로에서 찾기");
        _regex.ToolTipText = Loc.T("정규식");
        _filter.ToolTipText = Loc.T("M-Finder 필터");
        _locate.Text = Loc.T("M-Finder 위치 지정...");
        if (_filter.Items.Count > 0)
        {
            _fillingFilters = true;
            _filter.Items[0] = Loc.T("모두");
            _fillingFilters = false;
        }

        if (_list.ContextMenuStrip is { } menu)
            Loc.Apply(menu.Items);
        if (_connected && _ready)
            ShowCount();
    }

    /// <summary>
    /// Moves the selection <paramref name="step"/> items on, skipping results <paramref name="accept"/> rejects (the
    /// preview's ◀ ▶ and slideshow). Only results already loaded are considered. Returns false when there is none.
    /// </summary>
    public bool StepSelection(int step, Func<FinderRow, bool> accept, bool wrap)
    {
        if (_total == 0 || step == 0)
            return false;

        int start = _list.FocusedItem?.Index ?? (_list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1);
        for (int i = 1; i <= _total; i++)
        {
            int index = start < 0 ? (step > 0 ? i - 1 : _total - i) : start + Math.Sign(step) * i;
            if (wrap)
                index = (index % _total + _total) % _total;
            else if (index < 0 || index >= _total)
                return false;

            if (RowAt(index) is not { } row)
                return false;

            if (!row.IsDirectory && accept(row))
            {
                SelectIndex(index);
                return true;
            }
        }

        return false;
    }

    // ---- connection ------------------------------------------------------------------------------------------------

    private async Task ConnectAsync()
    {
        if (_connecting)
            return;

        _connecting = true;
        _locate.Visible = false;
        SetStatus(Loc.T("M-Finder에 연결하는 중..."));
        try
        {
            _lastStartAttempt = DateTime.UtcNow;
            string? error = await _ensureRunning();
            if (IsDisposed)
                return;

            if (error is not null)
            {
                _connected = false;
                SetStatus(error);
                _locate.Visible = true;
                return;
            }

            _connected = true;
            await LoadFiltersAsync();
            await PollAsync();
            if (_active)
                _pollTimer.Start();
        }
        finally
        {
            _connecting = false;
        }
    }

    private async Task LoadFiltersAsync()
    {
        IReadOnlyList<string> filters;
        try
        {
            filters = await _backend.FiltersAsync();
        }
        catch (FinderUnavailableException)
        {
            return;
        }

        string? selected = _filter.SelectedIndex > 0 ? _filter.SelectedItem as string : null;
        _fillingFilters = true;
        try
        {
            _filter.Items.Clear();
            _filter.Items.Add(Loc.T("모두"));
            foreach (string filter in filters)
                _filter.Items.Add(filter);
            int index = selected is null ? 0 : _filter.Items.IndexOf(selected);
            _filter.SelectedIndex = Math.Max(0, index);
        }
        finally
        {
            _fillingFilters = false;
        }
    }

    /// <summary>Follows M-Finder's state: waits for its index, and refreshes the results after the indexed files changed.</summary>
    private async Task PollAsync()
    {
        if (!_connected || IsDisposed)
            return;

        FinderStatus status;
        try
        {
            status = await _backend.StatusAsync();
        }
        catch (FinderUnavailableException)
        {
            OnConnectionLost();
            return;
        }
        catch (FinderException ex)
        {
            SetStatus(ex.Message);
            return;
        }

        if (IsDisposed)
            return;

        if (!status.Ready)
        {
            _ready = false;
            SetStatus(Loc.F("M-Finder가 색인을 준비하는 중입니다: {0}", status.Text));
            return;
        }

        if (!_ready)
        {
            _ready = true;
            RunSearch();
            return;
        }

        // Live changes bump the revision constantly while files are being written; refresh at most every 2 s.
        if (status.Revision != _revision && DateTime.UtcNow - _lastRefresh > TimeSpan.FromSeconds(2))
            RunSearch(keepView: true);
    }

    private void OnConnectionLost()
    {
        _connected = false;
        _ready = false;
        _pollTimer.Stop();
        SetStatus(Loc.T("M-Finder와 연결이 끊겼습니다."));

        // Started again by itself (once every 5 s at most) while the panel is shown.
        if (_active && DateTime.UtcNow - _lastStartAttempt > TimeSpan.FromSeconds(5))
            _ = ConnectAsync();
        else if (_active)
            _pollTimer.Start();
    }

    // ---- searching -------------------------------------------------------------------------------------------------

    private void ScheduleSearch()
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private FinderQuery BuildQuery() => new(
        _searchBox.Text,
        SortKeys[_sortColumn],
        _ascending,
        _filter.SelectedIndex > 0 ? _filter.SelectedItem as string : null,
        _matchCase.Checked,
        _wholeWord.Checked,
        _matchPath.Checked,
        _regex.Checked);

    /// <summary>Runs the current search; with <paramref name="keepView"/> the scroll position and selection stay (a refresh).</summary>
    internal async void RunSearch(bool keepView = false)
    {
        _searchTimer.Stop();
        if (!_connected)
        {
            if (_active && !_connecting)
                await ConnectAsync();
            return;
        }

        if (!_ready)
            return;

        int generation = ++_generation;
        FinderQuery query = BuildQuery();
        _lastRefresh = DateTime.UtcNow;
        FinderPage page;
        try
        {
            page = await _backend.SearchAsync(query, 0, PageSize);
        }
        catch (FinderUnavailableException)
        {
            if (generation == _generation)
                OnConnectionLost();
            return;
        }
        catch (FinderException ex)
        {
            // A query M-Finder cannot read (an unfinished regex...): show why and an empty list.
            if (generation != _generation || IsDisposed)
                return;

            ShowResults(query, new FinderPage(0, _revision, []), keepView: false);
            SetStatus(Loc.F("검색할 수 없습니다: {0}", ex.Message));
            return;
        }

        if (generation != _generation || IsDisposed)
            return;

        ShowResults(query, page, keepView);
        ShowCount();
    }

    private void ShowResults(FinderQuery query, FinderPage page, bool keepView)
    {
        _query = query;
        _revision = page.Revision;
        _pages.Clear();
        _pendingPages.Clear();
        _pages[0] = [.. page.Rows];

        int top = keepView && _list.TopItem is { } item ? item.Index : 0;
        if (!keepView)
        {
            _list.SelectedIndices.Clear();
            _lastSelected = -1;
        }

        _total = page.Total;
        _list.VirtualListSize = _total;
        if (_total > 0)
        {
            int visible = Math.Clamp(top, 0, _total - 1);
            if (!keepView)
                _list.EnsureVisible(0);
            else if (visible > 0)
                _list.TopItem = _list.Items[visible];
        }

        _list.Invalidate();
        if (!keepView)
            OnSelectionChanged();
    }

    private void ShowCount() => SetStatus(Loc.F("{0:N0}개 항목", _total));

    private void SortBy(int column)
    {
        if (column == _sortColumn)
        {
            _ascending = !_ascending;
        }
        else
        {
            _sortColumn = column;
            // Like Explorer: newest and biggest first.
            _ascending = column is not (ColumnSize or ColumnModified);
        }

        RunSearch();
    }

    /// <summary>The row at <paramref name="index"/> when its page is loaded; otherwise asks for the page and returns null.</summary>
    private FinderRow? RowAt(int index)
    {
        if (index < 0 || index >= _total)
            return null;

        int page = index / PageSize;
        if (_pages.TryGetValue(page, out FinderRow[]? rows))
            return index % PageSize < rows.Length ? rows[index % PageSize] : null;

        LoadPage(page);
        return null;
    }

    private async void LoadPage(int page)
    {
        if (!_pendingPages.Add(page))
            return;

        int generation = _generation;
        FinderQuery query = _query;
        FinderPage result;
        try
        {
            result = await _backend.SearchAsync(query, page * PageSize, PageSize);
        }
        catch (Exception ex) when (ex is FinderUnavailableException or FinderException)
        {
            if (generation == _generation)
                _pendingPages.Remove(page);
            return;
        }

        if (generation != _generation || IsDisposed)
            return;

        _pendingPages.Remove(page);
        _pages[page] = [.. result.Rows];
        int first = page * PageSize;
        int last = Math.Min(_total - 1, first + PageSize - 1);
        if (first <= last)
            _list.RedrawItems(first, last, invalidateOnly: true);
    }

    private ListViewItem CreateItem(int index)
    {
        if (RowAt(index) is not { } row)
            return new ListViewItem([string.Empty, string.Empty, string.Empty, string.Empty]);

        return new ListViewItem(
        [
            row.Name,
            row.Folder,
            row.Size is { } size && (!row.IsDirectory || size > 0) ? FormatSize(size) : string.Empty,
            row.Modified?.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty,
        ], IconFor(row))
        {
            ToolTipText = row.FullPath,
        };
    }

    internal static string FormatSize(long bytes) => bytes == 0 ? "0KB" : $"{(bytes + 1023) / 1024:N0}KB";

    // ---- selection and actions -------------------------------------------------------------------------------------

    private void OnSelectionChanged()
    {
        int selected = _list.SelectedIndices.Count == 1 ? _list.SelectedIndices[0] : -1;
        if (selected == _lastSelected)
            return;

        _lastSelected = selected;
        SelectionChanged?.Invoke(this, selected < 0 ? null : RowAt(selected));
    }

    private void SelectIndex(int index)
    {
        _list.SelectedIndices.Clear();
        _list.SelectedIndices.Add(index);
        _list.Items[index].Focused = true;
        _list.EnsureVisible(index);
    }

    private void ActivateSelected(bool showInFolder)
    {
        if (SelectedRow is not { } row)
            return;

        if (showInFolder)
            ShowInFolderRequested?.Invoke(this, row);
        else
            OpenRequested?.Invoke(this, row);
    }

    private IReadOnlyList<FinderRow> SelectedRows()
    {
        var rows = new List<FinderRow>();
        foreach (int index in _list.SelectedIndices)
        {
            if (RowAt(index) is { } row)
                rows.Add(row);
        }

        return rows;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Down or Keys.Enter && _total > 0)
        {
            _list.Focus();
            if (_list.SelectedIndices.Count == 0)
                SelectIndex(0);
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape && _searchBox.TextLength > 0)
        {
            _searchBox.Clear();
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter and Ctrl+Enter arrive as ItemActivate.
        if (e.KeyCode == Keys.Up && !e.Shift && _list.SelectedIndices is [0])
        {
            FocusSearch();
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.C && e.Control)
        {
            CopyToClipboard(row => row.FullPath);
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("열기(&O)", null, (_, _) => ActivateSelected(showInFolder: false)) { ShortcutKeyDisplayString = "Enter" };
        open.Font = new Font(open.Font, FontStyle.Bold);
        var showInFolder = new ToolStripMenuItem("폴더에서 보기(&F)", null, (_, _) => ActivateSelected(showInFolder: true)) { ShortcutKeyDisplayString = "Ctrl+Enter" };
        var preview = new ToolStripMenuItem("미리보기(&P)", null, (_, _) =>
        {
            if (SelectedRow is { } row)
                PreviewRequested?.Invoke(this, row);
        });
        var copyPath = new ToolStripMenuItem("전체 경로 복사(&C)", null, (_, _) => CopyToClipboard(row => row.FullPath)) { ShortcutKeyDisplayString = "Ctrl+C" };
        var copyName = new ToolStripMenuItem("이름 복사(&N)", null, (_, _) => CopyToClipboard(row => row.Name));
        menu.Items.AddRange([open, showInFolder, preview, new ToolStripSeparator(), copyPath, copyName]);
        menu.Opening += (_, e) =>
        {
            bool one = SelectedRow is not null;
            open.Enabled = showInFolder.Enabled = preview.Enabled = one;
            copyPath.Enabled = copyName.Enabled = _list.SelectedIndices.Count > 0;
            e.Cancel = _list.SelectedIndices.Count == 0;
        };
        return menu;
    }

    private void CopyToClipboard(Func<FinderRow, string> text)
    {
        IReadOnlyList<FinderRow> rows = SelectedRows();
        if (rows.Count == 0)
            return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(text)));
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard.
        }
    }

    private void StartDrag()
    {
        string[] paths = [.. SelectedRows().Select(row => row.FullPath).Where(path => File.Exists(path) || Directory.Exists(path))];
        if (paths.Length == 0)
            return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        _list.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    // ---- look ------------------------------------------------------------------------------------------------------

    private static ToolStripButton Toggle(string text, string tip) =>
        new(text) { CheckOnClick = true, ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text };

    private void SetStatus(string text) => _status.Text = text;

    private void ApplyListTheme()
    {
        if (!_list.IsHandleCreated)
            return;

        AppAppearance.SetControlTheme(_list.Handle, _darkMode);
        AppAppearance.SetHeaderTheme(NativeMethods.SendMessage(_list.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero), _darkMode);
    }

    private const uint LvmGetHeader = 0x101F;

    /// <summary>The shell's small icon for the result's kind (by extension, without touching the file), cached per kind.</summary>
    private int IconFor(FinderRow row)
    {
        string ext = Path.GetExtension(row.Name);
        // Programs, shortcuts and icons each have their own picture; they share the generic one of their kind here.
        string key = row.IsDirectory ? "<folder>" : ext.Length > 0 ? ext : "<file>";
        if (_iconIndex.TryGetValue(key, out int index))
            return index;

        index = -1;
        if (LoadIcon(row.IsDirectory ? "folder" : "file" + ext, row.IsDirectory) is { } bitmap)
        {
            if (_icons.Images.Count == 0)
                _icons.ImageSize = bitmap.Size;
            _icons.Images.Add(bitmap);
            index = _icons.Images.Count - 1;
        }

        _iconIndex[key] = index;
        return index;
    }

    private static unsafe Bitmap? LoadIcon(string name, bool folder)
    {
        const uint useFileAttributes = 0x10;
        const uint attributeDirectory = 0x10, attributeNormal = 0x80;
        IntPtr text = Marshal.StringToHGlobalUni(name);
        try
        {
            var info = default(ShFileInfo);
            if (NativeMethods.SHGetFileInfo(text, folder ? attributeDirectory : attributeNormal, ref info, (uint)sizeof(ShFileInfo),
                    useFileAttributes | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON) == 0 || info.Icon == IntPtr.Zero)
                return null;

            try
            {
                using Icon icon = Icon.FromHandle(info.Icon);
                return icon.ToBitmap();
            }
            finally
            {
                NativeMethods.DestroyIcon(info.Icon);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchTimer.Dispose();
            _pollTimer.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
