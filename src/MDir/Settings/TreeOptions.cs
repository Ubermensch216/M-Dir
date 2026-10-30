namespace MDir.Settings;

/// <summary>Look and behaviour of the folder trees, shared by all trees and kept in <c>M-Dir.ini</c>.</summary>
internal sealed class TreeOptions
{
    private const string Section = "Tree";

    /// <summary>Connecting lines between folders. Ignored while <see cref="FullRowSelect"/> is on (the tree control cannot do both).</summary>
    public bool Lines { get; set; } = true;

    public bool FullRowSelect { get; set; }

    /// <summary>One click on a folder expands or collapses it.</summary>
    public bool SingleClickExpand { get; set; }

    /// <summary>The folder shown in the list is expanded in the tree as well as selected.</summary>
    public bool AutoExpand { get; set; } = true;

    /// <summary>Indentation of each tree level in pixels; 0 keeps the Windows default.</summary>
    public int Indent { get; set; }

    /// <summary>The indentation steps offered in the menu (0 = Windows default).</summary>
    public static readonly int[] IndentChoices = [0, 12, 24, 32];

    public static TreeOptions Read(IniFile ini) => new()
    {
        Indent = ini.GetInt(Section, nameof(Indent), 0) is var indent && indent >= 5 && indent <= 64 ? indent : 0,
        Lines = ini.GetBool(Section, nameof(Lines), true),
        FullRowSelect = ini.GetBool(Section, nameof(FullRowSelect), false),
        SingleClickExpand = ini.GetBool(Section, nameof(SingleClickExpand), false),
        AutoExpand = ini.GetBool(Section, nameof(AutoExpand), true),
    };

    public void Write(IniFile ini)
    {
        ini.Set(Section, nameof(Lines), Lines);
        ini.Set(Section, nameof(FullRowSelect), FullRowSelect);
        ini.Set(Section, nameof(SingleClickExpand), SingleClickExpand);
        ini.Set(Section, nameof(AutoExpand), AutoExpand);
        ini.Set(Section, nameof(Indent), Indent);
    }
}
