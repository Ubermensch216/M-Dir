using System.Drawing.Imaging;
using MDir.Features;
using MDir.Layout;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>Text viewer wrap and search, media files, image stepping and the slideshow.</summary>
public class PreviewExtrasTests
{
    private static ShellLocation Loc(string path) => ShellLocation.Parse(path)!;

    private static void RunPane(Func<Form, PreviewPane, Task> body)
    {
        PreviewPane? pane = null;
        Run(() =>
        {
            var form = new Form { Width = 700, Height = 500, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir preview extras" };
            pane = new PreviewPane { Dock = DockStyle.Fill };
            form.Controls.Add(pane);
            return form;
        }, form => body(form, pane!));
    }

    /// <summary>A tiny 8-bit mono PCM WAV file with a short beep-less silence: enough for a player to open.</summary>
    private static void WriteWav(string path)
    {
        const int rate = 8000, samples = 800;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + samples);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate);
        writer.Write((short)1);
        writer.Write((short)8);
        writer.Write("data"u8.ToArray());
        writer.Write(samples);
        writer.Write(Enumerable.Repeat((byte)128, samples).ToArray());
    }

    [Fact]
    public void TextViewer_FindsText_BothWays_AndWrapsAround()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("notes.txt"), "alpha\r\nbeta Needle here\r\ngamma\r\ndelta needle again\r\n");
        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("notes.txt")), 1);
            var box = (TextBox)pane.View!;
            Assert.True(pane.TextBarVisible);

            Assert.True(pane.FindNext("needle", forward: true));
            Assert.Equal("Needle", box.SelectedText);
            int first = box.SelectionStart;

            Assert.True(pane.FindNext("needle", forward: true));
            Assert.Equal("needle", box.SelectedText);
            Assert.True(box.SelectionStart > first);

            Assert.True(pane.FindNext("needle", forward: true), "wraps to the first one");
            Assert.Equal(first, box.SelectionStart);

            Assert.True(pane.FindNext("needle", forward: false), "backwards from the first wraps to the last");
            Assert.True(box.SelectionStart > first);
            Assert.True(pane.FindNext("needle", forward: false));
            Assert.Equal(first, box.SelectionStart);

            Assert.False(pane.FindNext("absent", forward: true));
            Assert.False(pane.FindNext(string.Empty, forward: true));
        });
    }

    [Fact]
    public void TextViewer_WordWrapCanBeSwitched_AndStaysForTheNextFile()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("a.txt"), new string('x', 500));
        File.WriteAllText(dir.File("b.txt"), "second");
        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("a.txt")), 1);
            Assert.False(pane.WordWrapOn);

            pane.SetWordWrap(true);
            Assert.True(pane.WordWrapOn);
            Assert.Equal(ScrollBars.Vertical, ((TextBox)pane.View!).ScrollBars);

            await pane.ShowAsync(Loc(dir.File("b.txt")), 1);
            Assert.True(pane.WordWrapOn, "the choice is kept");

            pane.SetWordWrap(false);
            Assert.False(pane.WordWrapOn);
            Assert.Equal(ScrollBars.Both, ((TextBox)pane.View!).ScrollBars);
        });
    }

    [Fact]
    public void TextBar_IsOnlyShownForText()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("a.txt"), "hello");
        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("a.txt")), 1);
            Assert.True(pane.TextBarVisible);

            await pane.ShowAsync(null, 0);
            Assert.False(pane.TextBarVisible);
            Assert.False(pane.FindNext("hello", forward: true));
        });
    }

    [Fact]
    public void MediaFile_OpensInThePlayer_AndLeavingItReleasesTheFile()
    {
        using var dir = new TempDirectory();
        WriteWav(dir.File("tone.wav"));
        File.WriteAllText(dir.File("next.txt"), "next");
        RunPane(async (_, pane) =>
        {
            await pane.ShowAsync(Loc(dir.File("tone.wav")), 1);

            if (pane.Kind != PreviewKind.Media)
            {
                // No Windows Media Player control on this PC: the pane must fall back to the facts page.
                Assert.Equal(PreviewKind.Info, pane.Kind);
                return;
            }

            var player = (MediaView)pane.View!;
            Assert.True(player.IsOpen);
            Assert.Contains("미디어", pane.Detail);
            Assert.Equal("tone.wav", pane.Title);

            await pane.ShowAsync(Loc(dir.File("next.txt")), 1);
            Assert.Equal(PreviewKind.Text, pane.Kind);

            // The player is gone, so the file is free again.
            await WaitUntil(() =>
            {
                try
                {
                    File.Delete(dir.File("tone.wav"));
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            }, "the media file released");
        });
    }

    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("clip.MP4", true)]
    [InlineData("movie.mkv", true)]
    [InlineData("notes.txt", false)]
    [InlineData("pic.png", false)]
    public void MediaExtensions_AreRecognised(string name, bool expected) => Assert.Equal(expected, MediaView.IsMedia(name));

    [Fact]
    public void NavigationButtons_AskTheWindowToMoveTheSelection()
    {
        using var dir = new TempDirectory();
        using (var bitmap = new Bitmap(10, 10))
            bitmap.Save(dir.File("p.png"), ImageFormat.Png);
        RunPane(async (_, pane) =>
        {
            var steps = new List<int>();
            pane.NavigateRequested += (_, step) => steps.Add(step);
            await pane.ShowAsync(Loc(dir.File("p.png")), 1);

            var bar = pane.Controls.OfType<ToolStrip>().First(strip => strip.Items.OfType<ToolStripButton>().Any(button => button.Text == "▶"));
            Assert.True(bar.Visible);
            bar.Items.OfType<ToolStripButton>().First(button => button.Text == "▶").PerformClick();
            bar.Items.OfType<ToolStripButton>().First(button => button.Text == "◀").PerformClick();

            Assert.Equal([1, -1], steps);
        });
    }

    private static TempDirectory CreateGallery()
    {
        var root = new TempDirectory();
        Directory.CreateDirectory(root.File("g"));
        File.WriteAllText(root.File(@"g\a.txt"), "a");
        foreach (string name in new[] { "b.png", "d.png" })
        {
            using var bitmap = new Bitmap(20, 20);
            bitmap.Save(root.File(@"g\" + name), ImageFormat.Png);
        }

        File.WriteAllText(root.File(@"g\c.txt"), "c");
        return root;
    }

    private static MainForm CreateForm(TempDirectory root)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(root.File("g"))!);
        PaneLayout layout = PaneLayout.FromId(1)!;
        return new MainForm(null, new Workspace { Layout = layout, Ratios = layout.DefaultRatios, Panes = basis.Panes }, null, null)
        {
            Size = new Size(1300, 800),
            SilentFileOperations = true,
        };
    }

    private static string Selected(MainForm form) =>
        form.ActivePane.Browser.GetSelectedPaths().Select(Path.GetFileName).SingleOrDefault() ?? string.Empty;

    [Fact]
    public void StepPreviewItem_MovesThroughTheList_SkippingNonImagesOnRequest()
    {
        using TempDirectory root = CreateGallery();
        Run(() => CreateForm(root), async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount == 4, "gallery listed");
            await WaitUntil(() => browser.SelectItem(0) && Selected(form) == "a.txt", "a selected");

            Assert.True(form.StepPreviewItem(1, imagesOnly: true));
            await WaitUntil(() => Selected(form) == "b.png", "first image");
            Assert.True(form.StepPreviewItem(1, imagesOnly: true));
            await WaitUntil(() => Selected(form) == "d.png", "second image");
            Assert.False(form.StepPreviewItem(1, imagesOnly: true), "no image after the last one");

            Assert.True(form.StepPreviewItem(1, imagesOnly: true, wrap: true));
            await WaitUntil(() => Selected(form) == "b.png", "wrapped to the first image");

            Assert.True(form.StepPreviewItem(-1, imagesOnly: false));
            await WaitUntil(() => Selected(form) == "a.txt", "previous item of any kind");
            Assert.False(form.StepPreviewItem(-1, imagesOnly: false), "nothing before the first item");
        });
    }

    [Fact]
    public void Slideshow_ShowsTheImagesOneAfterAnother_AndStopsWhenSwitchedOff()
    {
        using TempDirectory root = CreateGallery();
        Run(() => CreateForm(root), async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount == 4, "gallery listed");
            form.SlideshowIntervalMs = 250;
            form.TogglePreview();
            await WaitUntil(() => browser.SelectItem(1) && Selected(form) == "b.png", "b selected");
            await WaitUntil(() => form.PreviewPane?.Kind == PreviewKind.Image, "image previewed");

            form.PreviewPane!.SlideshowActive = true;

            await WaitUntil(() => Selected(form) == "d.png", "slideshow moved to d");
            await WaitUntil(() => Selected(form) == "b.png", "slideshow wrapped to b");

            form.PreviewPane.SlideshowActive = false;
            string stopped = Selected(form);
            await Task.Delay(800);
            Assert.Equal(stopped, Selected(form));
        });
    }

    [Fact]
    public void HidingThePreview_SwitchesTheSlideshowOff()
    {
        using TempDirectory root = CreateGallery();
        Run(() => CreateForm(root), async form =>
        {
            await WaitUntil(() => form.ActivePane.Browser.ItemCount == 4, "gallery listed");
            form.TogglePreview();
            form.PreviewPane!.SlideshowActive = true;

            form.TogglePreview();

            Assert.False(form.PreviewPane.SlideshowActive);
        });
    }
}
