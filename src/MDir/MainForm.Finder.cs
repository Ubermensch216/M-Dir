using System.ComponentModel;
using System.Diagnostics;
using MDir.Finder;
using MDir.Finder.Engine;
using MDir.Layout;
using MDir.Localization;
using MDir.Panes;
using MDir.Settings;

namespace MDir;

// The M-Finder search panel: docked right, between the panes and the preview. M-Finder itself (Python) keeps the
// index and runs the searches; M-Dir starts it without a window when none is running and asks it over its pipe.
internal sealed partial class MainForm
{
    private readonly Splitter _finderSplitter = new() { Dock = DockStyle.Right, Width = 5, MinSize = 200, MinExtra = 300, Visible = false };
    private FinderPane? _finderPane;
    private FinderClient? _finderClient;
    private FinderEngine? _finderEngine;
    private bool _finderVisible;
    private int _finderSplit = Workspace.DefaultFinderSplit;
    private bool _applyingFinderWidth;
    private bool _finderStartedHere;

    /// <summary>The preview shows the result selected in the M-Finder panel, not the active pane's selection.</summary>
    private bool _previewFromFinder;
    private FinderRow? _finderPreviewRow;

    internal FinderPane? FinderPane => _finderPane;

    internal bool FinderVisible => _finderVisible;

    internal Splitter FinderSplitter => _finderSplitter;

    internal Splitter PreviewSplitter => _previewSplitter;

    /// <summary>Tests: answers searches instead of a real M-Finder (set before the panel is first shown).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IFinderBackend? FinderBackendOverride { get; set; }

    /// <summary>Shows or hides the M-Finder panel (Ctrl+Shift+M); shown, it takes the keyboard focus.</summary>
    internal void ToggleFinder()
    {
        _finderVisible = !_finderVisible;
        UpdateFinder();
        if (_finderVisible)
            _finderPane?.FocusSearch();
        RefreshCommandStates();
    }

    private void UpdateFinder()
    {
        if (_finderVisible && _finderPane is null)
            CreateFinderPane();

        if (_finderPane is not null)
            _finderPane.Visible = _finderVisible;
        _finderSplitter.Visible = _finderVisible;
        // After the visibility changes: a control shown for the first time gets its window then, and WinForms
        // re-reads the collection order from the windows' z-order, which can undo an order set earlier.
        ArrangeWorkArea();

        if (_finderVisible)
        {
            ApplyPreviewWidth();
            ApplyFinderWidth();
            _ = _finderPane!.ActivateAsync();
        }
        else
        {
            _finderPane?.Deactivate();
            if (_previewFromFinder)
            {
                _previewFromFinder = false;
                SchedulePreview();
            }
        }
    }

    private void CreateFinderPane()
    {
        IFinderBackend backend;
        Func<Task<string?>> ensureRunning;
        if (FinderBackendOverride is not null)
        {
            backend = FinderBackendOverride;
            ensureRunning = () => Task.FromResult<string?>(null);
        }
        else if (Settings.Finder.UsesExternal)
        {
            // An external M-Finder the user chose: talk to it over its pipe.
            backend = _finderClient ??= new FinderClient();
            ensureRunning = EnsureFinderRunningAsync;
        }
        else
        {
            // The search built into M-Dir.
            FinderEngine engine = _finderEngine ??= CreateFinderEngine();
            backend = engine;
            ensureRunning = () =>
            {
                engine.Start();
                return Task.FromResult<string?>(null);
            };
        }

        var pane = new FinderPane(backend, ensureRunning) { Dock = DockStyle.Right, Width = 300, DarkMode = IsDarkMode };
        pane.SelectionChanged += (_, row) => PreviewFinderRow(row, openPreview: false);
        pane.PreviewRequested += (_, row) => PreviewFinderRow(row, openPreview: true);
        pane.OpenRequested += (_, row) => OpenFinderRow(row);
        pane.ShowInFolderRequested += async (_, row) => await ShowFinderRowInFolderAsync(row);
        pane.LocateRequested += (_, _) => LocateFinder();

        _workArea.Controls.Add(pane);
        _finderPane = pane;
    }

    private string FinderDatabaseFile() =>
        Settings.Finder.DatabasePath.Length > 0
            ? Settings.Finder.DatabasePath
            : FinderEngine.DefaultDatabaseFile(_profile?.Paths.DataDirectory ?? Path.Combine(Path.GetTempPath(), "M-Dir"));

    private FinderEngine CreateFinderEngine() => new(Settings.Finder, FinderDatabaseFile(), AskFinderElevation);

    /// <summary>
    /// Asks (once, from the engine's background thread) whether the built-in search may start the elevated helper that
    /// reads NTFS drives through their MFT, and remembers the answer.
    /// </summary>
    private bool AskFinderElevation()
    {
        if (IsDisposed || !IsHandleCreated)
            return false;

        bool allow = false;
        try
        {
            Invoke(() =>
            {
                string text = Loc.T("NTFS 드라이브를 빠르게 색인하려면 관리자 권한이 필요합니다.\n\n허용하면 Windows가 권한 확인 창(UAC)을 한 번 표시하고, 관리자 권한으로 실행되는 M-Dir 도우미가 파일 이름·크기·날짜만 읽어 M-Dir에 전달합니다. 허용하지 않으면 폴더를 직접 훑어 색인하므로 처음 색인이 더 오래 걸립니다.\n\n허용할까요? (M-Finder 설정에서 바꿀 수 있습니다.)");
                allow = MessageBox.Show(this, text, "M-Finder", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                Settings.Finder.MftConsent = allow ? FinderMftConsent.Allow : FinderMftConsent.Deny;
                SaveSettings();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }

        return allow;
    }

    /// <summary>
    /// Puts the work area's controls in docking order. WinForms docks the last control of the collection first (on
    /// the outside), so from the end: preview, its splitter, M-Finder, its splitter (right side, outside in); shared
    /// tree, its splitter (left side); and the panes, which fill what is left, first. A splitter resizes the control
    /// its outer edge touches, so each splitter must come right after (inside of) the panel it belongs to; with the
    /// two splitters side by side the M-Finder one would resize the preview splitter instead (a wide empty strip)
    /// and the M-Finder panel could not be resized at all.
    /// </summary>
    private void ArrangeWorkArea()
    {
        Control?[] order =
        [
            _layoutPanel,
            _sharedSplitter, _sharedTree,
            _finderSplitter, _finderPane,
            _previewSplitter, _previewPane,
        ];
        int index = 0;
        foreach (Control? control in order)
        {
            if (control is not null && control.Parent == _workArea)
                _workArea.Controls.SetChildIndex(control, index++);
        }
    }

    private void PreviewFinderRow(FinderRow? row, bool openPreview)
    {
        _previewFromFinder = true;
        _finderPreviewRow = row;
        if (openPreview && !_previewVisible)
            TogglePreview();
        else
            SchedulePreview();
    }

    private void OpenFinderRow(FinderRow row)
    {
        string path = row.FullPath;
        if (row.IsDirectory)
        {
            // A folder opens in the active pane, like double-clicking it there.
            if (!ActiveBrowser.Navigate(path))
                ReportError($"'{path}' 폴더를 열 수 없습니다.");
            else
                _activePane.FocusBrowser();
            return;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = row.Folder });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ReportError(ex.Message);
        }
    }

    private async Task ShowFinderRowInFolderAsync(FinderRow row)
    {
        PaneControl pane = _activePane;
        await ShowItemAsync(pane, row.FullPath);
        ActivatePane(pane, focus: true);
    }

    /// <summary>
    /// Makes sure an M-Finder answers on the pipe, starting one without a window when none does. Returns null, or why
    /// it is not available (shown in the panel with a link to choose M-Finder's location).
    /// </summary>
    private async Task<string?> EnsureFinderRunningAsync()
    {
        FinderClient client = _finderClient ??= new FinderClient();
        if (await client.PingAsync())
            return null;

        if (FinderLauncher.Locate(Settings.Finder.ExternalPath) is not { } program)
            return Loc.F("'{0}'에서 M-Finder를 찾을 수 없습니다.", Settings.Finder.ExternalPath);

        // Newer M-Finders start without a window (--headless); one that does not know the option exits right away
        // (argument error), so it is started again hidden in its tray instead.
        for (bool fallback = false; ; fallback = true)
        {
            FinderProgram attempt = fallback ? program.WithFallbackArguments() : program;
            Process? process;
            try
            {
                process = FinderLauncher.Start(attempt);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                return Loc.F("M-Finder를 시작하지 못했습니다: {0}", ex.Message);
            }

            using (process)
            {
                _finderStartedHere = true;
                // Loading the saved index takes a moment; building one for the first time takes longer, but the pipe
                // answers before that (the panel then shows the indexing progress).
                DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(300);
                    if (await client.PingAsync())
                        return null;

                    if (process is { HasExited: true } && !await client.PingAsync())
                    {
                        _finderStartedHere = false;
                        if (!fallback && program.FallbackArguments is not null)
                            break;

                        return Loc.F("M-Finder가 바로 끝났습니다 (종료 코드 {0}). 지정한 M-Finder가 실행되는지 확인하세요.", process.ExitCode);
                    }
                }

                if (!(process is { HasExited: true }))
                    return Loc.T("M-Finder가 응답하지 않습니다.");
            }
        }
    }

    /// <summary>Ends the M-Finder this window started (one the user started themself keeps running).</summary>
    private void StopFinderIfStartedHere()
    {
        if (!_finderStartedHere || _finderClient is not { } client)
            return;

        _finderStartedHere = false;
        try
        {
            // The client never resumes on this thread, so waiting here cannot deadlock.
            client.QuitAsync().Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>Opens the M-Finder settings (what to index, exclusions, an optional external M-Finder) and applies them.</summary>
    private void LocateFinder()
    {
        FinderOptions edited = Settings.Finder.Clone();
        using var dialog = new FinderOptionsDialog(edited);
        if (ShowLocalized(dialog) != DialogResult.OK)
            return;

        if (edited.UsesExternal && FinderLauncher.FromPath(edited.ExternalPath) is null)
        {
            ReportError(Loc.T("M-Finder.exe나 M-Finder 소스의 mfinder\\__main__.py를 고르세요. 소스는 pythonw.exe가 PATH에 있어야 실행됩니다."));
            return;
        }

        string database = FinderDatabaseFile();
        Settings.Finder = edited;
        SaveSettings();

        // The panel and the engine are made again with the new options.
        if (_finderPane is not null)
        {
            _workArea.Controls.Remove(_finderPane);
            _finderPane.Dispose();
            _finderPane = null;
        }

        _finderEngine?.Dispose();
        _finderEngine = null;
        if (dialog.RebuildRequested)
        {
            try
            {
                File.Delete(database);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The index is then simply reused, or rebuilt when its options differ.
            }
        }

        if (!_finderVisible)
            ToggleFinder();
        else
            UpdateFinder();
    }

    private static Image? FinderIcon()
    {
        try
        {
            using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream("M-Finder.ico");
            if (stream is null)
                return null;

            // The 16 px picture; the toolbar is scaled by the window font, not by this image.
            using var icon = new Icon(stream, new Size(16, 16));
            return icon.ToBitmap();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }

    private void ApplyFinderWidth()
    {
        if (_finderPane is null || !_finderVisible)
            return;

        int available = _workArea.ClientSize.Width - _finderSplitter.Width;
        int others = _previewVisible && _previewPane is not null ? _previewPane.Width + _previewSplitter.Width : 0;
        int width = Math.Clamp(available * _finderSplit / PaneLayout.RatioScale, _finderSplitter.MinSize,
            Math.Max(_finderSplitter.MinSize, available - others - _finderSplitter.MinExtra));
        if (_finderPane.Width == width)
            return;

        _applyingFinderWidth = true;
        try
        {
            _finderPane.Width = width;
        }
        finally
        {
            _applyingFinderWidth = false;
        }
    }

    private void OnFinderSplitterMoved()
    {
        int available = _workArea.ClientSize.Width - _finderSplitter.Width;
        if (!_applyingFinderWidth && _finderPane is not null && available > 0)
            _finderSplit = Math.Clamp(_finderPane.Width * PaneLayout.RatioScale / available, 500, 9500);
    }
}
