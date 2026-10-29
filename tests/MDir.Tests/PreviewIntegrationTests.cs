using System.Drawing.Imaging;
using MDir.Features;
using MDir.Settings;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>The preview panel in the real main window. Shows a window briefly on the desktop.</summary>
public class PreviewIntegrationTests(ITestOutputHelper output)
{
    private static TempDirectory CreateFolder()
    {
        var dir = new TempDirectory();
        File.WriteAllText(dir.File("a.txt"), "alpha text");
        using (var bitmap = new Bitmap(80, 40))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
                g.Clear(Color.FromArgb(250, 0, 250));
            bitmap.Save(dir.File("b.png"), ImageFormat.Png);
        }

        File.WriteAllBytes(dir.File("c.pdf"), PreviewHandlerHostTests.MinimalPdf());
        return dir;
    }

    private static MainForm CreateForm(TempDirectory dir, Workspace? workspace = null, AppProfile? profile = null)
    {
        Workspace basis = workspace ?? Workspace.CreateDefault(ShellLocation.Parse(dir.Path)!);
        return new MainForm(null, basis, null, profile) { Size = new Size(1300, 800) };
    }

    [Fact]
    public void F12_PreviewsTheSelectedFile_AndFollowsSelectionAndTheActivePane()
    {
        using TempDirectory dir = CreateFolder();
        Run(() => CreateForm(dir), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "the folder is listed");
            Assert.False(form.PreviewVisible);
            Assert.Null(form.PreviewPane);

            form.TogglePreview();
            Assert.True(form.PreviewVisible);
            PreviewPane preview = form.PreviewPane!;
            await WaitUntil(() => preview.Kind == PreviewKind.Message, "nothing is selected yet");

            // Items sort by name: a.txt, b.png, c.pdf.
            form.Panes[0].Browser.SelectItem(0);
            await WaitUntil(() => preview.Kind == PreviewKind.Text && ((TextBox)preview.View!).Text == "alpha text", "the text file is previewed");

            form.Panes[0].Browser.SelectItem(1);
            await WaitUntil(() => preview.Kind == PreviewKind.Image, "the image is previewed");
            await Task.Delay(400);
            output.WriteLine($"Screenshot: {SaveScreenshot(form, "preview-main-window.png")}");

            form.Panes[0].Browser.SelectItem(2);
            await WaitUntil(() => preview.Kind == PreviewKind.Handler, "the PDF is shown by its shell handler");

            // Another pane has nothing selected: the preview follows the active pane.
            form.ActivatePane(1);
            await WaitUntil(() => preview.Kind == PreviewKind.Message, "the preview switched to the new active pane");

            form.ActivatePane(0);
            await WaitUntil(() => preview.Kind == PreviewKind.Handler, "and back again");

            form.TogglePreview();
            Assert.False(form.PreviewVisible);
            Assert.Equal(PreviewKind.None, preview.Kind);
            Assert.False(preview.Visible);
        });
    }

    [Fact]
    public void HiddenPreview_DoesNotReactToSelection_AndFileIsReleased()
    {
        using TempDirectory dir = CreateFolder();
        Run(() => CreateForm(dir), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "the folder is listed");
            form.TogglePreview();
            form.Panes[0].Browser.SelectItem(2);
            await WaitUntil(() => form.PreviewPane!.Kind == PreviewKind.Handler, "the PDF is shown");

            form.TogglePreview();
            form.Panes[0].Browser.SelectItem(0);
            await Task.Delay(500);
            Assert.Equal(PreviewKind.None, form.PreviewPane!.Kind);

            // Hiding unloaded the handler, so the file can be deleted.
            await WaitUntil(() =>
            {
                try
                {
                    File.Delete(dir.File("c.pdf"));
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            }, "the PDF handler released the file");
        });
    }

    [Fact]
    public void PreviewState_IsSavedAndRestored()
    {
        using TempDirectory dir = CreateFolder();
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(temp.Path, temp.Path), new AppSettings());

        Run(() => CreateForm(dir, profile: profile), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "the folder is listed");
            form.TogglePreview();
        });

        Workspace saved = Workspace.Load(profile.Paths.StartWorkspaceFile)!;
        Assert.True(saved.PreviewVisible);
        Assert.Equal(Workspace.DefaultPreviewSplit, saved.PreviewSplit);

        var wider = new Workspace { Panes = saved.Panes, PreviewVisible = true, PreviewSplit = 5000 };
        Run(() => CreateForm(dir, wider), async form =>
        {
            await WaitUntil(() => form.Panes[0].Browser.ItemCount == 3, "the folder is listed");
            Assert.True(form.PreviewVisible);
            Assert.NotNull(form.PreviewPane);

            // 50% of the work area, give or take the splitter.
            await WaitUntil(() => form.PreviewPane!.Width > form.ClientSize.Width / 3, "the preview got its saved width");
            double share = form.PreviewPane!.Width / (double)form.ClientSize.Width;
            Assert.InRange(share, 0.44, 0.52);
        });
    }

    [Fact]
    public void PreviewPanel_KeepsItsWidthShareWhenTheWindowIsResized()
    {
        using TempDirectory dir = CreateFolder();
        var workspace = new Workspace { Panes = Workspace.CreateDefault(ShellLocation.Parse(dir.Path)!).Panes, PreviewVisible = true, PreviewSplit = 4000 };
        Run(() => CreateForm(dir, workspace), async form =>
        {
            await WaitUntil(() => form.PreviewPane is not null && form.PreviewPane.Width > 100, "preview sized");
            double before = form.PreviewPane!.Width / (double)form.ClientSize.Width;

            form.Width += 300;
            await WaitUntil(() => Math.Abs(form.PreviewPane.Width / (double)form.ClientSize.Width - before) < 0.02, "the preview kept its share");
        });
    }

    [Fact]
    public void WorkspaceFile_RoundTripsThePreviewKeysAndClampsTheSplit()
    {
        var workspace = new Workspace { PreviewVisible = true, PreviewSplit = 4200 };
        string text = workspace.ToIni().ToText();

        Assert.Contains("Preview=1\r\nPreviewSplit=4200\r\n", text);
        Workspace loaded = Workspace.FromIni(IniFile.Parse(text))!;
        Assert.True(loaded.PreviewVisible);
        Assert.Equal(4200, loaded.PreviewSplit);

        Assert.Equal(9500, Workspace.FromIni(IniFile.Parse("[Workspace]\nPreviewSplit=99999\n"))!.PreviewSplit);
        Assert.False(Workspace.FromIni(IniFile.Parse("[Workspace]\n"))!.PreviewVisible);
    }
}
