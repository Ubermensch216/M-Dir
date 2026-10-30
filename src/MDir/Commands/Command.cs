namespace MDir.Commands;

/// <summary>
/// One user-visible action. Menus, the toolbar and keyboard shortcuts all run the same command, so it is enabled,
/// disabled and checked the same everywhere.
/// </summary>
internal sealed class Command(string id, string text, Action execute)
{
    private static readonly System.Text.RegularExpressions.Regex AccessKeySuffix = new(@"\(&.\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public string Id { get; } = id;

    /// <summary>The Korean menu text, which is also the key of its translation.</summary>
    public string TextKey { get; } = text;

    /// <summary>Menu text in the current language, with the access key marked by '&amp;'.</summary>
    public string Text => Localization.Loc.T(TextKey);

    /// <summary><see cref="Category"/> in the current language.</summary>
    public string CategoryText => Localization.Loc.T(Category);

    /// <summary>The group shown in the shortcut editor ("파일", "편집"...).</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Shortcuts before the user changes anything; the first one is shown in menus.</summary>
    public Keys[] DefaultShortcuts { get; init; } = [];

    /// <summary>
    /// The shell view or a text box already handles the default keys (Ctrl+C, Delete...) when it has focus, so they
    /// must not be intercepted. Only a shortcut the user chose themselves is.
    /// </summary>
    public bool NativeShortcut { get; init; }

    public Action Execute { get; } = execute;

    /// <summary>Null means always available.</summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>Null means the command is not a toggle.</summary>
    public Func<bool>? IsChecked { get; init; }

    /// <summary>A Segoe Fluent Icons character for the toolbar, or null.</summary>
    public string? Glyph { get; init; }

    /// <summary>The text without the access key marker, for dialogs and tooltips.</summary>
    public string DisplayText => AccessKeySuffix.Replace(Text, string.Empty).Replace("&", string.Empty).Replace("...", string.Empty).Trim();

    public bool IsEnabled => CanExecute?.Invoke() ?? true;
}

/// <summary>All commands, and which keys run them.</summary>
internal sealed class CommandRegistry
{
    private readonly List<Command> _commands = [];
    private readonly Dictionary<string, Command> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Keys[]> _custom = new(StringComparer.Ordinal);
    private Dictionary<Keys, Command>? _lookup;

    public IReadOnlyList<Command> All => _commands;

    public Command Add(Command command)
    {
        if (!_byId.TryAdd(command.Id, command))
            throw new ArgumentException($"Command '{command.Id}' is already registered.", nameof(command));

        _commands.Add(command);
        _lookup = null;
        return command;
    }

    public Command Get(string id) => _byId.TryGetValue(id, out Command? command) ? command : throw new KeyNotFoundException($"Unknown command '{id}'.");

    public bool TryGet(string id, out Command? command) => _byId.TryGetValue(id, out command);

    /// <summary>The keys that run <paramref name="command"/> now: the user's choice if there is one, else the defaults.</summary>
    public IReadOnlyList<Keys> GetShortcuts(Command command) => _custom.TryGetValue(command.Id, out Keys[]? custom) ? custom : command.DefaultShortcuts;

    public bool IsCustomized(string id) => _custom.ContainsKey(id);

    /// <summary>Replaces a command's keys (an empty list removes the shortcut).</summary>
    public void SetShortcuts(string id, IReadOnlyList<Keys> keys)
    {
        Command command = Get(id);
        if (keys.SequenceEqual(command.DefaultShortcuts))
            _custom.Remove(id);
        else
            _custom[id] = [.. keys];

        _lookup = null;
    }

    public void ResetShortcuts(string id)
    {
        _custom.Remove(id);
        _lookup = null;
    }

    public void ResetAllShortcuts()
    {
        _custom.Clear();
        _lookup = null;
    }

    /// <summary>The command a key press runs, or null. Native default keys are left to the focused control.</summary>
    public Command? FindByShortcut(Keys keyData)
    {
        _lookup ??= BuildLookup();
        return _lookup.TryGetValue(keyData, out Command? command) ? command : null;
    }

    /// <summary>Another command that already uses <paramref name="key"/> (native defaults count), or null.</summary>
    public Command? FindConflict(Keys key, string excludeId) =>
        _commands.FirstOrDefault(command => command.Id != excludeId && GetShortcuts(command).Contains(key));

    /// <summary>The shortcut text shown next to a menu item ("Ctrl+T"), empty when there is none.</summary>
    public string ShortcutText(Command command) => GetShortcuts(command) is { Count: > 0 } keys ? FormatKeys(keys[0]) : string.Empty;

    /// <summary>The user's changes as text, for the settings file: command id to "Ctrl+X, Alt+Y" (empty = no shortcut).</summary>
    public IReadOnlyDictionary<string, string> ExportCustom() =>
        _custom.ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value.Select(FormatKeys)), StringComparer.Ordinal);

    /// <summary>Loads the user's changes; unknown commands and unreadable keys are ignored.</summary>
    public void ImportCustom(IReadOnlyDictionary<string, string> custom)
    {
        foreach ((string id, string text) in custom)
        {
            if (!_byId.ContainsKey(id))
                continue;

            var keys = new List<Keys>();
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseKeys(part, out Keys key))
                    keys.Add(key);
            }

            SetShortcuts(id, keys);
        }
    }

    public static string FormatKeys(Keys keys) => new KeysConverter().ConvertToInvariantString(keys) ?? keys.ToString();

    public static bool TryParseKeys(string text, out Keys keys)
    {
        keys = Keys.None;
        try
        {
            if (new KeysConverter().ConvertFromInvariantString(text) is Keys parsed && (parsed & Keys.KeyCode) != Keys.None)
            {
                keys = parsed;
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
        }

        return false;
    }

    private Dictionary<Keys, Command> BuildLookup()
    {
        var lookup = new Dictionary<Keys, Command>();
        foreach (Command command in _commands)
        {
            if (command.NativeShortcut && !IsCustomized(command.Id))
                continue;

            foreach (Keys key in GetShortcuts(command))
                lookup.TryAdd(key, command);
        }

        return lookup;
    }
}
