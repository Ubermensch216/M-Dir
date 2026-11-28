using System.Security.Cryptography;

namespace MDir.Features.Hwp;

/// <summary>
/// Decrypts HWP 5.x distribution-only (<c>FLAG_DISTRIBUTION = 0x04</c>) <c>ViewText/Section{N}</c> streams.
/// Benchmarked from kordoc (<c>src/hwp5/crypto.ts</c>, <c>src/hwp5/aes.ts</c>):
/// <list type="number">
///   <item>Read the first record (<c>HWPTAG_DISTRIBUTE_DOC_DATA</c> = 28) with a 256-byte payload.</item>
///   <item>Decrypt the payload using MSVC LCG (<c>seed = seed * 214013 + 2531011</c>) + XOR.</item>
///   <item>Extract the 16-byte AES-128 key at <c>4 + (decrypted[0] &amp; 0x0F)</c>.</item>
///   <item>Decrypt the remaining bytes with AES-128-ECB (no padding) and inflate if compressed.</item>
/// </list>
/// </summary>
internal static class HwpCrypto
{
    internal const int TagDistributeDocData = 28; // HWPTAG_BEGIN (0x10) + 12

    /// <summary>Decrypts a <c>ViewText/Section{N}</c> stream into plain BodyText record bytes, or returns null on failure.</summary>
    public static byte[]? DecryptViewText(byte[] raw, bool compressed)
    {
        if (raw.Length < 4 + 256)
            return null;

        uint head = BitConverter.ToUInt32(raw, 0);
        int tag = (int)(head & 0x3FF);
        long size = head >> 20;
        int headerSize = 4;
        if (size == 0xFFF)
        {
            if (raw.Length < 8)
                return null;

            size = BitConverter.ToUInt32(raw, 4);
            headerSize = 8;
        }

        if (tag != TagDistributeDocData || size < 256 || headerSize + size > raw.Length)
            return null;

        byte[] payload = DecryptDistributePayload(raw.AsSpan(headerSize, 256));
        int keyOffset = 4 + (payload[0] & 0x0F);
        if (keyOffset + 16 > payload.Length)
            return null;

        byte[] key = payload[keyOffset..(keyOffset + 16)];
        int encryptedStart = headerSize + (int)size;
        int encryptedLen = raw.Length - encryptedStart;
        int alignedLen = encryptedLen - (encryptedLen % 16);
        if (alignedLen <= 0)
            return null;

        byte[] decrypted = DecryptAes128Ecb(raw.AsSpan(encryptedStart, alignedLen), key);
        if (!compressed)
            return decrypted;

        try
        {
            return HwpReader.Inflate(decrypted);
        }
        catch (InvalidDataException)
        {
            // Some distribution streams are stored uncompressed despite the header flag.
            return decrypted;
        }
    }

    /// <summary>XOR-decrypts (or encrypts, since XOR is symmetric) the 256-byte DISTRIBUTE_DOC_DATA payload.</summary>
    internal static byte[] DecryptDistributePayload(ReadOnlySpan<byte> payload)
    {
        byte[] result = payload[..256].ToArray();
        uint seed = BitConverter.ToUInt32(result, 0);
        int n = 0;
        byte key = 0;

        // Consume LCG steps starting from i=0 (skipping XOR for i<4 so the seed stays intact), matching MSVC/rhwp/kordoc.
        for (int i = 0; i < 256; i++)
        {
            if (n == 0)
            {
                key = (byte)(NextRand(ref seed) & 0xFF);
                n = (NextRand(ref seed) & 0x0F) + 1;
            }

            if (i >= 4)
                result[i] ^= key;

            n--;
        }

        return result;
    }

    internal static byte[] DecryptAes128Ecb(ReadOnlySpan<byte> cipherText, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes.DecryptEcb(cipherText, PaddingMode.None);
    }

    internal static byte[] EncryptAes128Ecb(ReadOnlySpan<byte> plainText, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes.EncryptEcb(plainText, PaddingMode.None);
    }

    private static int NextRand(ref uint seed)
    {
        seed = unchecked((seed * 214013u) + 2531011u);
        return (int)((seed >> 16) & 0x7FFF);
    }
}
