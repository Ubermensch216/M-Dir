using MDir.Settings;
using MDir.Shell;

namespace MDir;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        AppProfile profile = AppProfile.Load();

        // A .mdr file opens that workspace, a folder opens in pane 1; "-options" are reserved for later.
        string? workspacePath = null;
        Workspace? workspace = null;
        ShellLocation? folder = null;
        foreach (string arg in args.Where(arg => !arg.StartsWith('-')))
        {
            if (arg.EndsWith(AppPaths.WorkspaceExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
            {
                workspace = Workspace.Load(arg);
                workspacePath = workspace is null ? null : Path.GetFullPath(arg);
            }
            else
            {
                folder ??= ShellLocation.Parse(arg);
            }
        }

        // Otherwise start where the last run ended (Q-Dir's start.qdr).
        if (workspace is null && profile.Settings.AutoWorkspace)
            workspace = Workspace.Load(profile.Paths.StartWorkspaceFile);

        Application.Run(new MainForm(folder, workspace, workspacePath, profile));
    }
}
