using Microsoft.Win32;

namespace MDir.Integration;

/// <summary>
/// The two things M-Dir can register for the current user (HKCU, so no administrator rights are needed): the
/// Explorer right-click entry on folders, and starting with Windows. Portable mode never calls this, so it leaves
/// the registry alone.
/// </summary>
internal sealed class ShellRegistration(string exePath, string classesKey = @"Software\Classes", string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    public const string MenuKeyName = "M-Dir";
    public const string MenuText = "M-Dir에서 열기";
    public const string RunValueName = "M-Dir";

    /// <summary>Folders, the white space inside a folder window, and drives. The argument differs: %1 is the clicked item, %V the open folder.</summary>
    private static readonly (string Path, string Argument)[] MenuTargets =
    [
        (@"Directory\shell", "%1"),
        (@"Directory\Background\shell", "%V"),
        (@"Drive\shell", "%1"),
    ];

    public string ExePath { get; } = exePath;

    /// <summary>The registration of the running program, in the real registry.</summary>
    public static ShellRegistration ForCurrentProcess() =>
        new(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "M-Dir.exe"));

    public bool IsContextMenuRegistered
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{classesKey}\Directory\shell\{MenuKeyName}\command");
            return key?.GetValue(null) as string == CommandFor("%1");
        }
    }

    public void RegisterContextMenu()
    {
        foreach ((string path, string argument) in MenuTargets)
        {
            using RegistryKey menu = Registry.CurrentUser.CreateSubKey($@"{classesKey}\{path}\{MenuKeyName}");
            menu.SetValue(null, MenuText);
            menu.SetValue("Icon", $"\"{ExePath}\"");
            using RegistryKey command = menu.CreateSubKey("command");
            command.SetValue(null, CommandFor(argument));
        }
    }

    public void UnregisterContextMenu()
    {
        foreach ((string path, _) in MenuTargets)
            Registry.CurrentUser.DeleteSubKeyTree($@"{classesKey}\{path}\{MenuKeyName}", throwOnMissingSubKey: false);
    }

    public bool IsAutoStart
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey);
            return key?.GetValue(RunValueName) as string == AutoStartCommand;
        }
    }

    public void SetAutoStart(bool enabled)
    {
        if (enabled)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(runKey);
            key.SetValue(RunValueName, AutoStartCommand);
        }
        else
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Started with Windows: minimized, so it waits in the tray (or the taskbar) instead of covering the desktop.</summary>
    private string AutoStartCommand => $"\"{ExePath}\" -minimized";

    private string CommandFor(string argument) => $"\"{ExePath}\" \"{argument}\"";
}
