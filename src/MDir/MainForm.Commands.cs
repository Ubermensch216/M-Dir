using System.ComponentModel;
using System.Runtime.InteropServices;
using MDir.Commands;
using MDir.Features;
using MDir.Layout;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;

namespace MDir;

// Commands: every action of the window is registered once, then shown as a menu item, a toolbar button and a
// keyboard shortcut. Keys are dispatched here (not by per-item menu shortcuts) so that the keys a text box or the
// shell view already handles (Ctrl+C, Delete, F2...) are left to them.
internal sealed partial class MainForm
{
    private readonly CommandRegistry _commands = new();
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 400 };
    private readonly List<(Command Command, ToolStripItem Item)> _toolbarItems = [];
    private ToolStrip? _toolbar;
    private MenuStrip? _menu;
    private FavoritesMenu? _favoritesMenu;
    private bool _fullScreen;
    private FormBorderStyle _borderBeforeFullScreen;
    private FormWindowState _stateBeforeFullScreen;

    /// <summary>The clipboard used for file copy/cut/paste and path text. Tests replace it so the user's clipboard is left alone.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IClipboardService FileClipboard { get; set; } = new SystemClipboard();

    /// <summary>Starts "open here" programs. Tests replace it so no windows open.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IProcessLauncher ProcessLauncher { get; set; } = new SystemProcessLauncher();

    /// <summary>No shell dialogs and prompts during file operations (tests, scripted use).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool SilentFileOperations { get; set; }

    /// <summary>Shows an error to the user; replaced by tests to collect messages instead of blocking on a message box.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<string> ReportError { get; set; } = message => MessageBox.Show(message, "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    internal CommandRegistry Commands => _commands;

    internal ToolStrip? Toolbar => _toolbar;

    internal bool IsFullScreen => _fullScreen;

    /// <summary>Runs a command by id if it is enabled; returns whether it ran.</summary>
    internal bool RunCommand(string id) => Run(_commands.Get(id));

    private bool Run(Command command)
    {
        if (!command.IsEnabled)
            return false;

        try
        {
            command.Execute();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            ReportError(ex.Message);
        }

        RefreshCommandStates();
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        TryShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    /// <summary>
    /// Runs the command bound to <paramref name="keyData"/>. Returns false (the key is left to the focused control)
    /// when no command uses it, the command is disabled, or the key is a native default such as Ctrl+C.
    /// </summary>
    internal bool TryShortcut(Keys keyData) => _commands.FindByShortcut(keyData) is { } command && Run(command);

    private Command Add(string id, string text, string category, Action execute, Keys[]? keys = null, bool native = false,
        Func<bool>? can = null, Func<bool>? check = null, string? glyph = null) =>
        _commands.Add(new Command(id, text, execute)
        {
            Category = category,
            DefaultShortcuts = keys ?? [],
            NativeShortcut = native,
            CanExecute = can,
            IsChecked = check,
            Glyph = glyph,
        });

    private string? CurrentFolderPath => ActiveBrowser.CurrentLocation?.FileSystemPath is { } path && Directory.Exists(path) ? path : null;

    private bool HasSelection => ActiveBrowser.SelectedCount > 0;

    private void RegisterCommands()
    {
        // File
        const string file = "파일", edit = "편집", view = "보기", go = "이동", favorites = "즐겨찾기", tabs = "탭", layout = "레이아웃", tools = "도구", window = "창", help = "도움말";
        Add("file.newWorkspace", "새 작업 공간(&N)", file, NewWorkspace, [Keys.Control | Keys.N]);
        Add("file.openWorkspace", "작업 공간 열기(&O)...", file, OpenWorkspace, [Keys.Control | Keys.O]);
        Add("file.saveWorkspace", "작업 공간 저장(&S)", file, SaveWorkspace, [Keys.Control | Keys.S]);
        Add("file.saveWorkspaceAs", "다른 이름으로 저장(&A)...", file, SaveWorkspaceAs);
        Add("file.autoWorkspace", "종료 시 저장, 시작 시 복원(&W)", file, () => SetAutoWorkspace(!Settings.AutoWorkspace),
            can: () => _profile is not null, check: () => Settings.AutoWorkspace);
        Add("file.newFolder", "새 폴더(&F)", file, () => _ = NewFolderAsync(), [Keys.Control | Keys.Shift | Keys.N],
            can: () => CurrentFolderPath is not null, glyph: "");
        Add("file.exit", "끝내기(&X)", file, Close, [Keys.Alt | Keys.F4], native: true);

        // Edit: the shell view handles these keys itself while it has focus, so the defaults are native.
        Add("edit.cut", "잘라내기(&T)", edit, () => CopySelectionToClipboard(cut: true), [Keys.Control | Keys.X], native: true, can: () => HasSelection, glyph: "");
        Add("edit.copy", "복사(&C)", edit, () => CopySelectionToClipboard(cut: false), [Keys.Control | Keys.C], native: true, can: () => HasSelection, glyph: "");
        Add("edit.paste", "붙여넣기(&P)", edit, PasteFromClipboard, [Keys.Control | Keys.V], native: true,
            can: () => CurrentFolderPath is not null && FileClipboard.HasFiles(), glyph: "");
        Add("edit.delete", "삭제(&D)", edit, () => DeleteSelection(permanent: false), [Keys.Delete], native: true, can: () => HasSelection, glyph: "");
        Add("edit.deletePermanent", "영구 삭제(&E)", edit, () => DeleteSelection(permanent: true), [Keys.Shift | Keys.Delete], native: true, can: () => HasSelection);
        Add("edit.rename", "이름 바꾸기(&M)", edit, () => ActiveBrowser.BeginRename(), [Keys.F2], native: true, can: () => HasSelection);
        Add("edit.selectAll", "모두 선택(&A)", edit, () => ActiveBrowser.SelectAll(), [Keys.Control | Keys.A], native: true);
        Add("edit.invertSelection", "선택 반전(&I)", edit, () => ActiveBrowser.InvertSelection(), [Keys.Control | Keys.Shift | Keys.A]);
        Add("edit.properties", "속성(&R)", edit, () => ActiveBrowser.InvokeVerb("properties"), [Keys.Alt | Keys.Enter], native: true, can: () => HasSelection);
        Add("edit.copyPath", "전체 경로 복사", edit, () => CopyPathText(PathText.FullPath), [Keys.Control | Keys.Shift | Keys.C], can: () => HasSelection || CurrentFolderPath is not null);
        Add("edit.copyName", "파일 이름 복사", edit, () => CopyPathText(PathText.Name), can: () => HasSelection || CurrentFolderPath is not null);
        Add("edit.copyFolderPath", "폴더 경로 복사", edit, () => CopyPathText(PathText.Folder), can: () => CurrentFolderPath is not null);
        Add("edit.copyToNext", "다음 패널로 복사", edit, () => TransferToNextPane(move: false), [Keys.Control | Keys.Alt | Keys.C], can: () => HasSelection && NextVisiblePane() is not null);
        Add("edit.moveToNext", "다음 패널로 이동", edit, () => TransferToNextPane(move: true), [Keys.Control | Keys.Alt | Keys.M], can: () => HasSelection && NextVisiblePane() is not null);
        for (int i = 0; i < PaneLayout.MaxPanes; i++)
        {
            int index = i;
            Func<bool> canTransfer = () => HasSelection && TransferTargetFolder(_panes[index]) is not null;
            Add($"edit.copyToPane{i + 1}", $"패널 {i + 1}(으)로 복사", edit, () => TransferSelection(_panes[index], move: false), can: canTransfer);
            Add($"edit.moveToPane{i + 1}", $"패널 {i + 1}(으)로 이동", edit, () => TransferSelection(_panes[index], move: true), can: canTransfer);
        }

        // View
        for (int i = 0; i < ViewPreset.All.Count; i++)
        {
            ViewPreset preset = ViewPreset.All[i];
            Add($"view.preset.{i}", preset.Name, view, () => ActiveBrowser.SetViewMode(preset.Mode, preset.IconSize),
                check: () => preset.Matches(ActiveBrowser.ViewMode, ActiveBrowser.IconSize));
        }

        Add("view.refresh", "새로 고침(&R)", view, () => ActiveBrowser.RefreshView(), [Keys.F5], glyph: Glyphs.Refresh);
        Add("view.tree", "폴더 트리 표시(&T)", view, ToggleTree, [Keys.F9], check: () => IsTreeShown);
        Add("view.treePaneMode", "패널별 트리(&P)", view, () => SetSharedTreeMode(false), check: () => !_sharedTreeMode);
        Add("view.treeSharedMode", "공용 트리(&S)", view, () => SetSharedTreeMode(true), check: () => _sharedTreeMode);
        AddTreeOption("lines", "연결선 표시", o => o.Lines, (o, v) => o.Lines = v);
        AddTreeOption("fullRow", "전체 행 선택 (연결선 없음)", o => o.FullRowSelect, (o, v) => o.FullRowSelect = v);
        AddTreeOption("singleClick", "한 번 클릭으로 펼치기", o => o.SingleClickExpand, (o, v) => o.SingleClickExpand = v);
        AddTreeOption("autoExpand", "현재 폴더 자동 펼치기", o => o.AutoExpand, (o, v) => o.AutoExpand = v);
        Add("view.filter", "파일 필터(&I)", view, ToggleFilter, [Keys.Control | Keys.Shift | Keys.F], check: () => _activePane.FilterBarVisible, glyph: "");
        Add("view.colorFilter", "색상 필터 사용(&C)", view, () => SetColorFilterEnabled(!Settings.ColorFilterEnabled), check: () => Settings.ColorFilterEnabled);
        Add("view.editColorFilter", "색상 필터 편집(&E)...", view, EditColorFilter);
        Add("view.preview", "미리보기 창(&P)", view, TogglePreview, [Keys.F12], check: () => _previewVisible);

        // Go
        Add("go.back", "뒤로(&B)", go, () => ActiveBrowser.GoBack(), [Keys.Alt | Keys.Left], can: () => ActiveBrowser.CanGoBack, glyph: Glyphs.Back);
        Add("go.forward", "앞으로(&F)", go, () => ActiveBrowser.GoForward(), [Keys.Alt | Keys.Right], can: () => ActiveBrowser.CanGoForward, glyph: Glyphs.Forward);
        Add("go.up", "상위 폴더(&U)", go, () => ActiveBrowser.GoUp(), [Keys.Alt | Keys.Up], can: () => ActiveBrowser.CanGoUp, glyph: Glyphs.Up);
        Add("go.address", "주소창(&A)", go, () => _activePane.FocusAddressBar(), [Keys.F4, Keys.Alt | Keys.D, Keys.Control | Keys.L]);

        // Favorites
        _favoritesMenu = _favorites is { } store
            ? new FavoritesMenu(store, () => ActiveBrowser.CurrentLocation, (location, newTab) => _activePane.OpenFavorite(location, newTab), ManageFavorites)
            : null;
        Add("fav.add", "현재 폴더를 즐겨찾기에 추가(&A)", favorites, () => _favoritesMenu?.AddCurrent(_favorites!.Root), [Keys.Control | Keys.D], can: () => _favoritesMenu is not null);
        Add("fav.addQuick", "현재 폴더를 퀵링크에 추가(&Q)", favorites, () => _favoritesMenu?.AddCurrent(_favorites!.QuickLinkFolder), can: () => _favoritesMenu is not null);
        Add("fav.manage", "즐겨찾기 정리(&O)...", favorites, ManageFavorites, [Keys.Control | Keys.Shift | Keys.O], can: () => _favoritesMenu is not null);

        // Tabs
        Add("tab.new", "새 탭(&N)", tabs, NewTab, [Keys.Control | Keys.T, Keys.Control | Keys.Alt | Keys.A], glyph: Glyphs.Add);
        Add("tab.duplicate", "탭 복제(&D)", tabs, () => _activePane.DuplicateTab());
        Add("tab.close", "탭 닫기(&C)", tabs, () => _activePane.CloseTab(_activePane.SelectedTab), [Keys.Control | Keys.W], can: () => _activePane.Tabs.Count > 1);
        Add("tab.next", "다음 탭", tabs, () => _activePane.SelectNextTab(1), [Keys.Control | Keys.Tab, Keys.Control | Keys.PageDown]);
        Add("tab.previous", "이전 탭", tabs, () => _activePane.SelectNextTab(-1), [Keys.Control | Keys.Shift | Keys.Tab, Keys.Control | Keys.PageUp]);
        Add("tab.multiline", "여러 줄 탭(&M)", tabs, () => SetMultilineTabs(!_multilineTabs), check: () => _multilineTabs);

        // Layout and panes
        foreach (PaneLayout paneLayout in PaneLayout.All)
            Add($"layout.{paneLayout.Id}", paneLayout.Name, layout, () => _layoutPanel.CurrentLayout = paneLayout, check: () => _layoutPanel.CurrentLayout == paneLayout);

        Add("pane.next", "다음 패널(&N)", layout, () => ActivateNextPane(1), [Keys.F6]);
        Add("pane.previous", "이전 패널(&P)", layout, () => ActivateNextPane(-1), [Keys.Shift | Keys.F6]);
        for (int i = 0; i < PaneLayout.MaxPanes; i++)
        {
            int index = i;
            Add($"pane.{i + 1}", $"패널 {i + 1}(&{i + 1})", layout, () => ActivatePane(index), [Keys.Control | Keys.Alt | (Keys.D1 + i)], can: () => _layoutPanel.IsPaneVisible(index));
        }

        // Tools
        Add("tools.openCmd", "여기서 명령 프롬프트 열기(&C)", tools, () => OpenHere(OpenHereTarget.CommandPrompt), can: () => CurrentFolderPath is not null);
        Add("tools.openPowerShell", "여기서 PowerShell 열기(&P)", tools, () => OpenHere(OpenHereTarget.PowerShell), can: () => CurrentFolderPath is not null);
        Add("tools.openExplorer", "여기서 탐색기 열기(&E)", tools, () => OpenHere(OpenHereTarget.Explorer), can: () => CurrentFolderPath is not null);
        Add("tools.shortcuts", "단축키 편집(&K)...", tools, EditShortcuts);

        // Window
        Add("window.fullScreen", "전체 화면(&F)", window, ToggleFullScreen, [Keys.F11], check: () => _fullScreen);
        Add("window.hideMenu", "메뉴 막대 숨기기(&M)", window, ToggleMenuBar, [Keys.Alt | Keys.F11], check: () => _menu is { Visible: false });
        Add("window.toolbar", "도구 모음 표시(&T)", window, ToggleToolbar, check: () => _toolbar is { Visible: true });

        // Help
        Add("help.about", "M-Dir 정보(&A)", help, ShowAbout);

        _commands.ImportCustom(Settings.Shortcuts);

        void AddTreeOption(string name, string text, Func<TreeOptions, bool> get, Action<TreeOptions, bool> set) =>
            Add($"view.treeOption.{name}", text, view, () =>
            {
                set(TreeSettings, !get(TreeSettings));
                ApplyTreeOptions();
            }, check: () => get(TreeSettings));
    }

    private MenuStrip BuildMenu()
    {
        ToolStripMenuItem Top(string text, params ToolStripItem[] items)
        {
            var top = new ToolStripMenuItem(text);
            top.DropDownItems.AddRange(items);
            top.DropDownOpening += (_, _) => RefreshMenuItems(top.DropDownItems);
            return top;
        }

        ToolStripMenuItem Sub(string text, params ToolStripItem[] items)
        {
            var sub = new ToolStripMenuItem(text);
            sub.DropDownItems.AddRange(items);
            return sub;
        }

        ToolStripSeparator Sep() => new();
        ToolStripMenuItem Item(string id) => CreateMenuItem(_commands.Get(id));

        int layoutIcon = LogicalToDeviceUnits(16);
        ToolStripItem[] layoutItems = [.. PaneLayout.All.Select(paneLayout =>
        {
            ToolStripMenuItem item = Item($"layout.{paneLayout.Id}");
            item.Image = LayoutIcon.Create(paneLayout, layoutIcon);
            return (ToolStripItem)item;
        })];

        ToolStripItem[] File() =>
        [
            Item("file.newWorkspace"), Item("file.openWorkspace"), Item("file.saveWorkspace"), Item("file.saveWorkspaceAs"), Sep(),
            Item("file.newFolder"), Sep(),
            Item("file.autoWorkspace"), Sep(),
            Item("file.exit"),
        ];

        ToolStripItem[] Edit() =>
        [
            Item("edit.cut"), Item("edit.copy"), Item("edit.paste"), Sep(),
            Item("edit.delete"), Item("edit.deletePermanent"), Item("edit.rename"), Sep(),
            Item("edit.selectAll"), Item("edit.invertSelection"), Sep(),
            Sub("경로 복사(&P)", Item("edit.copyPath"), Item("edit.copyName"), Item("edit.copyFolderPath")),
            Sub("다른 패널로(&T)", Item("edit.copyToNext"), Item("edit.moveToNext"), Sep(),
                Item("edit.copyToPane1"), Item("edit.copyToPane2"), Item("edit.copyToPane3"), Item("edit.copyToPane4"), Sep(),
                Item("edit.moveToPane1"), Item("edit.moveToPane2"), Item("edit.moveToPane3"), Item("edit.moveToPane4")),
            Sep(),
            Item("edit.properties"),
        ];

        ToolStripItem[] View() =>
        [
            .. Enumerable.Range(0, ViewPreset.All.Count).Select(i => (ToolStripItem)Item($"view.preset.{i}")),
            Sep(),
            Item("view.refresh"), Sep(),
            Item("view.tree"), Item("view.treePaneMode"), Item("view.treeSharedMode"),
            Sub("트리 옵션(&O)", Item("view.treeOption.lines"), Item("view.treeOption.fullRow"), Item("view.treeOption.singleClick"), Item("view.treeOption.autoExpand")),
            Sep(),
            Item("view.filter"), Item("view.colorFilter"), Item("view.editColorFilter"), Sep(),
            Item("view.preview"),
        ];

        ToolStripItem[] Go() => [Item("go.back"), Item("go.forward"), Item("go.up"), Sep(), Item("go.address")];

        ToolStripItem[] Tabs() =>
        [
            Item("tab.new"), Item("tab.duplicate"), Item("tab.close"), Sep(),
            Item("tab.next"), Item("tab.previous"), Sep(),
            Item("tab.multiline"),
        ];

        ToolStripItem[] Layout() =>
        [
            .. layoutItems, Sep(),
            Item("pane.next"), Item("pane.previous"), Item("pane.1"), Item("pane.2"), Item("pane.3"), Item("pane.4"),
        ];

        ToolStripItem[] Tools() =>
        [
            Item("tools.openCmd"), Item("tools.openPowerShell"), Item("tools.openExplorer"), Sep(),
            Item("tools.shortcuts"),
        ];

        ToolStripItem[] Window() => [Item("window.fullScreen"), Item("window.hideMenu"), Item("window.toolbar")];

        var menu = new MenuStrip { Dock = DockStyle.Top };
        menu.Items.AddRange(
        [
            Top("파일(&F)", File()),
            Top("편집(&E)", Edit()),
            Top("보기(&V)", View()),
            Top("이동(&G)", Go()),
            BuildFavoritesMenu(),
            Top("탭(&T)", Tabs()),
            Top("레이아웃(&L)", Layout()),
            Top("도구(&O)", Tools()),
            Top("창(&W)", Window()),
            Top("도움말(&H)", Item("help.about")),
        ]);
        _menu = menu;
        return menu;
    }

    /// <summary>The favorites menu: the three fixed commands, then the favorites themselves, rebuilt each time it opens.</summary>
    private ToolStripMenuItem BuildFavoritesMenu()
    {
        var menu = new ToolStripMenuItem("즐겨찾기(&A)");
        menu.DropDownItems.Add(CreateMenuItem(_commands.Get("fav.add")));
        menu.DropDownItems.Add(CreateMenuItem(_commands.Get("fav.addQuick")));
        menu.DropDownItems.Add(CreateMenuItem(_commands.Get("fav.manage")));
        menu.DropDownItems.Add(new ToolStripSeparator());
        int fixedCount = menu.DropDownItems.Count;
        menu.DropDownOpening += (_, _) =>
        {
            RefreshMenuItems(menu.DropDownItems);
            FavoritesMenu.Clear(menu.DropDownItems, fixedCount);
            _favoritesMenu?.AddEntries(menu.DropDownItems);
        };
        return menu;
    }

    private ToolStripMenuItem CreateMenuItem(Command command)
    {
        var item = new ToolStripMenuItem(command.Text) { Tag = command };
        item.Click += (_, _) => Run(command);
        item.ShortcutKeyDisplayString = _commands.ShortcutText(command);
        return item;
    }

    private ToolStrip BuildToolbar()
    {
        var toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, CanOverflow = true, Visible = Settings.ShowToolbar };
        void Button(string id)
        {
            Command command = _commands.Get(id);
            var button = new ToolStripButton(command.Glyph ?? command.DisplayText)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Tag = command,
                Font = command.Glyph is null ? toolbar.Font : Glyphs.Font,
            };
            button.Click += (_, _) => Run(command);
            toolbar.Items.Add(button);
            _toolbarItems.Add((command, button));
        }

        foreach (string id in new[] { "go.back", "go.forward", "go.up", "view.refresh" })
            Button(id);
        toolbar.Items.Add(new ToolStripSeparator());
        foreach (string id in new[] { "tab.new", "file.newFolder" })
            Button(id);
        toolbar.Items.Add(new ToolStripSeparator());
        foreach (string id in new[] { "edit.cut", "edit.copy", "edit.paste", "edit.delete" })
            Button(id);
        toolbar.Items.Add(new ToolStripSeparator());
        foreach (string id in new[] { "view.tree", "view.filter", "view.preview" })
            Button(id);

        var layouts = new ToolStripDropDownButton("레이아웃") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "창 레이아웃" };
        int iconSize = LogicalToDeviceUnits(16);
        foreach (PaneLayout paneLayout in PaneLayout.All)
        {
            ToolStripMenuItem item = CreateMenuItem(_commands.Get($"layout.{paneLayout.Id}"));
            item.Image = LayoutIcon.Create(paneLayout, iconSize);
            layouts.DropDownItems.Add(item);
        }

        layouts.DropDownOpening += (_, _) => RefreshMenuItems(layouts.DropDownItems);
        toolbar.Items.Add(layouts);

        _toolbar = toolbar;
        return toolbar;
    }

    /// <summary>Brings the toolbar buttons in line with the current selection, clipboard and toggles.</summary>
    private void RefreshCommandStates()
    {
        foreach ((Command command, ToolStripItem item) in _toolbarItems)
        {
            bool enabled = command.IsEnabled;
            if (item.Enabled != enabled)
                item.Enabled = enabled;
            if (item is ToolStripButton button && command.IsChecked is { } isChecked && button.Checked != isChecked())
                button.Checked = !button.Checked;
        }
    }

    private void RefreshAllMenus()
    {
        if (_menu is null)
            return;

        foreach (ToolStripItem top in _menu.Items)
        {
            if (top is ToolStripMenuItem menuItem)
                RefreshMenuItems(menuItem.DropDownItems);
        }
    }

    /// <summary>Updates enabled, checked and shortcut text of the command items in a menu (and its sub-menus).</summary>
    private void RefreshMenuItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Tag is Command command && item is ToolStripMenuItem menuItem)
            {
                menuItem.Enabled = command.IsEnabled;
                menuItem.Checked = command.IsChecked?.Invoke() ?? false;
                menuItem.ShortcutKeyDisplayString = _commands.ShortcutText(command);
            }

            if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
                RefreshMenuItems(parent.DropDownItems);
        }
    }

    // ---- file operations -------------------------------------------------------------------------------------

    private FileOperations CreateFileOperations() => new(Handle, SilentFileOperations);

    private void ReportResult(FileOperationResult result)
    {
        // A cancelled operation is not an error; the shell has already told the user what it wanted to.
        if (!result.Success && !result.Aborted && result.Error is { } error)
            ReportError(error);
    }

    /// <summary>Puts the selected files on the clipboard as copied or cut, so they can be pasted here or in Explorer.</summary>
    internal void CopySelectionToClipboard(bool cut)
    {
        IReadOnlyList<string> paths = ActiveBrowser.GetSelectedPaths();
        if (paths.Count > 0)
            FileClipboard.SetFiles(paths, cut);
    }

    /// <summary>Pastes the clipboard's files into the active pane's folder (a cut moves them, a copy copies).</summary>
    internal void PasteFromClipboard()
    {
        if (CurrentFolderPath is not { } folder || FileClipboard.GetFiles() is not { } clipboard)
            return;

        FileOperationResult result = clipboard.Cut
            ? CreateFileOperations().Move(clipboard.Paths, folder)
            : CreateFileOperations().Copy(clipboard.Paths, folder);
        if (result.Success && clipboard.Cut)
            FileClipboard.ClearFiles();

        ReportResult(result);
        ActiveBrowser.RefreshView();
    }

    /// <summary>Deletes the selection to the Recycle Bin, or for good (the shell asks for confirmation).</summary>
    internal void DeleteSelection(bool permanent)
    {
        IReadOnlyList<string> paths = ActiveBrowser.GetSelectedPaths();
        if (paths.Count > 0)
            ReportResult(CreateFileOperations().Delete(paths, permanent));
    }

    /// <summary>Creates "새 폴더" in the active folder, selects it and starts renaming it.</summary>
    internal async Task NewFolderAsync()
    {
        if (CurrentFolderPath is not { } folder)
            return;

        (FileOperationResult result, string? path) = CreateFileOperations().NewFolder(folder, "새 폴더");
        ReportResult(result);
        if (path is null)
            return;

        // The new folder shows up in the view a moment after it exists.
        if (await ActiveBrowser.SelectPathAsync(path, TimeSpan.FromSeconds(3)) && !SilentFileOperations)
            ActiveBrowser.BeginRename();
    }

    private PaneControl? NextVisiblePane()
    {
        int count = _layoutPanel.CurrentLayout.PaneCount;
        return count < 2 ? null : _panes[(_activePane.Index + 1) % count];
    }

    /// <summary>The folder a transfer into <paramref name="target"/> would use, or null (not another visible pane, or no file system folder).</summary>
    private string? TransferTargetFolder(PaneControl target)
    {
        if (target == _activePane || !_layoutPanel.IsPaneVisible(target.Index))
            return null;

        return target.Browser.CurrentLocation?.FileSystemPath is { } path && Directory.Exists(path) ? path : null;
    }

    private void TransferToNextPane(bool move)
    {
        if (NextVisiblePane() is { } target)
            TransferSelection(target, move);
    }

    /// <summary>Copies or moves the active pane's selection into the folder another pane shows.</summary>
    internal void TransferSelection(PaneControl target, bool move)
    {
        IReadOnlyList<string> paths = ActiveBrowser.GetSelectedPaths();
        if (paths.Count == 0 || TransferTargetFolder(target) is not { } folder)
            return;

        FileOperations operations = CreateFileOperations();
        ReportResult(move ? operations.Move(paths, folder) : operations.Copy(paths, folder));
        target.Browser.RefreshView();
    }

    internal void CopyPathText(PathText kind)
    {
        IReadOnlyList<string> paths = ActiveBrowser.GetSelectedPaths();
        if (paths.Count == 0 && CurrentFolderPath is { } folder)
            paths = [folder];
        if (paths.Count == 0)
            return;

        FileClipboard.SetText(kind switch
        {
            PathText.Name => PathCommands.FileNames(paths),
            PathText.Folder => PathCommands.FolderPaths(paths),
            _ => PathCommands.FullPaths(paths),
        });
    }

    internal void OpenHere(OpenHereTarget target)
    {
        if (CurrentFolderPath is { } folder)
            ProcessLauncher.Start(PathCommands.CreateOpenHere(target, folder));
    }

    // ---- window ----------------------------------------------------------------------------------------------

    internal void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _borderBeforeFullScreen = FormBorderStyle;
            _stateBeforeFullScreen = WindowState;

            // A maximized window must be restored first, or removing the border leaves it the wrong size.
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _fullScreen = true;
        }
        else
        {
            FormBorderStyle = _borderBeforeFullScreen;
            WindowState = _stateBeforeFullScreen == FormWindowState.Minimized ? FormWindowState.Normal : _stateBeforeFullScreen;
            _fullScreen = false;
        }
    }

    private void ToggleMenuBar()
    {
        if (_menu is not null)
            _menu.Visible = !_menu.Visible;
    }

    internal void ToggleToolbar()
    {
        if (_toolbar is null)
            return;

        _toolbar.Visible = !_toolbar.Visible;
        Settings.ShowToolbar = _toolbar.Visible;
        SaveSettings();
    }

    private void ShowAbout() =>
        MessageBox.Show(this, $"M-Dir {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}\n\n여러 패널과 탭, 폴더 트리, 필터, 미리보기를 갖춘 파일 관리자입니다.\nWindows 탐색기의 파일 목록을 그대로 사용합니다.",
            "M-Dir 정보", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void EditShortcuts()
    {
        using var dialog = new ShortcutDialog(_commands);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        ApplyShortcuts(dialog.Result);
    }

    /// <summary>Applies the shortcut table chosen in the editor (command id to keys), saves it, and updates menu texts.</summary>
    internal void ApplyShortcuts(IReadOnlyDictionary<string, Keys[]> shortcuts)
    {
        foreach ((string id, Keys[] keys) in shortcuts)
        {
            if (_commands.TryGet(id, out _))
                _commands.SetShortcuts(id, keys);
        }

        Settings.Shortcuts.Clear();
        foreach ((string id, string text) in _commands.ExportCustom())
            Settings.Shortcuts[id] = text;

        SaveSettings();
        RefreshAllMenus();
    }
}

/// <summary>Which text the "copy path" commands put on the clipboard.</summary>
internal enum PathText
{
    FullPath,
    Name,
    Folder,
}
