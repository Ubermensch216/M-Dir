using System.ComponentModel;
using System.Diagnostics;
using MDir.Finder;
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
        IFinderBackend backend = FinderBackendOverride ?? (_finderClient ??= new FinderClient());
        Func<Task<string?>> ensureRunning = FinderBackendOverride is null ? EnsureFinderRunningAsync : () => Task.FromResult<string?>(null);
        var pane = new FinderPane(backend, ensureRunning) { Dock = DockStyle.Right, Width = 300, DarkMode = IsDarkMode };
        pane.SelectionChanged += (_, row) => PreviewFinderRow(row, openPreview: false);
        pane.PreviewRequested += (_, row) => PreviewFinderRow(row, openPreview: true);
        pane.OpenRequested += (_, row) => OpenFinderRow(row);
        pane.ShowInFolderRequested += async (_, row) => await ShowFinderRowInFolderAsync(row);
        pane.LocateRequested += (_, _) => LocateFinder();

        _workArea.Controls.Add(pane);
        _finderPane = pane;
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

        if (FinderLauncher.Locate(Settings.FinderPath) is not { } program)
        {
            return Settings.FinderPath.Length > 0
                ? Loc.F("'{0}'에서 M-Finder를 찾을 수 없습니다.", Settings.FinderPath)
                : Loc.T("M-Finder를 찾을 수 없습니다. 위치를 지정하세요.");
        }

        Process? process;
        try
        {
            process = FinderLauncher.Start(program);
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
                    return Loc.F("M-Finder가 바로 끝났습니다 (종료 코드 {0}). --headless를 지원하는 M-Finder인지 확인하세요.", process.ExitCode);
                }
            }
        }

        return Loc.T("M-Finder가 응답하지 않습니다.");
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

    /// <summary>Lets the user pick M-Finder.exe (or mfinder\__main__.py of a source copy) and connects to it.</summary>
    private void LocateFinder()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Loc.T("M-Finder 위치 지정"),
            Filter = Loc.T("M-Finder (M-Finder.exe, __main__.py)|M-Finder.exe;MFinder.exe;__main__.py|프로그램 (*.exe)|*.exe|모든 파일 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (Settings.FinderPath.Length > 0)
        {
            try
            {
                dialog.InitialDirectory = Directory.Exists(Settings.FinderPath) ? Settings.FinderPath : Path.GetDirectoryName(Settings.FinderPath) ?? string.Empty;
            }
            catch (ArgumentException)
            {
            }
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (FinderLauncher.FromPath(dialog.FileName) is null)
        {
            ReportError(Loc.T("M-Finder.exe나 M-Finder 소스의 mfinder\\__main__.py를 고르세요. 소스는 pythonw.exe가 PATH에 있어야 실행됩니다."));
            return;
        }

        Settings.FinderPath = dialog.FileName;
        SaveSettings();
        if (_finderPane is not null)
            _finderPane.Reset();
        if (!_finderVisible)
            ToggleFinder();
        else
            _ = _finderPane?.ActivateAsync();
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
