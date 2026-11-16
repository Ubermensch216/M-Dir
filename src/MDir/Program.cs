using MDir.Integration;
using MDir.Localization;
using MDir.Settings;
using MDir.Shell;

namespace MDir;

static class Program
{
    /// <summary>Where language pack files (<c>Lang\*.lng</c>) are looked for: the data folder, then the program folder.</summary>
    internal static string[] LanguageDirectories(AppProfile profile) =>
        [Path.Combine(profile.Paths.DataDirectory, "Lang"), Path.Combine(AppContext.BaseDirectory, "Lang")];

    [STAThread]
    static void Main(string[] args)
    {
        // The elevated copy that reads NTFS volumes for the built-in M-Finder (see FinderVolumeHelper): no window.
        if (MDir.Finder.Engine.FinderVolumeHelper.IsHelperCommandLine(args))
        {
            Environment.ExitCode = MDir.Finder.Engine.FinderVolumeHelper.RunServer(args);
            return;
        }

        AppProfile profile = AppProfile.Load();
        StartupArguments startup = StartupArguments.Parse(args);

        // Single-instance mode: a second start hands its command line to the running window and ends.
        SingleInstance? single = null;
        if (profile.Settings.SingleInstance && !startup.NewInstance)
        {
            single = new SingleInstance();
            if (!single.IsFirst)
            {
                if (SingleInstance.TrySend("M-Dir", StartupArguments.Absolutize(args), TimeSpan.FromSeconds(3)))
                {
                    single.Dispose();
                    return;
                }

                // The first instance does not answer (hung or just closing): start normally instead of doing nothing.
                single.Dispose();
                single = null;
            }
        }

        ApplicationConfiguration.Initialize();
        AppAppearance.ApplyToProcess(profile.Settings.ColorMode);
        Loc.Set(profile.Settings.Language, LanguageDirectories(profile));

        // A .mdr file opens that workspace, a folder opens in pane 1.
        Workspace? workspace = null;
        string? workspacePath = null;
        if (startup.WorkspaceFile is { } file)
        {
            workspace = Workspace.Load(file);
            workspacePath = workspace is null ? null : file;
        }

        ShellLocation? folder = startup.Folder is { } text ? ShellLocation.Parse(text) : null;

        // Otherwise start where the last run ended (Q-Dir's start.qdr).
        if (workspace is null && profile.Settings.AutoWorkspace)
            workspace = Workspace.Load(profile.Paths.StartWorkspaceFile);

        using var form = new MainForm(folder, workspace, workspacePath, profile) { StartMinimized = startup.Minimized };
        if (startup.Explore)
            form.ShowTreeForExplore();
        if (single is not null)
            form.AttachSingleInstance(single);

        try
        {
            Application.Run(form);
        }
        finally
        {
            single?.Dispose();
        }
    }
}
