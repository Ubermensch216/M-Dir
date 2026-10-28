using MDir.Shell;

namespace MDir.Panes;

/// <summary>A named view-mode/icon-size pair, matching Explorer's View menu.</summary>
internal sealed record ViewPreset(string Name, FolderViewMode Mode, int IconSize)
{
    public static IReadOnlyList<ViewPreset> All { get; } =
    [
        new("아주 큰 아이콘", FolderViewMode.Icon, 256),
        new("큰 아이콘", FolderViewMode.Icon, 96),
        new("보통 아이콘", FolderViewMode.Icon, 48),
        new("작은 아이콘", FolderViewMode.SmallIcon, 16),
        new("목록", FolderViewMode.List, 16),
        new("자세히", FolderViewMode.Details, 16),
        new("타일", FolderViewMode.Tile, 48),
        new("내용", FolderViewMode.Content, 32),
    ];

    public bool Matches(FolderViewMode mode, int iconSize) =>
        Mode == mode && (Mode != FolderViewMode.Icon || IconSize == iconSize);
}
