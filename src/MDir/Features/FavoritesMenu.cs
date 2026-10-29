using MDir.Shell;

namespace MDir.Features;

/// <summary>
/// Builds the favorites menu entries from a <see cref="FavoritesStore"/>: shortcuts become items, folders become
/// sub-menus that are filled when opened. Click opens the favorite in the current tab; Ctrl+click or middle click
/// opens it in a new tab. The same builder serves the main menu and each pane's star button.
/// </summary>
internal sealed class FavoritesMenu
{
    private readonly FavoritesStore _store;
    private readonly Func<ShellLocation?> _currentLocation;
    private readonly Action<ShellLocation, bool> _open;
    private readonly Action _manage;

    /// <param name="currentLocation">The folder "add current folder" saves.</param>
    /// <param name="open">Opens a favorite: (location, in a new tab).</param>
    /// <param name="manage">Opens the management dialog.</param>
    public FavoritesMenu(FavoritesStore store, Func<ShellLocation?> currentLocation, Action<ShellLocation, bool> open, Action manage)
    {
        _store = store;
        _currentLocation = currentLocation;
        _open = open;
        _manage = manage;
    }

    /// <summary>Adds the current folder as a shortcut to <paramref name="folder"/> (the root or the Quick-Link folder).</summary>
    public void AddCurrent(string folder)
    {
        if (_currentLocation() is not { } location)
            return;

        try
        {
            _store.AddLink(folder, location);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"즐겨찾기에 추가하지 못했습니다.\n{ex.Message}", "M-Dir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>The fixed commands: add to favorites, add to Quick-Link, manage.</summary>
    public ToolStripItem[] CreateCommands() =>
    [
        new ToolStripMenuItem("현재 폴더를 즐겨찾기에 추가(&A)", null, (_, _) => AddCurrent(_store.Root)),
        new ToolStripMenuItem("현재 폴더를 퀵링크에 추가(&Q)", null, (_, _) => AddCurrent(_store.QuickLinkFolder)),
        new ToolStripMenuItem("즐겨찾기 정리(&O)...", null, (_, _) => _manage()),
    ];

    /// <summary>Appends the favorites themselves (the root folder's entries) to <paramref name="items"/>.</summary>
    public void AddEntries(ToolStripItemCollection items) => AddEntries(items, _store.Root);

    /// <summary>Removes (and disposes) everything after the first <paramref name="keep"/> items.</summary>
    public static void Clear(ToolStripItemCollection items, int keep = 0)
    {
        while (items.Count > keep)
        {
            ToolStripItem item = items[^1];
            items.RemoveAt(items.Count - 1);
            item.Image?.Dispose();
            item.Dispose();
        }
    }

    /// <summary>The 16x16 icon of a favorite: the shortcut's own icon if it names one, else the target's.</summary>
    public static Bitmap? LoadIcon(FavoritesStore store, FavoriteEntry entry)
    {
        if (entry.IsFolder)
            return ShellLocation.Parse(entry.Path)?.GetSmallIcon();

        ShellLinkInfo? info = store.Read(entry.Path);
        return (info?.IconPath is { } iconPath ? ShellLinks.LoadIcon(iconPath, info.IconIndex) : null) ?? info?.Target?.GetSmallIcon();
    }

    private void AddEntries(ToolStripItemCollection items, string folder)
    {
        IReadOnlyList<FavoriteEntry> entries = _store.List(folder);
        if (entries.Count == 0)
        {
            items.Add(new ToolStripMenuItem("(비어 있음)") { Enabled = false });
            return;
        }

        foreach (FavoriteEntry entry in entries)
            items.Add(entry.IsFolder ? CreateFolderItem(entry) : CreateLinkItem(entry));
    }

    private ToolStripMenuItem CreateFolderItem(FavoriteEntry entry)
    {
        bool quickLink = _store.IsQuickLinkFolder(entry.Path);
        var item = new ToolStripMenuItem(quickLink ? "퀵링크" : entry.Name) { Image = LoadIcon(_store, entry) };

        // Filled when opened, so a big favorites tree costs nothing until it is looked at.
        item.DropDownItems.Add(new ToolStripMenuItem("…"));
        item.DropDownOpening += (_, _) =>
        {
            Clear(item.DropDownItems);
            AddEntries(item.DropDownItems, entry.Path);
        };
        return item;
    }

    private ToolStripMenuItem CreateLinkItem(FavoriteEntry entry)
    {
        ShellLinkInfo? info = _store.Read(entry.Path);
        ShellLocation? target = info?.Target;
        var item = new ToolStripMenuItem(entry.Name)
        {
            Image = LoadIcon(_store, entry),
            Enabled = target is not null,
            ToolTipText = target?.DisplayName ?? "대상을 찾을 수 없는 바로 가기",
        };

        bool middle = false;
        item.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Middle)
            {
                middle = true;
                item.PerformClick();
            }
        };
        item.Click += (_, _) =>
        {
            bool newTab = middle || (Control.ModifierKeys & Keys.Control) != 0;
            middle = false;
            if (target is not null)
                _open(target, newTab);
        };
        return item;
    }
}
