using System.Diagnostics;

namespace MDir.Finder;

/// <summary>How to start M-Finder: an exe, or Python running the source package.</summary>
internal sealed record FinderProgram(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>
    /// Arguments to try when <see cref="Arguments"/> makes the program exit right away: an older M-Finder that does not
    /// know <c>--headless</c> is started hidden in its tray instead (<c>gui --minimized</c>).
    /// </summary>
    public IReadOnlyList<string>? FallbackArguments { get; init; }

    public FinderProgram WithFallbackArguments() => this with { Arguments = FallbackArguments ?? Arguments, FallbackArguments = null };
}

/// <summary>
/// Finds M-Finder and starts it without a window (<c>gui --headless</c>): it then only keeps the index up to date and
/// answers searches over its pipe. A running M-Finder (the user's own window, or one another M-Dir started) is used as it is.
/// </summary>
internal static class FinderLauncher
{
    private static readonly string[] ExeNames = ["M-Finder.exe", "MFinder.exe"];
    private static readonly string[] MinimizedArguments = ["gui", "--minimized"];

    // The source package is called mfinder in newer copies, filefinder in older ones.
    private static readonly string[] SourcePackages = ["mfinder", "filefinder"];

    /// <summary>
    /// The program to start for <paramref name="configured"/> (an exe, or a folder holding the exe or the
    /// <c>mfinder</c> source package), or the first one found in the usual places when it is empty. Null: none found.
    /// </summary>
    public static FinderProgram? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return FromPath(configured.Trim().Trim('"'));

        string baseDir = AppContext.BaseDirectory;
        string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] folders =
        [
            baseDir,
            Path.Combine(baseDir, "M-Finder"),
            Path.Combine(localPrograms, "M-Finder"),
            Path.Combine(programFiles, "M-Finder"),
        ];
        return folders.Select(FromPath).FirstOrDefault(program => program is not null);
    }

    /// <summary>The program for an exe path or a folder; null when it holds neither the exe nor the source package.</summary>
    public static FinderProgram? FromPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return new FinderProgram(path, ["gui", "--headless"], Path.GetDirectoryName(path) ?? string.Empty) { FallbackArguments = MinimizedArguments };

                // <package>\__main__.py (or any file of the package) picked in the dialog: use the source folder above it.
                string? folder = Path.GetDirectoryName(Path.GetDirectoryName(path));
                return folder is null ? null : FromSource(folder);
            }

            if (!Directory.Exists(path))
                return null;

            foreach (string name in ExeNames)
            {
                string exe = Path.Combine(path, name);
                if (File.Exists(exe))
                    return new FinderProgram(exe, ["gui", "--headless"], path) { FallbackArguments = MinimizedArguments };
            }

            return FromSource(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static FinderProgram? FromSource(string folder)
    {
        string? package = SourcePackages.FirstOrDefault(name => File.Exists(Path.Combine(folder, name, "__main__.py")));
        if (package is null)
            return null;

        // pythonw has no console window; the "py" launcher's windowless twin is pyw.
        string? python = FindOnPath("pythonw.exe") ?? FindOnPath("pyw.exe");
        return python is null
            ? null
            : new FinderProgram(python, ["-m", package, "gui", "--headless"], folder) { FallbackArguments = ["-m", package, .. MinimizedArguments] };
    }

    private static string? FindOnPath(string exe)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }

    /// <summary>Starts <paramref name="program"/>; it keeps running after M-Dir exits unless asked to quit.</summary>
    public static Process? Start(FinderProgram program)
    {
        var info = new ProcessStartInfo(program.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = program.WorkingDirectory,
        };
        foreach (string argument in program.Arguments)
            info.ArgumentList.Add(argument);

        return Process.Start(info);
    }
}
