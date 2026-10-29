using MDir.Features;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public class FilterSettingsTests
{
    [Fact]
    public void RecentFilters_AreNewestFirstWithoutDuplicatesAndCapped()
    {
        var settings = new AppSettings();
        settings.AddRecentFilter("*.txt");
        settings.AddRecentFilter("  ");
        settings.AddRecentFilter("*.md");
        settings.AddRecentFilter("*.TXT");

        Assert.Equal(["*.TXT", "*.md"], settings.RecentFilters);

        for (int i = 0; i < 30; i++)
            settings.AddRecentFilter($"f{i}");
        Assert.Equal(20, settings.RecentFilters.Count);
        Assert.Equal("f29", settings.RecentFilters[0]);
    }

    [Fact]
    public void ColourRulesAndRecentFilters_SurviveSaveAndLoad()
    {
        using var dir = new TempDirectory();
        string path = dir.File("M-Dir.ini");
        var settings = new AppSettings { ColorFilterEnabled = false };
        settings.ColorRules = new ColorRuleSet([new ColorRule("*.xyz", Text: Color.FromArgb(9, 8, 7), Bold: true)]);
        settings.AddRecentFilter("one");
        settings.AddRecentFilter("two");
        settings.Save(path);

        AppSettings loaded = AppSettings.Load(path);

        Assert.False(loaded.ColorFilterEnabled);
        Assert.Equal(settings.ColorRules.Rules, loaded.ColorRules.Rules);
        Assert.Equal(["two", "one"], loaded.RecentFilters);
    }

    [Fact]
    public void MissingColourSection_GivesTheDefaultRulesAndColouringOn()
    {
        using var dir = new TempDirectory();

        AppSettings loaded = AppSettings.Load(dir.File("absent.ini"));

        Assert.True(loaded.ColorFilterEnabled);
        Assert.Equal(ColorRuleSet.CreateDefault().Rules, loaded.ColorRules.Rules);
        Assert.Empty(loaded.RecentFilters);
    }

    [Fact]
    public void PaneFilter_RoundTripsThroughTheWorkspaceFile()
    {
        using var dir = new TempDirectory();
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!);
        var workspace = new Workspace
        {
            Panes =
            [
                basis.Panes[0] with { Filter = new FileFilter("*.bak;Thumbs.db", AutoWildcard: false, Negative: true, FilterFolders: true), FilterBarVisible = true },
                basis.Panes[1] with { FilterBarVisible = true },
                .. basis.Panes.Skip(2),
            ],
        };

        workspace.Save(dir.File("w.mdr"));
        Workspace loaded = Workspace.Load(dir.File("w.mdr"))!;

        Assert.Equal(new FileFilter("*.bak;Thumbs.db", false, true, true), loaded.Panes[0].Filter);
        Assert.True(loaded.Panes[0].FilterBarVisible);
        Assert.Null(loaded.Panes[1].Filter);
        Assert.True(loaded.Panes[1].FilterBarVisible);
        Assert.Null(loaded.Panes[2].Filter);
        Assert.False(loaded.Panes[2].FilterBarVisible);
    }
}

public class ColorFilterDialogTests(ITestOutputHelper output)
{
    [Fact]
    public void Dialog_ShowsTheRulesInTheirOwnColours()
    {
        Run(() => new ColorFilterDialog(ColorRuleSet.CreateDefault()), async dialog =>
        {
            await Task.Delay(400);
            dialog.SelectRule(8);
            await Task.Delay(300);
            output.WriteLine($"Screenshot: {SaveScreenshot(dialog, "color-dialog.png")}");

            // The .exe rule's text colour is red and its row is previewed that way.
            Assert.Equal(Color.FromArgb(255, 0, 0), dialog.RuleList.Items[8].ForeColor);
            Assert.Equal(8, dialog.SelectedIndex);
        });
    }

    [Fact]
    public void Operations_EditTheRuleListAndResultReflectsThem()
    {
        RunSta(() =>
        {
            using var dialog = new ColorFilterDialog(new ColorRuleSet([new ColorRule("*.a"), new ColorRule("*.b"), new ColorRule("*.c")]));
            _ = dialog.Handle;
            Assert.Equal(3, dialog.RuleCount);
            Assert.Equal(0, dialog.SelectedIndex);

            dialog.AddRule("*.new");
            Assert.Equal(["*.a", "*.new", "*.b", "*.c"], dialog.Result.Rules.Select(r => r.Pattern));
            Assert.Equal(1, dialog.SelectedIndex);

            dialog.MoveSelected(1);
            Assert.Equal(["*.a", "*.b", "*.new", "*.c"], dialog.Result.Rules.Select(r => r.Pattern));
            Assert.Equal(2, dialog.SelectedIndex);

            dialog.MoveSelected(-1);
            dialog.MoveSelected(-1);
            Assert.Equal(["*.new", "*.a", "*.b", "*.c"], dialog.Result.Rules.Select(r => r.Pattern));
            Assert.Equal(0, dialog.SelectedIndex);

            dialog.MoveSelected(-1);
            Assert.Equal(["*.new", "*.a", "*.b", "*.c"], dialog.Result.Rules.Select(r => r.Pattern));
            Assert.Equal(0, dialog.SelectedIndex);

            dialog.DeleteSelected();
            Assert.Equal(["*.a", "*.b", "*.c"], dialog.Result.Rules.Select(r => r.Pattern));
            Assert.Equal(0, dialog.SelectedIndex);
        });
    }

    [Fact]
    public void ResetAndImport_ReplaceTheRules()
    {
        RunSta(() =>
        {
            using var dialog = new ColorFilterDialog(new ColorRuleSet([new ColorRule("*.a")]));
            _ = dialog.Handle;

            dialog.ResetToDefaults();
            Assert.Equal(ColorRuleSet.CreateDefault().Rules, dialog.Result.Rules);

            Assert.False(dialog.Import(IniFile.Parse("[Other]\nx=1")));
            Assert.Equal(ColorRuleSet.CreateDefault().Rules.Count, dialog.RuleCount);

            Assert.True(dialog.Import(IniFile.Parse("[Filter2]\n0=*.q=1=112233=-1=1=-1=0\n1=#DIR=0=-1=-1=1=-1=0\n")));
            Assert.Equal(2, dialog.RuleCount);
            Assert.Equal(new ColorRule("*.q", true, Color.FromArgb(0x11, 0x22, 0x33)), dialog.Result.Rules[0]);
            Assert.False(dialog.Result.Rules[1].Enabled);
        });
    }

    [Fact]
    public void CheckBoxes_ToggleTheEnabledFlag()
    {
        RunSta(() =>
        {
            using var dialog = new ColorFilterDialog(new ColorRuleSet([new ColorRule("*.a"), new ColorRule("*.b", Enabled: false)]));
            _ = dialog.Handle;
            Assert.Equal([true, false], dialog.Result.Rules.Select(r => r.Enabled));

            ListView list = dialog.RuleList;
            list.Items[0].Checked = false;
            list.Items[1].Checked = true;

            Assert.Equal([false, true], dialog.Result.Rules.Select(r => r.Enabled));
        });
    }
}

/// <summary>The filter bar and colour menu on real panes and the main window. Shows a window briefly.</summary>
public class PaneFilterIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void TypingInTheFilterBar_FiltersTheListAndTheStatusSaysSo()
    {
        using var dir = CreateFolder();
        RunPane(dir, async pane =>
        {
            await WaitUntil(() => pane.Browser.ItemCount == 6, "all items listed");
            pane.FilterBarVisible = true;

            pane.FilterBar.NegativeBox.Checked = true;
            pane.FilterBar.PatternBox.Text = "*.bak;Thumbs.db";

            await WaitUntil(() => pane.Browser.ItemCount == 4, "the typed exclude filter was applied after the pause");
            Assert.True(pane.Filter.IsActive);
            Assert.True(pane.Filter.Negative);
            await Task.Delay(500);
            output.WriteLine($"Screenshot: {SaveScreenshot(pane.FindForm()!, "filter-bar.png")}");

            pane.FilterBar.PatternBox.Text = string.Empty;
            await WaitUntil(() => pane.Browser.ItemCount == 6, "clearing the box shows everything");
        });
    }

    [Fact]
    public void HidingTheFilterBar_ClearsTheFilter_AndCaptureStateKeepsItWhileShown()
    {
        using var dir = CreateFolder();
        RunPane(dir, async pane =>
        {
            await WaitUntil(() => pane.Browser.ItemCount == 6, "all items listed");

            pane.Filter = new FileFilter("*.txt");
            Assert.True(pane.FilterBarVisible, "setting a filter shows the bar");
            await WaitUntil(() => pane.Browser.ItemCount == 3, "filter applied");

            PaneState state = pane.CaptureState();
            Assert.Equal(new FileFilter("*.txt"), state.Filter);
            Assert.True(state.FilterBarVisible);

            pane.FilterBarVisible = false;
            Assert.False(pane.Filter.IsActive);
            await WaitUntil(() => pane.Browser.ItemCount == 6, "hiding the bar cleared the filter");
            Assert.Null(pane.CaptureState().Filter);

            pane.ApplyState(state);
            Assert.Equal(new FileFilter("*.txt"), pane.Filter);
            await WaitUntil(() => pane.Browser.ItemCount == 3, "the saved filter was applied again");
        });
    }

    [Fact]
    public void FilterIsRestoredWithTheWorkspace_AndRecentFiltersFillTheBox()
    {
        using var dir = CreateFolder();
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());
        profile.Settings.AddRecentFilter("*.txt");
        profile.Settings.AddRecentFilter("*.bak");
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(dir.Path)!);
        var workspace = new Workspace
        {
            Panes = [basis.Panes[0] with { Filter = new FileFilter("*.bak", Negative: true) }, .. basis.Panes.Skip(1)],
        };

        Run(() => new MainForm(null, workspace, null, profile), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 5, "the restored exclude filter hides old.bak");
            Assert.True(form.Panes[0].FilterBarVisible);
            Assert.Equal(["*.bak", "*.txt"], form.Panes[0].FilterBar.History);
        });
    }

    [Fact]
    public void FilterAndColourMenus_ActOnTheActivePaneAndSaveSettings()
    {
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());

        Run(() => new MainForm(null, null, null, profile), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount > 0, "pane 1 listed");
            Assert.All(form.Panes, pane => Assert.NotNull(pane.ColorRules));

            form.ToggleFilter();
            Assert.True(form.ActivePane.FilterBarVisible);
            Assert.False(form.Panes[1].FilterBarVisible);
            form.ToggleFilter();
            Assert.False(form.ActivePane.FilterBarVisible);

            form.SetColorFilterEnabled(false);
            Assert.All(form.Panes, pane => Assert.Null(pane.ColorRules));
            Assert.False(AppSettings.Load(profile.Paths.SettingsFile).ColorFilterEnabled);

            form.SetColorFilterEnabled(true);
            Assert.All(form.Panes, pane => Assert.NotNull(pane.ColorRules));
            Assert.True(AppSettings.Load(profile.Paths.SettingsFile).ColorFilterEnabled);
        });
    }

    [Fact]
    public void FocusingAPanesList_MakesThatPaneActive()
    {
        RunMainForm(async form =>
        {
            await WaitUntil(() => form.Panes.All(pane => pane.Browser.ItemCount > 0), "all panes listed");
            Assert.Equal(0, form.ActivePane.Index);

            form.Panes[2].Browser.FocusView();

            await WaitUntil(() => form.ActivePane.Index == 2, "the shell view's focus notification activated pane 3");
        });
    }

    private static TempDirectory CreateFolder()
    {
        var dir = new TempDirectory();
        foreach (string name in new[] { "report.txt", "notes.txt", "old.bak", "Thumbs.db", "setup.exe" })
            File.WriteAllText(dir.File(name), "x");
        Directory.CreateDirectory(dir.File("sub"));
        return dir;
    }

    private static void RunPane(TempDirectory dir, Func<PaneControl, Task> body)
    {
        PaneControl? pane = null;
        Run(() =>
        {
            var form = new Form { Width = 900, Height = 600, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir filter bar test" };
            pane = new PaneControl(0) { Dock = DockStyle.Fill };
            form.Controls.Add(pane);
            form.Shown += (_, _) => pane.Browser.Navigate(dir.Path);
            return form;
        }, _ => body(pane!));
    }

    private static void RunMainForm(Func<MainForm, Task> body) =>
        Run(() => new MainForm(ShellLocation.Parse(Environment.GetFolderPath(Environment.SpecialFolder.Windows))) { Size = new Size(1200, 800) }, body);
}
