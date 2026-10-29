using System.Globalization;
using System.Text;

namespace MDir.Settings;

/// <summary>
/// Text form of an ITEMIDLIST for INI files: every byte as <c>%XX</c>, the same encoding Q-Dir uses for the
/// folders in its workspace files. Decoding is strict so a damaged value is rejected instead of reaching the shell.
/// </summary>
internal static class IdListText
{
    public static string Encode(ReadOnlySpan<byte> idList)
    {
        var text = new StringBuilder(idList.Length * 3);
        foreach (byte value in idList)
            text.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));

        return text.ToString();
    }

    /// <summary>Returns null unless <paramref name="text"/> is well-formed <c>%XX</c> bytes.</summary>
    public static byte[]? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length % 3 != 0)
            return null;

        var bytes = new byte[text.Length / 3];
        for (int i = 0; i < bytes.Length; i++)
        {
            ReadOnlySpan<char> group = text.AsSpan(i * 3, 3);
            if (group[0] != '%' || !byte.TryParse(group[1..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }

        return bytes;
    }
}
