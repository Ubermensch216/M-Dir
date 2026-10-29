using System.Text;
using MDir.Features;

namespace MDir.Tests;

public class FileHasherTests
{
    [Theory]
    [InlineData(HashAlgorithmKind.Sha256, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData(HashAlgorithmKind.Sha1, "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData(HashAlgorithmKind.Md5, "900150983cd24fb0d6963f7d28e17f72")]
    public async Task Compute_MatchesTheKnownTestVectorForAbc(HashAlgorithmKind kind, string expected)
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(dir.File("abc.txt"), "abc");

        Assert.Equal(expected, await FileHasher.ComputeAsync(dir.File("abc.txt"), kind));
    }

    [Fact]
    public async Task Compute_EmptyAndLargeFiles_AndProgressReachesOne()
    {
        using var dir = new TempDirectory();
        await File.WriteAllBytesAsync(dir.File("empty.bin"), []);
        await File.WriteAllBytesAsync(dir.File("big.bin"), new byte[5 * 1024 * 1024 + 123]);
        var reports = new List<double>();

        string empty = await FileHasher.ComputeAsync(dir.File("empty.bin"), HashAlgorithmKind.Sha256);
        string big = await FileHasher.ComputeAsync(dir.File("big.bin"), HashAlgorithmKind.Sha256, new Progress<double>(reports.Add));
        await Task.Delay(100);

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", empty);
        Assert.Equal(64, big.Length);
        Assert.Contains(1.0, reports);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public async Task Compute_CanBeCancelled_AndDoesNotLockTheFile()
    {
        using var dir = new TempDirectory();
        await File.WriteAllBytesAsync(dir.File("big.bin"), new byte[40 * 1024 * 1024]);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileHasher.ComputeAsync(dir.File("big.bin"), HashAlgorithmKind.Sha256, null, cancel.Token));

        File.Delete(dir.File("big.bin"));
    }

    [Fact]
    public async Task Compute_ReadsAFileThatAnotherProgramHasOpenForWriting()
    {
        using var dir = new TempDirectory();
        await using (var writer = new FileStream(dir.File("log.txt"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            await writer.WriteAsync("abc"u8.ToArray());
            await writer.FlushAsync();

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                await FileHasher.ComputeAsync(dir.File("log.txt"), HashAlgorithmKind.Sha256));
        }
    }

    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("  BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD \r\n")]
    [InlineData("SHA256: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad *abc.txt")]
    [InlineData("BA:78:16:BF:8F:01:CF:EA:41:41:40:DE:5D:AE:22:23:B0:03:61:A3:96:17:7A:9C:B4:10:FF:61:F2:00:15:AD")]
    public void Matches_AcceptsCommonFormats(string pasted) =>
        Assert.True(FileHasher.Matches("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", pasted));

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ae")]
    [InlineData("ba7816bf")]
    public void Matches_RejectsOtherTexts(string pasted) =>
        Assert.False(FileHasher.Matches("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", pasted));

    [Fact]
    public void ExtractHash_ReturnsLowercaseHexOrNull()
    {
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", FileHasher.ExtractHash("MD5 = 900150983CD24FB0D6963F7D28E17F72"));
        Assert.Null(FileHasher.ExtractHash("xyz"));
        Assert.Equal("SHA-256", FileHasher.Name(HashAlgorithmKind.Sha256));
    }
}

public class HardLinkTests
{
    [Fact]
    public void Create_MakesASecondNameForTheSameFile()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("original.txt"), "one");

        HardLinks.Create(dir.File("link.txt"), dir.File("original.txt"));
        File.AppendAllText(dir.File("link.txt"), "+two");

        Assert.Equal("one+two", File.ReadAllText(dir.File("original.txt")));
        File.Delete(dir.File("original.txt"));
        Assert.Equal("one+two", File.ReadAllText(dir.File("link.txt")));
    }

    [Fact]
    public void Create_RefusesFoldersMissingFilesAndExistingTargets()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("a.txt"), "a");
        File.WriteAllText(dir.File("b.txt"), "b");
        Directory.CreateDirectory(dir.File("folder"));

        Assert.Throws<ArgumentException>(() => HardLinks.Create(dir.File("link"), dir.File("folder")));
        Assert.Throws<FileNotFoundException>(() => HardLinks.Create(dir.File("link"), dir.File("missing.txt")));
        Assert.Throws<IOException>(() => HardLinks.Create(dir.File("b.txt"), dir.File("a.txt")));
        Assert.Equal("b", File.ReadAllText(dir.File("b.txt")));
    }

    [Fact]
    public void SuggestLinkPath_AvoidsExistingNames()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("report.docx"), "x");

        string first = HardLinks.SuggestLinkPath(dir.File("report.docx"));
        Assert.Equal(dir.File("report - 링크.docx"), first);

        File.WriteAllText(first, "x");
        Assert.Equal(dir.File("report - 링크 (2).docx"), HardLinks.SuggestLinkPath(dir.File("report.docx")));
    }
}
