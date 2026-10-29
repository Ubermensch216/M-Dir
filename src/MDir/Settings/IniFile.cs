using System.Globalization;
using System.Text;

namespace MDir.Settings;

/// <summary>
/// Small INI document: <c>[section]</c> headers and <c>key=value</c> lines, names compared case-insensitively.
/// Comment lines (<c>;</c> or <c>#</c>) are dropped on load. Files are written as UTF-16 LE with a BOM like
/// Q-Dir's, and read as UTF-8 or UTF-16 (detected from the BOM).
/// </summary>
internal sealed class IniFile
{
    private readonly List<Section> _sections = [];

    public IEnumerable<string> SectionNames => _sections.Select(section => section.Name);

    public bool HasSection(string section) => Find(section) is not null;

    public string? Get(string section, string key) =>
        Find(section)?.Entries.TryGetValue(key, out string? value) == true ? value : null;

    public int GetInt(string section, string key, int fallback) =>
        int.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    public bool GetBool(string section, string key, bool fallback) =>
        Get(section, key)?.Trim() switch
        {
            "1" or "true" or "True" => true,
            "0" or "false" or "False" => false,
            _ => fallback,
        };

    public void Set(string section, string key, string value)
    {
        if (key.Length == 0 || key.AsSpan().IndexOfAny("=\r\n[]") >= 0)
            throw new ArgumentException($"Invalid INI key '{key}'.", nameof(key));
        if (section.Length == 0 || section.AsSpan().IndexOfAny("\r\n]") >= 0)
            throw new ArgumentException($"Invalid INI section '{section}'.", nameof(section));
        if (value.AsSpan().IndexOfAny("\r\n") >= 0)
            throw new ArgumentException("INI values cannot contain line breaks.", nameof(value));

        (Find(section) ?? Add(section)).Entries[key] = value;
    }

    public void Set(string section, string key, int value) => Set(section, key, value.ToString(CultureInfo.InvariantCulture));

    public void Set(string section, string key, bool value) => Set(section, key, value ? "1" : "0");

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        Section? current = null;
        foreach (string raw in text.TrimStart('﻿').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                current = end > 1 ? ini.Find(line[1..end]) ?? ini.Add(line[1..end]) : null;
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals > 0 && current is not null)
                current.Entries[line[..equals].TrimEnd()] = line[(equals + 1)..].TrimStart();
        }

        return ini;
    }

    /// <summary>Reads a file; a missing file gives an empty document. I/O errors are thrown.</summary>
    public static IniFile Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : new IniFile();

    public string ToText()
    {
        var text = new StringBuilder();
        foreach (Section section in _sections)
        {
            if (text.Length > 0)
                text.Append("\r\n");

            text.Append('[').Append(section.Name).Append("]\r\n");
            foreach (var (key, value) in section.Entries)
                text.Append(key).Append('=').Append(value).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>Writes through a temporary file so a crash never leaves a half-written settings file.</summary>
    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        string temp = path + ".tmp";
        File.WriteAllText(temp, ToText(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        File.Move(temp, path, overwrite: true);
    }

    private Section? Find(string name) =>
        _sections.FirstOrDefault(section => string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase));

    private Section Add(string name)
    {
        var section = new Section(name);
        _sections.Add(section);
        return section;
    }

    private sealed class Section(string name)
    {
        public string Name { get; } = name;

        public OrderedDictionary<string, string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
