using System.Globalization;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;

namespace MDir.Features;

/// <summary>A workspace read from a Q-Dir file, and what could not be carried over.</summary>
internal sealed record QdrImportResult(Workspace Workspace, IReadOnlyList<string> Notes);

/// <summary>
/// Reads a Q-Dir workspace (<c>.qdr</c>, such as <c>start.qdr</c>). Taken over: the folder of each pane (a PIDL in
/// the same <c>%XX</c> text as M-Dir's own files), its view mode, the splitter positions, the tree and preview
/// settings, the active pane and the window position. Not taken over: the layout number (only 0, the four-pane
/// default, is known), tabs, and the column/sort streams (shell property-bag data in a Q-Dir specific form).
/// </summary>
internal static class QdrImporter
{
    private const int PaneCount = PaneLayout.MaxPanes;

    public static QdrImportResult? Import(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses the text of a .qdr file; null when it is not one (no "Q-Dir Version" line and no folders).</summary>
    public static QdrImportResult? Parse(string text)
    {
        Dictionary<string, string> values = ReadValues(text);
        bool isQDir = text.TrimStart('﻿').StartsWith("Q-Dir", StringComparison.OrdinalIgnoreCase) || values.ContainsKey("Folder1");
        if (!isQDir)
            return null;

        var notes = new List<string>();
        PaneLayout layout = PaneLayout.Default;
        int layoutNumber = Int(values, "ViewMode", 0);
        if (layoutNumber != 0)
            notes.Add($"Q-Dir 레이아웃 번호 {layoutNumber}은(는) 대응하는 M-Dir 레이아웃을 알 수 없어 기본 4분할로 가져왔습니다.");

        ShellLocation fallback = Workspace.DefaultLocation;
        var panes = new List<PaneState>();
        for (int i = 1; i <= PaneCount; i++)
        {
            ShellLocation? folder = values.TryGetValue($"Folder{i}", out string? encoded) && IdListText.Decode(encoded) is { } bytes
                ? ShellLocation.TryFromIdList(bytes)
                : null;
            if (folder is null)
                notes.Add($"패널 {i}의 폴더를 읽을 수 없어 '내 PC'로 열었습니다.");

            int mode = Int(values, $"ViewMode{i}", (int)FolderViewMode.Details);
            FolderViewMode viewMode = Enum.IsDefined((FolderViewMode)mode) && (FolderViewMode)mode != FolderViewMode.Auto ? (FolderViewMode)mode : FolderViewMode.Details;
            panes.Add(new PaneState(
                [new TabState(folder ?? fallback, viewMode, 16)],
                0,
                Int(values, $"Tree{i}", 0) == 1,
                Math.Clamp(Int(values, $"Tree{i}split", Workspace.DefaultTreeSplit), 500, 9500)));
        }

        // SP0, SP1, SP2 are the splitter positions of the four-pane layout in the same order as M-Dir's ratios.
        List<int> ratios = [.. Enumerable.Range(0, layout.SplitterCount).Select(i => Math.Clamp(Int(values, $"SP{i}", layout.DefaultRatios[i]), 0, PaneLayout.RatioScale))];

        int previewPosition = Int(values, "VorschauPos", PaneLayout.RatioScale - Workspace.DefaultPreviewSplit);
        notes.Add("열 구성과 정렬, 탭은 Q-Dir 전용 형식이라 가져오지 않았습니다.");
        return new QdrImportResult(new Workspace
        {
            Layout = layout,
            Ratios = ratios,
            ActivePane = Math.Clamp(Int(values, "Focus", 0), 0, layout.PaneCount - 1),
            SharedTree = Int(values, "tree_mode", 0) == 1,
            PreviewVisible = Int(values, "Vorschau", 0) == 1,

            // Q-Dir stores where the preview's left edge is; M-Dir stores the preview's width.
            PreviewSplit = Math.Clamp(PaneLayout.RatioScale - previewPosition, 500, 9500),
            Window = WindowPlacement.TryParse(values.GetValueOrDefault("windowplacement")),
            Panes = panes,
        }, notes);
    }

    /// <summary>
    /// The settings of the file by name. A line holds <c>key=value</c> pairs joined by <c>&amp;</c> (Q-Dir writes a
    /// pane's folder and view mode on one line); lines that are only a continuation (blank, or an INI header) are skipped.
    /// </summary>
    private static Dictionary<string, string> ReadValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.TrimStart('﻿').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '[' || line[0] == ';')
                continue;

            // "windowplacement" has a '|' of its own and no '&'; every other line is split into its pairs.
            foreach (string pair in line.Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals > 0)
                    values.TryAdd(pair[..equals].Trim(), pair[(equals + 1)..].Trim());
            }
        }

        return values;
    }

    private static int Int(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out string? text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
}
