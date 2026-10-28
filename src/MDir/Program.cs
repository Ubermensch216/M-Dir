using MDir.Shell;

namespace MDir;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // A folder passed on the command line opens there; otherwise start at "This PC" like Q-Dir.
        ShellLocation start = (args.Length > 0 ? ShellLocation.Parse(args[0]) : null)
            ?? ShellLocation.Parse("shell:MyComputerFolder")
            ?? ShellLocation.Desktop;

        Application.Run(new MainForm(start));
    }
}
