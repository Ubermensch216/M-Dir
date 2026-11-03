using MDir.Layout;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Edit > Undo: the shell remembers the copies, moves and deletes this program made.</summary>
public class UndoTests
{
    [Fact]
    public void Undo_TakesBackTheLastCopy()
    {
        using var root = new TempDirectory();
        Directory.CreateDirectory(root.File("src"));
        Directory.CreateDirectory(root.File("dest"));
        File.WriteAllText(root.File(@"src\a.txt"), "hello");
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(root.File("src"))!);
        PaneLayout layout = PaneLayout.FromId(2)!;
        var workspace = new Workspace
        {
            Layout = layout,
            Ratios = layout.DefaultRatios,
            Panes = [basis.Panes[0], basis.Panes[0] with { Tabs = [new TabState(ShellLocation.Parse(root.File("dest"))!, FolderViewMode.Details, 16)] }, .. basis.Panes.Skip(2)],
        };

        Run(() => new MainForm(null, workspace, null, null) { Size = new Size(1300, 800), SilentFileOperations = true }, async form =>
        {
            var source = form.ActivePane.Browser;
            await WaitUntil(() => source.ItemCount == 1 && form.Panes[1].Browser.CurrentLocation is not null, "both panes shown");
            source.SelectAll();
            await WaitUntil(() => source.SelectedCount == 1, "file selected");
            await form.TransferSelectionAsync(form.Panes[1], move: false);
            Assert.True(File.Exists(root.File(@"dest\a.txt")));

            Assert.True(form.Panes[1].Browser.CanUndo);
            Assert.True(form.Panes[1].Browser.Undo());

            await WaitUntil(() => !File.Exists(root.File(@"dest\a.txt")), "the copy is gone again");
            Assert.True(File.Exists(root.File(@"src\a.txt")), "the original is untouched");
        });
    }
}
