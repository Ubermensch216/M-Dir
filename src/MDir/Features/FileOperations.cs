using System.Diagnostics;
using System.Runtime.InteropServices;
using MDir.Shell;

namespace MDir.Features;

/// <summary>The outcome of a file operation. <see cref="Error"/> is set when it could not be started or failed.</summary>
internal sealed record FileOperationResult(bool Success, bool Aborted, string? Error = null)
{
    public static FileOperationResult Ok { get; } = new(true, false);
}

/// <summary>
/// Copies, moves and deletes files through the shell's <c>IFileOperation</c>, the engine Explorer uses: it shows the
/// progress dialog, asks what to do about name collisions, uses the Recycle Bin and records undo information.
/// </summary>
internal sealed class FileOperations(IntPtr ownerWindow = default, bool silent = false, bool replaceExisting = false)
{
    private const uint FileAttributeDirectory = 0x10;

    /// <summary>Copy and move keep undo information so Explorer-style Ctrl+Z works afterwards.</summary>
    private uint TransferFlags => FileOperationFlags.AllowUndo | FileOperationFlags.AddUndoRecord | QuietFlags;

    /// <summary>
    /// For tests and scripted use: no dialogs, no prompts. A name collision gets a numbered name, or with
    /// <c>replaceExisting</c> the existing item is overwritten.
    /// </summary>
    private uint QuietFlags => silent
        ? FileOperationFlags.Silent | FileOperationFlags.NoConfirmation | FileOperationFlags.NoErrorUi | (replaceExisting ? 0 : FileOperationFlags.RenameOnCollision)
        : 0;

    public FileOperationResult Copy(IReadOnlyList<string> sources, string destinationFolder) =>
        Transfer(sources, destinationFolder, (operation, item, folder) => operation.CopyItem(item, folder, null, IntPtr.Zero));

    public FileOperationResult Move(IReadOnlyList<string> sources, string destinationFolder) =>
        Transfer(sources, destinationFolder, (operation, item, folder) => operation.MoveItem(item, folder, null, IntPtr.Zero));

    /// <summary>
    /// Deletes to the Recycle Bin, or for good when <paramref name="permanent"/> is set (the shell then asks for
    /// confirmation unless this object is silent).
    /// </summary>
    public FileOperationResult Delete(IReadOnlyList<string> sources, bool permanent)
    {
        uint flags = permanent
            ? FileOperationFlags.WantNukeWarning | QuietFlags
            : FileOperationFlags.AllowUndo | FileOperationFlags.AddUndoRecord | FileOperationFlags.RecycleOnDelete | QuietFlags;

        return Run(flags, operation =>
        {
            foreach (string source in sources)
            {
                if (ShellItems.FromPath(source) is not { } item)
                    return $"'{source}' 항목을 찾을 수 없습니다.";

                int hr = operation.DeleteItem(item, IntPtr.Zero);
                if (hr < 0)
                    return Describe(hr);
            }

            return null;
        });
    }

    /// <summary>Creates a folder in <paramref name="parent"/> and returns its path (the name is numbered if it is taken).</summary>
    public (FileOperationResult Result, string? Path) NewFolder(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            path = Path.Combine(parent, $"{name} ({n})");

        FileOperationResult result = Run(FileOperationFlags.NoConfirmMkdir | TransferFlags, operation =>
        {
            if (ShellItems.FromPath(parent) is not { } folder)
                return $"'{parent}' 폴더를 찾을 수 없습니다.";

            int hr = operation.NewItem(folder, FileAttributeDirectory, Path.GetFileName(path), null, IntPtr.Zero);
            return hr < 0 ? Describe(hr) : null;
        });

        return (result, result.Success ? path : null);
    }

    private FileOperationResult Transfer(IReadOnlyList<string> sources, string destinationFolder, Func<IFileOperation, IShellItem, IShellItem, int> queue) =>
        Run(TransferFlags, operation =>
        {
            if (ShellItems.FromPath(destinationFolder) is not { } folder)
                return $"'{destinationFolder}' 폴더를 찾을 수 없습니다.";

            foreach (string source in sources)
            {
                if (ShellItems.FromPath(source) is not { } item)
                    return $"'{source}' 항목을 찾을 수 없습니다.";

                int hr = queue(operation, item, folder);
                if (hr < 0)
                    return Describe(hr);
            }

            return null;
        });

    /// <summary>Creates the operation, lets <paramref name="queue"/> add items (returning an error text on failure), and performs it.</summary>
    private FileOperationResult Run(uint flags, Func<IFileOperation, string?> queue)
    {
        IFileOperation operation;
        try
        {
            operation = (IFileOperation)new FileOperationClass();
        }
        catch (COMException ex)
        {
            return new FileOperationResult(false, false, Describe(ex.HResult));
        }

        try
        {
            operation.SetOperationFlags(flags);
            if (ownerWindow != IntPtr.Zero)
                operation.SetOwnerWindow(ownerWindow);

            if (queue(operation) is { } error)
                return new FileOperationResult(false, false, error);

            int hr = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            return hr < 0 ? new FileOperationResult(false, aborted, Describe(hr)) : new FileOperationResult(!aborted, aborted);
        }
        finally
        {
            Marshal.FinalReleaseComObject(operation);
        }
    }

    private static string Describe(int hresult) => $"{Marshal.GetExceptionForHR(hresult)?.Message ?? "알 수 없는 오류"} (0x{hresult:X8})";
}

/// <summary>The system clipboard as far as file lists and text are concerned (replaceable so tests keep the user's clipboard).</summary>
internal interface IClipboardService
{
    /// <summary>Puts a file list on the clipboard as Explorer does: copied, or cut (to be moved when pasted).</summary>
    void SetFiles(IReadOnlyList<string> paths, bool cut);

    /// <summary>True when the clipboard holds a file list (cheap; used to enable Paste).</summary>
    bool HasFiles();

    /// <summary>The file list on the clipboard and whether it was cut; null when there is none.</summary>
    (IReadOnlyList<string> Paths, bool Cut)? GetFiles();

    /// <summary>Forgets the file list (after a pasted cut, the files are gone from where they were).</summary>
    void ClearFiles();

    void SetText(string text);
}

/// <summary>Uses the real clipboard, with the "Preferred DropEffect" format Explorer understands.</summary>
internal sealed class SystemClipboard : IClipboardService
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    public void SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        var data = new DataObject();
        data.SetFileDropList([.. paths]);
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy)));
        TrySet(() => Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 50));
    }

    public bool HasFiles()
    {
        try
        {
            return Clipboard.ContainsFileDropList();
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public (IReadOnlyList<string> Paths, bool Cut)? GetFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
                return null;

            string[] paths = [.. Clipboard.GetFileDropList().Cast<string>()];
            bool cut = Clipboard.TryGetData(DropEffectFormat, out MemoryStream? stream) && stream is { Length: >= 4 }
                && (BitConverter.ToInt32(stream.ToArray(), 0) & DropEffectMove) != 0;
            return paths.Length == 0 ? null : (paths, cut);
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard for a moment.
            return null;
        }
    }

    public void ClearFiles() => TrySet(Clipboard.Clear);

    public void SetText(string text) => TrySet(() => Clipboard.SetText(text));

    private static void TrySet(Action action)
    {
        try
        {
            action();
        }
        catch (ExternalException)
        {
        }
    }
}

/// <summary>Starts programs (replaceable so tests can check what would run without opening windows).</summary>
internal interface IProcessLauncher
{
    void Start(ProcessStartInfo info);
}

internal sealed class SystemProcessLauncher : IProcessLauncher
{
    public void Start(ProcessStartInfo info) => Process.Start(info)?.Dispose();
}

/// <summary>Which program "open here" starts.</summary>
internal enum OpenHereTarget
{
    CommandPrompt,
    PowerShell,
    Explorer,
}

/// <summary>Text and launch helpers for the path commands.</summary>
internal static class PathCommands
{
    /// <summary>One full path per line (what Explorer's "Copy as path" puts on the clipboard, without the quotes).</summary>
    public static string FullPaths(IEnumerable<string> paths) => string.Join("\r\n", paths);

    public static string FileNames(IEnumerable<string> paths) =>
        string.Join("\r\n", paths.Select(path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path));

    /// <summary>The folder that holds each item (the item itself for a drive root), one per line, without repeats.</summary>
    public static string FolderPaths(IEnumerable<string> paths) =>
        string.Join("\r\n", paths.Select(path => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The program and settings that open <paramref name="folder"/> in a new window of the given kind.</summary>
    public static ProcessStartInfo CreateOpenHere(OpenHereTarget target, string folder)
    {
        switch (target)
        {
            case OpenHereTarget.CommandPrompt:
                return new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true };
            case OpenHereTarget.PowerShell:
                return new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true };
            default:
                var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                info.ArgumentList.Add(folder);
                return info;
        }
    }
}
