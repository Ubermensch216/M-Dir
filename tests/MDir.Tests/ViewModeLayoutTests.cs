using MDir.Panes;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Which view presets the classic (SysListView32) list layout really accepts.</summary>
public class ViewModeLayoutTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryPreset_IsReportedBackByTheShellView()
    {
        using var dir = new TempDirectory();
        for (int i = 0; i < 6; i++)
            File.WriteAllText(dir.File($"file{i}.txt"), "x");

        ExplorerBrowserHost? host = null;
        Run(() =>
        {
            var form = new Form { Width = 900, Height = 600, StartPosition = FormStartPosition.CenterScreen };
            host = new ExplorerBrowserHost { Dock = DockStyle.Fill };
            form.Controls.Add(host);
            form.Shown += (_, _) => host.Navigate(dir.Path);
            return form;
        }, async form =>
        {
            await WaitUntil(() => host!.ItemCount == 6, "items listed");
            var unsupported = new List<string>();
            foreach (ViewPreset preset in ViewPreset.All)
            {
                host!.SetViewMode(preset.Mode, preset.IconSize);
                await Task.Delay(500);
                bool ok = preset.Matches(host.ViewMode, host.IconSize);
                output.WriteLine($"{preset.Name}: asked {preset.Mode}/{preset.IconSize}, view reports {host.ViewMode}/{host.IconSize} -> {(ok ? "ok" : "NOT ACCEPTED")}");
                if (!ok)
                    unsupported.Add(preset.Name);
            }

            Assert.Empty(unsupported);
        });
    }
}
