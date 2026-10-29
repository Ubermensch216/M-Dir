using System.ComponentModel;
using MDir.Features;
using MDir.Panes;

namespace MDir;

// Tools: list export, folder sizes, multi-rename, folder compare, checksums and hard links. Each command has a
// method without dialogs that does the work, so it can be driven by tests and scripts; the dialogs run through
// RunDialog, which tests replace.
internal sealed partial class MainForm
{
    private readonly FolderSizeCache _folderSizes = new();
    private CancellationTokenSource? _sizeCancel;
    private string _sizeKey = string.Empty;

    /// <summary>Shows a tool dialog; tests replace it to look at the dialog and close it without blocking.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Form, DialogResult> RunDialog { get; set; } = null!;

    /// <summary>Asks for the file a list is exported to (and its format); null = cancelled. Tests replace it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string, (string Path, ExportFormat Format)?> ExportTargetPicker { get; set; } = null!;

    /// <summary>Asks for the path of a new hard link, offering a suggestion; null = cancelled. Tests replace it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string, string?> LinkPathPicker { get; set; } = null!;

    private void InitializeToolHooks()
    {
        RunDialog = dialog => dialog.ShowDialog(this);
        ExportTargetPicker = PickExportTarget;
        LinkPathPicker = suggestion =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = "하드 링크 만들기",
                FileName = Path.GetFileName(suggestion),
                InitialDirectory = Path.GetDirectoryName(suggestion),
                OverwritePrompt = true,
            };
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        };
    }

    private void RegisterToolCommands()
    {
        const string file = "파일", view = "보기", tools = "도구";
        Add("file.exportList", "목록 내보내기(&E)...", file, ExportListWithDialog, can: () => ActiveBrowser.ItemCount > 0);
        Add("view.autoFolderSize", "선택한 폴더 크기 자동 계산(&Z)", view, () =>
        {
            Settings.AutoFolderSize = !Settings.AutoFolderSize;
            SaveSettings();
            UpdateSizeForSelection();
        }, check: () => Settings.AutoFolderSize);
        Add("tools.folderSize", "폴더 크기 계산(&S)", tools, () => _ = MeasureSelectionAsync(refresh: true),
            can: () => HasSelection || CurrentFolderPath is not null);
        Add("tools.multiRename", "여러 파일 이름 바꾸기(&R)...", tools, OpenMultiRename, [Keys.Control | Keys.R], can: () => HasSelection);
        Add("tools.compareFolders", "폴더 비교 및 동기화(&D)...", tools, OpenCompareFolders, can: () => ComparePair() is not null);
        Add("tools.hash", "파일 해시(&H)...", tools, OpenHash, can: () => HasSelection);
        Add("tools.createHardLink", "하드 링크 만들기(&L)...", tools, CreateHardLinkWithDialog, can: () => HasSelection);
    }

    // ---- list export -----------------------------------------------------------------------------------------

    /// <summary>The rows to export: the selection when something is selected, otherwise everything the list shows.</summary>
    internal IReadOnlyList<ExportRow> ExportRows()
    {
        IReadOnlyList<string> paths = ActiveBrowser.GetSelectedPaths();
        if (paths.Count == 0)
            paths = ActiveBrowser.GetAllItemPaths();

        var rows = new List<ExportRow>(paths.Count);
        foreach (string path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var directory = new DirectoryInfo(path);
                    rows.Add(new ExportRow(directory.Name, "파일 폴더", null, directory.LastWriteTime, path, true));
                }
                else if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    string extension = info.Extension.TrimStart('.');
                    rows.Add(new ExportRow(info.Name, extension.Length > 0 ? extension.ToUpperInvariant() + " 파일" : "파일", info.Length, info.LastWriteTime, path, false));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An item that vanished or cannot be read is left out of the list.
            }
        }

        return rows;
    }

    /// <summary>Writes the pane's list (or its selection) to <paramref name="path"/>; returns how many rows were written.</summary>
    internal int ExportList(string path, ExportFormat format, bool utf16 = false)
    {
        IReadOnlyList<ExportRow> rows = ExportRows();
        ListExporter.Write(path, rows, format, utf16);
        return rows.Count;
    }

    private void ExportListWithDialog()
    {
        string name = ActiveBrowser.CurrentLocation?.DisplayName ?? "목록";
        if (ExportTargetPicker(name) is not { } target)
            return;

        try
        {
            ExportList(target.Path, target.Format);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError("목록을 저장하지 못했습니다.\n" + ex.Message);
        }
    }

    private (string Path, ExportFormat Format)? PickExportTarget(string defaultName)
    {
        ExportFormat[] formats = [ExportFormat.Text, ExportFormat.Csv, ExportFormat.Html, ExportFormat.Xlsx];
        using var dialog = new SaveFileDialog
        {
            Title = "목록 내보내기",
            Filter = "텍스트 (*.txt)|*.txt|CSV (*.csv)|*.csv|HTML (*.html)|*.html|Excel (*.xlsx)|*.xlsx",
            FileName = string.Concat(defaultName.Split(Path.GetInvalidFileNameChars())),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;

        return (dialog.FileName, formats[Math.Clamp(dialog.FilterIndex - 1, 0, formats.Length - 1)]);
    }

    // ---- folder sizes ----------------------------------------------------------------------------------------

    /// <summary>The paths whose size is shown: the selection, or the folder itself when nothing is selected.</summary>
    private IReadOnlyList<string> SizePaths()
    {
        IReadOnlyList<string> selected = ActiveBrowser.GetSelectedPaths();
        return selected.Count > 0 ? selected : CurrentFolderPath is { } folder ? [folder] : [];
    }

    /// <summary>
    /// Adds up the selection (files and folders) and shows the total in the active pane's status bar while it
    /// counts. <paramref name="refresh"/> forgets remembered sizes first. Starting again cancels the run before.
    /// </summary>
    internal async Task MeasureSelectionAsync(bool refresh = false)
    {
        PaneControl pane = _activePane;
        IReadOnlyList<string> paths = SizePaths();
        _sizeCancel?.Cancel();
        if (paths.Count == 0)
        {
            pane.SizeText = string.Empty;
            return;
        }

        var cancel = _sizeCancel = new CancellationTokenSource();
        _sizeKey = SizeKey();
        if (refresh)
        {
            foreach (string path in paths)
                _folderSizes.Invalidate(path);
        }

        pane.SizeText = "|  크기 계산 중…";

        // Progress reports are posted to this thread and can arrive after the result: they must not overwrite it.
        bool finished = false;
        try
        {
            var progress = new Progress<long>(bytes =>
            {
                if (!finished && !cancel.IsCancellationRequested)
                    pane.SizeText = $"|  {PreviewPane.FormatSize(bytes)} 계산 중…";
            });
            SelectionSize size = await SelectionSizer.MeasureAsync(paths, _folderSizes, progress, cancel.Token);
            finished = true;
            if (!cancel.IsCancellationRequested)
                pane.SizeText = $"|  {PreviewPane.FormatSize(size.Bytes)}{(size.Complete ? string.Empty : " 이상")} ({size.Files:N0}개 파일)";
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Called when the active selection changed: a size shown before is stale, and the automatic mode measures anew.</summary>
    private void UpdateSizeForSelection()
    {
        // The shell also reports a changed selection when the same items are still selected (after a refresh);
        // that must not cancel a measurement that is still right.
        string key = SizeKey();
        if (key == _sizeKey && _activePane.SizeText.Length > 0)
            return;

        _sizeCancel?.Cancel();
        _sizeKey = key;
        _activePane.SizeText = string.Empty;
        if (Settings.AutoFolderSize && HasSelection)
            _ = MeasureSelectionAsync();
    }

    /// <summary>Identifies what a shown size belongs to: the folder and the selection in it.</summary>
    private string SizeKey() => CurrentFolderPath + "?" + string.Join('|', ActiveBrowser.GetSelectedPaths());

    // ---- multi-rename, compare, hash, hard link --------------------------------------------------------------

    internal void OpenMultiRename()
    {
        IReadOnlyList<string> paths = ActiveBrowser.GetSelectedPaths();
        if (paths.Count == 0)
            return;

        PaneControl pane = _activePane;
        using var dialog = new MultiRenameDialog(paths);
        dialog.Applied += (_, _) =>
        {
            _folderSizes.Clear();
            pane.Browser.RefreshView();
        };
        RunDialog(dialog);
    }

    /// <summary>The two folders to compare: the active pane's and the next visible pane's. Null when either is not a file system folder.</summary>
    internal (string Left, string Right)? ComparePair()
    {
        if (NextVisiblePane() is not { } other || CurrentFolderPath is not { } left || TransferTargetFolder(other) is not { } right)
            return null;

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ? null : (left, right);
    }

    internal void OpenCompareFolders()
    {
        if (ComparePair() is not { } pair || NextVisiblePane() is not { } other)
            return;

        PaneControl pane = _activePane;
        using var dialog = new CompareDialog(pair.Left, pair.Right)
        {
            CreateOperations = () => new FileOperations(Handle, SilentFileOperations, replaceExisting: SilentFileOperations),
        };
        dialog.FilesChanged += (_, _) =>
        {
            _folderSizes.Clear();
            pane.Browser.RefreshView();
            other.Browser.RefreshView();
        };
        RunDialog(dialog);
    }

    internal void OpenHash()
    {
        string[] files = [.. ActiveBrowser.GetSelectedPaths().Where(File.Exists)];
        if (files.Length == 0)
        {
            ReportError("해시를 계산할 파일을 선택하세요. (폴더는 계산할 수 없습니다.)");
            return;
        }

        using var dialog = new HashDialog(files, FileClipboard);
        RunDialog(dialog);
    }

    /// <summary>Creates a hard link at <paramref name="linkPath"/> to <paramref name="existingFile"/> (same volume, NTFS).</summary>
    internal bool CreateHardLink(string existingFile, string linkPath)
    {
        try
        {
            HardLinks.Create(linkPath, existingFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportError("하드 링크를 만들지 못했습니다.\n" + ex.Message);
            return false;
        }

        _folderSizes.Clear();
        ActiveBrowser.RefreshView();
        return true;
    }

    private void CreateHardLinkWithDialog()
    {
        IReadOnlyList<string> paths = ActiveBrowser.GetSelectedPaths();
        if (paths.Count != 1 || !File.Exists(paths[0]))
        {
            ReportError("하드 링크는 파일 하나를 선택했을 때 만들 수 있습니다.");
            return;
        }

        if (LinkPathPicker(HardLinks.SuggestLinkPath(paths[0])) is { } linkPath)
            CreateHardLink(paths[0], linkPath);
    }
}
