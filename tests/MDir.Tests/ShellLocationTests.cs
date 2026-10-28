using MDir.Shell;

namespace MDir.Tests;

public class ShellLocationTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void Parse_FileSystemPath_RoundTripsDisplayName()
    {
        ShellLocation? location = ShellLocation.Parse(WindowsDir);

        Assert.NotNull(location);
        Assert.Equal(WindowsDir, location.DisplayName, ignoreCase: true);
        Assert.Equal(WindowsDir, location.FileSystemPath, ignoreCase: true);
    }

    [Fact]
    public void Parse_ExpandsEnvironmentVariablesAndQuotes()
    {
        Assert.Equal(ShellLocation.Parse(WindowsDir), ShellLocation.Parse("\"%SystemRoot%\""));
    }

    [Fact]
    public void Parse_ShellFolder_ResolvesVirtualFolderWithoutPath()
    {
        ShellLocation? thisPc = ShellLocation.Parse("shell:MyComputerFolder");

        Assert.NotNull(thisPc);
        Assert.False(thisPc.IsDesktop);
        Assert.Null(thisPc.FileSystemPath);
        Assert.NotEmpty(thisPc.DisplayName);
    }

    [Fact]
    public void Parse_Unresolvable_ReturnsNull()
    {
        Assert.Null(ShellLocation.Parse(@"Q:\does\not\exist\" + Guid.NewGuid()));
        Assert.Null(ShellLocation.Parse("   "));
    }

    [Fact]
    public void GetParent_WalksUpToDesktop()
    {
        ShellLocation windows = ShellLocation.Parse(WindowsDir)!;

        ShellLocation drive = windows.GetParent()!;
        Assert.Equal(Path.GetPathRoot(WindowsDir), drive.FileSystemPath, ignoreCase: true);

        ShellLocation thisPc = drive.GetParent()!;
        Assert.Equal(ShellLocation.Parse("shell:MyComputerFolder"), thisPc);

        Assert.True(thisPc.GetParent()!.IsDesktop);
        Assert.Null(ShellLocation.Desktop.GetParent());
    }

    [Fact]
    public void FromIdList_PreservesBytesAndEquality()
    {
        ShellLocation windows = ShellLocation.Parse(WindowsDir)!;
        ShellLocation copy = ShellLocation.FromIdList(windows.IdList);

        Assert.Equal(windows, copy);
        Assert.Equal(windows.GetHashCode(), copy.GetHashCode());
    }
}
