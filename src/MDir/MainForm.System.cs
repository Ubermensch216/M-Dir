using System.ComponentModel;
using MDir.Commands;
using MDir.Features;
using MDir.Integration;
using MDir.Localization;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;

namespace MDir;

// System integration: command-line hand-over from other processes, the tray icon, start with Windows, the Explorer
// context menu entry, dark mode, UI scale and the toolbar editor.
internal sealed partial class MainForm
{
    private NotifyIcon? _tray;
    private SingleInstance? _singleInstance;
    private Font? _baseFont;
    private bool _hiddenInTray;
    private bool _startMinimized;

    /// <summary>Reads and writes the registry entries for start-with-Windows and the Explorer menu. Tests replace it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ShellRegistration Registration { get; set; } = ShellRegistration.ForCurrentProcess();

    /// <summary>Starts minimized (to the tray when that option is on), e.g. when Windows starts the program.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool StartMinimized
    {
        get => _startMinimized;
        set => _startMinimized = value;
    }

    /// <summary>Whether dark colors are in use. "Follow Windows" is only honoured with a profile, so tests without one always run light.</summary>
    internal bool IsDarkMode => Settings.ColorMode switch
    {
        AppColorMode.Dark => true,
        AppColorMode.Light => false,
        _ => _profile is not null && AppAppearance.SystemPrefersDark(),
    };

    internal bool IsHiddenInTray => _hiddenInTray;

    internal NotifyIcon? TrayIcon => _tray;

    /// <summary>Whether the registry may be written: not in portable mode, and not without a profile (tests).</summary>
    private bool RegistryAllowed => _profile is { Paths.IsPortable: false };

    private void RegisterSystemCommands()
    {
        const string window = "창", tools = "도구";
        Add("tools.toolbar", "도구 모음 편집(&B)...", tools, EditToolbar);
        Add("window.minimizeToTray", "최소화하면 트레이로(&Y)", window, () => SetMinimizeToTray(!Settings.MinimizeToTray), check: () => Settings.MinimizeToTray);
        Add("window.singleInstance", "창 하나만 실행 (다시 시작하면 적용)", window, () => SetSingleInstance(!Settings.SingleInstance),
            can: () => _profile is not null, check: () => Settings.SingleInstance);
        Add("window.autoStart", "Windows 시작 시 실행", window, () => SetAutoStart(!SafeIsAutoStart()),
            can: () => RegistryAllowed, check: SafeIsAutoStart);
        Add("window.explorerMenu", "탐색기 폴더 메뉴에 'M-Dir에서 열기' 등록", window, () => SetExplorerMenu(!SafeIsMenuRegistered()),
            can: () => RegistryAllowed, check: SafeIsMenuRegistered);
        foreach (AppColorMode mode in Enum.GetValues<AppColorMode>())
        {
            AppColorMode captured = mode;
            string text = mode switch { AppColorMode.Light => "밝게", AppColorMode.Dark => "어둡게", _ => "시스템 설정 따르기" };
            Add($"window.colorMode.{mode}", text, window, () => SetColorMode(captured), check: () => Settings.ColorMode == captured);
        }

        foreach (string code in LanguageCodes())
        {
            string captured = code;
            string text = code switch { Loc.Korean => "한국어", Loc.English => "English", _ => code };
            Add($"window.language.{code}", text, window, () => SetLanguage(captured), check: () => string.Equals(Loc.Language, captured, StringComparison.OrdinalIgnoreCase));
        }

        foreach (int scale in AppAppearance.Scales)
        {
            int captured = scale;
            Add($"window.scale.{scale}", $"{scale}%", window, () => SetUiScale(captured), check: () => Settings.UiScale == captured);
        }
    }

    private ToolStripMenuItem[] SystemMenuItems(Func<string, ToolStripMenuItem> item)
    {
        var colorMode = new ToolStripMenuItem(Loc.T("화면 모드(&D)"));
        colorMode.DropDownItems.AddRange([.. Enum.GetValues<AppColorMode>().Select(mode => item($"window.colorMode.{mode}"))]);
        var scale = new ToolStripMenuItem(Loc.T("UI 배율(&Z)"));
        scale.DropDownItems.AddRange([.. AppAppearance.Scales.Select(value => item($"window.scale.{value}"))]);
        var language = new ToolStripMenuItem(Loc.T("언어(&L)"));
        language.DropDownItems.AddRange([.. LanguageCodes().Select(code => (ToolStripItem)item($"window.language.{code}"))]);
        language.DropDownOpening += (_, _) => RefreshMenuItems(language.DropDownItems);
        colorMode.DropDownOpening += (_, _) => RefreshMenuItems(colorMode.DropDownItems);
        scale.DropDownOpening += (_, _) => RefreshMenuItems(scale.DropDownItems);
        return [colorMode, scale, language];
    }

    // ---- other processes --------------------------------------------------------------------------------------

    /// <summary>
    /// Listens for arguments of later processes (single-instance mode). They arrive on a pipe thread and are
    /// handled on the UI thread.
    /// </summary>
    internal void AttachSingleInstance(SingleInstance singleInstance)
    {
        _singleInstance = singleInstance;
        singleInstance.ArgumentsReceived += args =>
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => OpenArguments(StartupArguments.Parse(args)));
        };
        singleInstance.StartListening();
    }

    /// <summary>
    /// Opens what a command line asks for in the running window: a workspace replaces the arrangement, a folder
    /// opens in a new tab of the active pane; the window comes to the front either way.
    /// </summary>
    internal void OpenArguments(StartupArguments startup)
    {
        if (startup.WorkspaceFile is { } file && Workspace.Load(file) is { } workspace)
        {
            ApplyWorkspace(workspace, applyWindow: false);
            _workspacePath = file;
        }

        if (startup.Folder is { } text && ShellLocation.Parse(text) is { } location)
            _activePane.OpenFavorite(location, newTab: true);

        if (startup.Explore)
            ShowTreeForExplore();

        ShowAndActivate();
    }

    /// <summary>"-explore": make sure the folder tree is visible, as in Explorer's /e.</summary>
    internal void ShowTreeForExplore()
    {
        if (_sharedTreeMode)
        {
            if (!_sharedTreeVisible)
                ToggleTree();
        }
        else if (!_activePane.TreeEnabled)
        {
            ToggleTree();
        }
    }

    /// <summary>Brings the window out of the tray or the taskbar and in front of other windows.</summary>
    internal void ShowAndActivate()
    {
        RestoreFromTray();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        Activate();
    }

    // ---- tray ---------------------------------------------------------------------------------------------------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && Settings.MinimizeToTray && !_hiddenInTray && IsHandleCreated)
            HideInTray();
    }

    internal void HideInTray()
    {
        if (_hiddenInTray)
            return;

        _tray ??= CreateTrayIcon();
        _tray.Visible = true;
        _hiddenInTray = true;
        ShowInTaskbar = false;
        Hide();
    }

    internal void RestoreFromTray()
    {
        if (!_hiddenInTray)
            return;

        _hiddenInTray = false;
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        if (_tray is not null)
            _tray.Visible = false;
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Loc.T("M-Dir 열기"), null, (_, _) => ShowAndActivate());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("끝내기"), null, (_, _) =>
        {
            RestoreFromTray();
            Close();
        });

        var tray = new NotifyIcon
        {
            Text = "M-Dir",
            Icon = AppIcon(),
            ContextMenuStrip = menu,
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowAndActivate();
        };
        return tray;
    }

    private static Icon AppIcon()
    {
        // The icon is embedded (Assets\M-Dir.ico), so it is found in tests and single-file builds alike.
        try
        {
            using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream("M-Dir.ico");
            if (stream is not null)
                return new Icon(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
        }

        return SystemIcons.Application;
    }

    private void SetMinimizeToTray(bool enabled)
    {
        Settings.MinimizeToTray = enabled;
        SaveSettings();
    }

    private void SetSingleInstance(bool enabled)
    {
        Settings.SingleInstance = enabled;
        SaveSettings();
    }

    // ---- registry -----------------------------------------------------------------------------------------------

    private bool SafeIsAutoStart() => Safe(() => Registration.IsAutoStart);

    private bool SafeIsMenuRegistered() => Safe(() => Registration.IsContextMenuRegistered);

    private bool Safe(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    internal void SetAutoStart(bool enabled)
    {
        try
        {
            Registration.SetAutoStart(enabled);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            ReportError(Loc.F("Windows 시작 항목을 바꾸지 못했습니다.\n{0}", ex.Message));
        }
    }

    internal void SetExplorerMenu(bool enabled)
    {
        try
        {
            if (enabled)
                Registration.RegisterContextMenu();
            else
                Registration.UnregisterContextMenu();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            ReportError(Loc.F("탐색기 메뉴를 바꾸지 못했습니다.\n{0}", ex.Message));
        }
    }

    // ---- appearance ---------------------------------------------------------------------------------------------

    internal void SetColorMode(AppColorMode mode)
    {
        Settings.ColorMode = mode;
        SaveSettings();
        ApplyAppearance(processMode: true);
    }

    /// <summary>
    /// Applies dark or light to the window frame, the file lists and the trees. <paramref name="processMode"/> also
    /// switches the WinForms controls (menu, tabs...), which the program has already done once at startup.
    /// </summary>
    internal void ApplyAppearance(bool processMode)
    {
        bool dark = IsDarkMode;
        if (processMode)
            AppAppearance.ApplyToProcess(dark ? AppColorMode.Dark : AppColorMode.Light);
        if (IsHandleCreated)
            AppAppearance.SetTitleBar(Handle, dark);

        foreach (var pane in _panes)
        {
            pane.DarkMode = dark;
            pane.Browser.HideSortHighlight = !Settings.SortColumnHighlight;
        }
        if (_sharedTree is not null)
            _sharedTree.DarkMode = dark;
        if (_finderPane is not null)
            _finderPane.DarkMode = dark;
        _previewPane?.ApplyTheme();
    }

    internal void SetSortHighlight(bool enabled)
    {
        Settings.SortColumnHighlight = enabled;
        SaveSettings();
        foreach (var pane in _panes)
            pane.Browser.HideSortHighlight = !enabled;
    }

    internal void SetUiScale(int percent)
    {
        Settings.UiScale = AppAppearance.NormalizeScale(percent);
        SaveSettings();
        ApplyUiScale();
    }

    /// <summary>Scales the font of the whole window (menu, toolbar, tabs, address bars and status bars follow it).</summary>
    internal void ApplyUiScale()
    {
        _baseFont ??= Font;
        float size = _baseFont.Size * Settings.UiScale / 100f;
        if (Math.Abs(Font.Size - size) < 0.01f)
            return;

        Font previous = Font;
        Font = new Font(_baseFont.FontFamily, size, _baseFont.Style);
        // Menus and tool bars keep their own default font instead of inheriting the window's.
        if (_menu is not null)
            _menu.Font = Font;
        if (_toolbar is not null)
            _toolbar.Font = Font;
        if (previous != _baseFont)
            previous.Dispose();

        FillToolbar();
    }

    // ---- language -----------------------------------------------------------------------------------------------

    /// <summary>The languages on offer: Korean, English and the language pack files found next to the program or in the data folder.</summary>
    private IReadOnlyList<string> LanguageCodes() =>
        _profile is null ? [Loc.Korean, Loc.English] : Loc.Available(Program.LanguageDirectories(_profile));

    /// <summary>Switches the language of the menus, tool bar, panes and dialogs at once and remembers the choice.</summary>
    internal void SetLanguage(string code)
    {
        string[] directories = _profile is null ? [] : Program.LanguageDirectories(_profile);
        Settings.Language = Loc.Set(code, directories);
        SaveSettings();
        ApplyLanguage();
    }

    /// <summary>Rebuilds what holds texts made once (menus, toolbar) and translates the panes after a language change.</summary>
    internal void ApplyLanguage()
    {
        RebuildMenu();
        FillToolbar();
        foreach (PaneControl pane in _panes)
            pane.ApplyLanguage();
        if (_previewPane is not null)
            Loc.Apply(_previewPane);
        _finderPane?.ApplyLanguage();

        UpdateActivePaneState();
        RefreshAllMenus();
    }

    /// <summary>Replaces the menu bar by a new one in the current language (keeping it hidden when it was).</summary>
    private void RebuildMenu()
    {
        MenuStrip? old = _menu;
        bool visible = old?.Visible ?? true;
        MenuStrip fresh = BuildMenu();
        fresh.Visible = visible;
        if (old is not null)
        {
            Controls.Remove(old);
            old.Dispose();
        }

        // Added last, so it docks above the toolbar like the first one did.
        Controls.Add(fresh);
        MainMenuStrip = fresh;
    }

    /// <summary>Shows a dialog after translating its texts.</summary>
    private DialogResult ShowLocalized(Form dialog)
    {
        Loc.Apply(dialog);
        return dialog.ShowDialog(this);
    }

    // ---- toolbar ------------------------------------------------------------------------------------------------

    private void EditToolbar()
    {
        using var dialog = new ToolbarDialog(_commands, ToolbarTokens);
        if (RunDialog(dialog) == DialogResult.OK)
            ApplyToolbar(dialog.Result);
    }

    /// <summary>Shows the toolbar made of <paramref name="tokens"/> and remembers it (the default toolbar is stored as nothing).</summary>
    internal void ApplyToolbar(IReadOnlyList<string> tokens)
    {
        IReadOnlyList<string> resolved = ToolbarLayout.Resolve(tokens, id => _commands.TryGet(id, out _));
        Settings.ToolbarItems.Clear();
        Settings.ToolbarItems.AddRange(ToolbarLayout.ToSettings(resolved));
        SaveSettings();
        FillToolbar();
    }
}
