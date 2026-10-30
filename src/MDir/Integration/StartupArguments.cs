namespace MDir.Integration;

/// <summary>
/// What the command line asks for: <c>M-Dir.exe [options] [folder | workspace.mdr]...</c>.
/// <list type="bullet">
/// <item><c>-explore</c> (also <c>/e</c>): show the folder tree next to the folder, like Explorer's <c>/e</c>.</item>
/// <item><c>-new</c>: start a separate window even when the single-instance option is on.</item>
/// <item><c>-minimized</c>: start minimized (to the tray when that option is on).</item>
/// </list>
/// Options start with <c>-</c> or <c>/</c> (a path such as <c>/data</c> that exists is still taken as a folder).
/// Of several folders the first one wins, of several workspaces the first existing one.
/// </summary>
internal sealed record StartupArguments(string? WorkspaceFile, string? Folder, bool Explore, bool NewInstance, bool Minimized)
{
    public const string WorkspaceExtension = ".mdr";

    public static StartupArguments Empty { get; } = new(null, null, false, false, false);

    /// <summary>True when the arguments only ask to open something (so a running instance can be handed them).</summary>
    public bool HasTarget => WorkspaceFile is not null || Folder is not null;

    public static StartupArguments Parse(IEnumerable<string> args, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        string? workspace = null;
        string? folder = null;
        bool explore = false, newInstance = false, minimized = false;

        foreach (string raw in args)
        {
            string arg = raw.Trim();
            if (arg.Length == 0)
                continue;

            if (IsOption(arg, out string option))
            {
                switch (option.ToLowerInvariant())
                {
                    case "explore" or "e":
                        explore = true;
                        break;
                    case "new" or "newinstance":
                        newInstance = true;
                        break;
                    case "minimized" or "min":
                        minimized = true;
                        break;
                }

                continue;
            }

            if (arg.EndsWith(WorkspaceExtension, StringComparison.OrdinalIgnoreCase) && fileExists(arg))
                workspace ??= Path.GetFullPath(arg);
            else
                folder ??= arg;
        }

        return new StartupArguments(workspace, folder, explore, newInstance, minimized);
    }

    /// <summary>
    /// The arguments with existing relative paths made absolute: another process has another current directory,
    /// so a path handed over to it must not depend on it.
    /// </summary>
    public static IEnumerable<string> Absolutize(IEnumerable<string> args) =>
        args.Select(arg => !IsOption(arg.Trim(), out _) && (File.Exists(arg) || Directory.Exists(arg)) ? Path.GetFullPath(arg) : arg);

    private static bool IsOption(string arg, out string option)
    {
        option = string.Empty;
        if (arg.StartsWith("--", StringComparison.Ordinal))
            option = arg[2..];
        else if (arg[0] == '-')
            option = arg[1..];
        else if (arg[0] == '/' && arg.Length > 1 && arg.IndexOfAny(['\\', ':', '/'], 1) < 0 && !Directory.Exists(arg))
            option = arg[1..];

        return option.Length > 0;
    }
}
