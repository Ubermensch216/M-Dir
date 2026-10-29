using System.Diagnostics;
using MDir.Features;

namespace MDir.Tests;

/// <summary>The shell's IFileOperation on real files (silent mode: no dialogs). Deletes are permanent, not Recycle Bin.</summary>
public class FileOperationsTests
{
    private static FileOperations Silent => new(IntPtr.Zero, silent: true);

    private static string Make(TempDirectory dir, string relative, string content = "x")
    {
        string path = dir.File(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Copy_CopiesFilesAndFoldersIntoTheDestination()
    {
        using var dir = new TempDirectory();
        string file = Make(dir, @"src\a.txt", "alpha");
        Make(dir, @"src\folder\inner.txt", "inner");
        Directory.CreateDirectory(dir.File("dest"));

        FileOperationResult result = Silent.Copy([file, dir.File(@"src\folder")], dir.File("dest"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("alpha", File.ReadAllText(dir.File(@"dest\a.txt")));
        Assert.Equal("inner", File.ReadAllText(dir.File(@"dest\folder\inner.txt")));
        Assert.True(File.Exists(file), "a copy leaves the original");
    }

    [Fact]
    public void Move_RemovesTheOriginal()
    {
        using var dir = new TempDirectory();
        string file = Make(dir, @"src\a.txt", "alpha");
        Directory.CreateDirectory(dir.File("dest"));

        FileOperationResult result = Silent.Move([file], dir.File("dest"));

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(file));
        Assert.Equal("alpha", File.ReadAllText(dir.File(@"dest\a.txt")));
    }

    [Fact]
    public void Copy_WithANameCollision_KeepsBothFilesWhenSilent()
    {
        using var dir = new TempDirectory();
        string source = Make(dir, @"src\a.txt", "new content");
        Make(dir, @"dest\a.txt", "old content");

        FileOperationResult result = Silent.Copy([source], dir.File("dest"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("old content", File.ReadAllText(dir.File(@"dest\a.txt")));
        string[] files = Directory.GetFiles(dir.File("dest"));
        Assert.Equal(2, files.Length);
        Assert.Contains(files, f => File.ReadAllText(f) == "new content");
    }

    [Fact]
    public void Delete_Permanently_RemovesFilesAndFolders()
    {
        using var dir = new TempDirectory();
        string file = Make(dir, "a.txt");
        Make(dir, @"folder\inner.txt");

        FileOperationResult result = Silent.Delete([file, dir.File("folder")], permanent: true);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(dir.File("folder")));
    }

    [Fact]
    public void NewFolder_CreatesAndNumbersTheName()
    {
        using var dir = new TempDirectory();

        (FileOperationResult first, string? firstPath) = Silent.NewFolder(dir.Path, "새 폴더");
        (FileOperationResult second, string? secondPath) = Silent.NewFolder(dir.Path, "새 폴더");

        Assert.True(first.Success, first.Error);
        Assert.Equal(dir.File("새 폴더"), firstPath);
        Assert.True(Directory.Exists(firstPath));
        Assert.True(second.Success, second.Error);
        Assert.Equal(dir.File("새 폴더 (2)"), secondPath);
        Assert.True(Directory.Exists(secondPath));
    }

    [Fact]
    public void NamesWithPercentSigns_AreNotMangledByVariableExpansion()
    {
        using var dir = new TempDirectory();
        string file = Make(dir, @"src\100%PATH%.txt", "percent");
        Directory.CreateDirectory(dir.File("dest"));

        FileOperationResult result = Silent.Copy([file], dir.File("dest"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("percent", File.ReadAllText(dir.File(@"dest\100%PATH%.txt")));
    }

    [Fact]
    public void MissingSourceOrDestination_IsReportedWithoutThrowing()
    {
        using var dir = new TempDirectory();
        string file = Make(dir, "a.txt");

        FileOperationResult missingSource = Silent.Copy([dir.File("nope.txt")], dir.Path);
        FileOperationResult missingDestination = Silent.Copy([file], dir.File("no-such-folder"));
        FileOperationResult missingDelete = Silent.Delete([dir.File("nope.txt")], permanent: true);

        Assert.False(missingSource.Success);
        Assert.NotNull(missingSource.Error);
        Assert.False(missingDestination.Success);
        Assert.False(missingDelete.Success);
        Assert.True(File.Exists(file));
    }
}

public class PathCommandsTests
{
    [Fact]
    public void Texts_ListOnePerLine()
    {
        string[] paths = [@"C:\Users\me\a.txt", @"C:\Users\me\folder\", @"D:\"];

        Assert.Equal("C:\\Users\\me\\a.txt\r\nC:\\Users\\me\\folder\\\r\nD:\\", PathCommands.FullPaths(paths));
        Assert.Equal("a.txt\r\nfolder\r\nD:\\", PathCommands.FileNames(paths));
        Assert.Equal("C:\\Users\\me\r\nC:\\Users\\me\\folder\r\nD:\\", PathCommands.FolderPaths([@"C:\Users\me\a.txt", @"C:\Users\me\b.txt", @"C:\Users\me\folder\x.txt", @"D:\"]));
    }

    [Fact]
    public void FolderPaths_AreDistinct() =>
        Assert.Equal("C:\\a\r\nC:\\b", PathCommands.FolderPaths([@"C:\a\1.txt", @"C:\a\2.txt", @"C:\B\3.txt".Replace("B", "b")]));

    [Fact]
    public void OpenHere_BuildsTheRightProcesses()
    {
        ProcessStartInfo cmd = PathCommands.CreateOpenHere(OpenHereTarget.CommandPrompt, @"C:\My Folder");
        Assert.Equal("cmd.exe", cmd.FileName);
        Assert.Equal(@"C:\My Folder", cmd.WorkingDirectory);
        Assert.True(cmd.UseShellExecute, "a new console window");

        ProcessStartInfo ps = PathCommands.CreateOpenHere(OpenHereTarget.PowerShell, @"C:\My Folder");
        Assert.Equal("powershell.exe", ps.FileName);
        Assert.Equal(@"C:\My Folder", ps.WorkingDirectory);

        // The path is one argument, so spaces and quotes in it cannot add arguments.
        ProcessStartInfo explorer = PathCommands.CreateOpenHere(OpenHereTarget.Explorer, @"C:\A ""B"" folder");
        Assert.Equal("explorer.exe", explorer.FileName);
        Assert.Equal([@"C:\A ""B"" folder"], explorer.ArgumentList);
        Assert.False(explorer.UseShellExecute);
    }
}
