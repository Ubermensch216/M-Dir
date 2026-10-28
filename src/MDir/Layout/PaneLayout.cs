namespace MDir.Layout;

/// <summary>Which way a split divides its area.</summary>
public enum SplitDirection
{
    /// <summary>Children side by side, separated by a vertical bar.</summary>
    Columns,

    /// <summary>Children stacked, separated by a horizontal bar.</summary>
    Rows,
}

/// <summary>A node of a layout's split tree: either a pane slot or a binary split.</summary>
public abstract record LayoutNode
{
    private LayoutNode()
    {
    }

    /// <summary>Pane slot; <see cref="Index"/> is the pane number (0 = top-left, in reading order).</summary>
    public sealed record Pane(int Index) : LayoutNode;

    /// <summary>
    /// Splits the area between <see cref="First"/> (left/top) and <see cref="Second"/>.
    /// The ratio is the share of <see cref="First"/> in 1/<see cref="PaneLayout.RatioScale"/> units.
    /// </summary>
    public sealed record Split(SplitDirection Direction, LayoutNode First, LayoutNode Second, int DefaultRatio) : LayoutNode;
}

/// <summary>
/// One of the window layouts (1 to 4 panes). Splitters are numbered in pre-order of the split tree,
/// which matches Q-Dir's <c>SP0</c>/<c>SP1</c>/<c>SP2</c> workspace keys for the 4-pane grid.
/// </summary>
public sealed class PaneLayout
{
    /// <summary>Ratios are stored as parts of 10000, like Q-Dir's workspace files.</summary>
    public const int RatioScale = 10000;

    public const int MaxPanes = 4;

    private const int Half = RatioScale / 2;
    private const int Third = RatioScale / 3;
    private const int Quarter = RatioScale / 4;

    private PaneLayout(int id, string name, LayoutNode root)
    {
        Id = id;
        Name = name;
        Root = root;

        var ratios = new List<int>();
        PaneCount = Walk(root, ratios);
        DefaultRatios = ratios;
    }

    public int Id { get; }

    public string Name { get; }

    public LayoutNode Root { get; }

    public int PaneCount { get; }

    public int SplitterCount => DefaultRatios.Count;

    public IReadOnlyList<int> DefaultRatios { get; }

    public static IReadOnlyList<PaneLayout> All { get; } =
    [
        new(1, "1 패널", P(0)),
        new(2, "2 패널 - 좌우", Cols(P(0), P(1))),
        new(3, "2 패널 - 위아래", Rows(P(0), P(1))),
        new(4, "3 패널 - 세로 3열", Cols(P(0), Cols(P(1), P(2)), Third)),
        new(5, "3 패널 - 가로 3행", Rows(P(0), Rows(P(1), P(2)), Third)),
        new(6, "3 패널 - 왼쪽 1 + 오른쪽 2", Cols(P(0), Rows(P(1), P(2)))),
        new(7, "3 패널 - 왼쪽 2 + 오른쪽 1", Cols(Rows(P(0), P(1)), P(2))),
        new(8, "3 패널 - 위 1 + 아래 2", Rows(P(0), Cols(P(1), P(2)))),
        new(9, "3 패널 - 위 2 + 아래 1", Rows(Cols(P(0), P(1)), P(2))),
        new(10, "4 패널 - 2×2 (세로 분할선 기준)", Cols(Rows(P(0), P(2)), Rows(P(1), P(3)))),
        new(11, "4 패널 - 2×2 (가로 분할선 기준)", Rows(Cols(P(0), P(1)), Cols(P(2), P(3)))),
        new(12, "4 패널 - 세로 4열", Cols(P(0), Cols(P(1), Cols(P(2), P(3)), Third), Quarter)),
        new(13, "4 패널 - 가로 4행", Rows(P(0), Rows(P(1), Rows(P(2), P(3)), Third), Quarter)),
    ];

    /// <summary>Q-Dir's default: four panes in a grid.</summary>
    public static PaneLayout Default => All[9];

    public static PaneLayout? FromId(int id) => All.FirstOrDefault(layout => layout.Id == id);

    public override string ToString() => Name;

    private static LayoutNode P(int index) => new LayoutNode.Pane(index);

    private static LayoutNode Cols(LayoutNode first, LayoutNode second, int ratio = Half) =>
        new LayoutNode.Split(SplitDirection.Columns, first, second, ratio);

    private static LayoutNode Rows(LayoutNode first, LayoutNode second, int ratio = Half) =>
        new LayoutNode.Split(SplitDirection.Rows, first, second, ratio);

    private static int Walk(LayoutNode node, List<int> ratios)
    {
        if (node is LayoutNode.Split split)
        {
            ratios.Add(split.DefaultRatio);
            return Walk(split.First, ratios) + Walk(split.Second, ratios);
        }

        return 1;
    }
}
