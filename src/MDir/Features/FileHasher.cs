using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MDir.Features;

public enum HashAlgorithmKind
{
    Sha256,
    Sha1,
    Md5,
}

/// <summary>File checksums, computed by streaming so any file size works and the file is never locked.</summary>
internal static partial class FileHasher
{
    private const int BufferSize = 1024 * 1024;

    public static string Name(HashAlgorithmKind kind) => kind switch
    {
        HashAlgorithmKind.Sha1 => "SHA-1",
        HashAlgorithmKind.Md5 => "MD5",
        _ => "SHA-256",
    };

    /// <summary>The checksum of the file as lowercase hex. <paramref name="progress"/> receives 0..1.</summary>
    public static async Task<string> ComputeAsync(string path, HashAlgorithmKind kind, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(kind switch
        {
            HashAlgorithmKind.Sha1 => HashAlgorithmName.SHA1,
            HashAlgorithmKind.Md5 => HashAlgorithmName.MD5,
            _ => HashAlgorithmName.SHA256,
        });

        byte[] buffer = new byte[BufferSize];
        long total = stream.Length, done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            if (total > 0)
                progress?.Report((double)done / total);
        }

        progress?.Report(1);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The checksum inside a pasted text: a bare hash, "SHA256: hash", or a line of <c>sha256sum</c> output
    /// ("hash *file"). Separators (spaces, colons, dashes) inside the hash are tolerated. Null when there is none.
    /// </summary>
    public static string? ExtractHash(string text)
    {
        // A contiguous run of hex digits that is not part of a longer word ("SHA256: <hash>", "<hash> *file").
        foreach (Match match in HexRun().Matches(text))
        {
            if (IsHashLength(match.Length))
                return match.Value.ToLowerInvariant();
        }

        // Otherwise the whole text may be a dump grouped in pairs ("BA:78:16:...", "ba 78 16 ...").
        string trimmed = text.Trim();
        if (GroupedHex().IsMatch(trimmed))
        {
            string compact = new([.. trimmed.Where(Uri.IsHexDigit)]);
            if (IsHashLength(compact.Length))
                return compact.ToLowerInvariant();
        }

        return null;
    }

    private static bool IsHashLength(int length) => length is 32 or 40 or 64 or 96 or 128;

    /// <summary>True when <paramref name="expected"/> (any common format) names the same checksum as <paramref name="actual"/>.</summary>
    public static bool Matches(string actual, string expected) =>
        ExtractHash(expected) is { } hash && string.Equals(hash, actual, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9A-Fa-f]{32,128}(?![0-9A-Za-z])")]
    private static partial Regex HexRun();

    [GeneratedRegex(@"^[0-9A-Fa-f]{2}(?:[ :\-]?[0-9A-Fa-f]{2})*$")]
    private static partial Regex GroupedHex();
}

/// <summary>Hard links (several names for one file on the same volume).</summary>
internal static partial class HardLinks
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    /// Creates <paramref name="linkPath"/> as another name of <paramref name="existingFile"/>. Fails (IOException) for a
    /// different volume, a file system without hard links, an existing target or missing permission.
    /// </summary>
    public static void Create(string linkPath, string existingFile)
    {
        if (Directory.Exists(existingFile))
            throw new ArgumentException("Hard links can only be made to files, not folders.", nameof(existingFile));
        if (!File.Exists(existingFile))
            throw new FileNotFoundException("The file does not exist.", existingFile);

        if (!CreateHardLink(linkPath, existingFile, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(Marshal.GetPInvokeErrorMessage(error).TrimEnd() + $" (0x{error:X})", error);
        }
    }

    /// <summary>A name that does not exist yet next to <paramref name="file"/> ("a - 링크.txt", "a - 링크 (2).txt").</summary>
    public static string SuggestLinkPath(string file)
    {
        string directory = Path.GetDirectoryName(file) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(file), extension = Path.GetExtension(file);
        string candidate = Path.Combine(directory, $"{stem} - 링크{extension}");
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(directory, $"{stem} - 링크 ({n}){extension}");

        return candidate;
    }
}
