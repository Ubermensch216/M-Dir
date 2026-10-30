using System.Runtime.InteropServices;
using MDir.Layout;
using MDir.Panes;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

public partial class ListTreeExtrasTests
{
    [LibraryImport("user32.dll")]
    private static partial int GetScrollPos(IntPtr window, int bar);

    private const int SbHorz = 0;

    private static MainForm CreateForm(string folder, int width = 1300, Action<MainForm>? configure = null)
    {
        Workspace basis = Workspace.CreateDefault(ShellLocation.Parse(folder)!);
        PaneLayout layout = PaneLayout.FromId(1)!;
        var form = new MainForm(null, new Workspace { Layout = layout, Ratios = layout.DefaultRatios, Panes = basis.Panes }, null, null)
        {
            Size = new Size(width, 800),
            SilentFileOperations = true,
        };
        configure?.Invoke(form);
        return form;
    }

    /// <summary>The wParam of a WM_MOUSEWHEEL: the wheel delta in the high word, the Shift key flag in the low word.</summary>
    private static IntPtr Wheel(int delta, bool shift) => (IntPtr)(((long)(ushort)(short)delta << 16) | (shift ? 4L : 0L));

    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void TreeIndent_CanBeChosenAndPutBack()
    {
        Run(() => CreateForm(WindowsDir, configure: form => form.ActivePane.TreeEnabled = true), async form =>
        {
            FolderTreeControl tree = await WaitForTree(form);
            int normal = tree.CurrentIndent;
            Assert.True(normal > 0);

            Assert.True(form.RunCommand("view.treeIndent.32"));
            await WaitUntil(() => tree.CurrentIndent == 32, "wide indent");

            Assert.True(form.RunCommand("view.treeIndent.12"));
            await WaitUntil(() => tree.CurrentIndent == 12, "narrow indent");

            Assert.True(form.RunCommand("view.treeIndent.0"));
            await WaitUntil(() => tree.CurrentIndent == normal, "default indent again");
        });
    }

    private static async Task<FolderTreeControl> WaitForTree(MainForm form)
    {
        await WaitUntil(() => form.ActivePane.Tree is { IsHandleCreated: true } tree && tree.CurrentIndent > 0, "tree created");
        return form.ActivePane.Tree!;
    }

    [Fact]
    public void TreeFolderNames_AreColouredByTheColourRules()
    {
        Run(() => CreateForm(WindowsDir, configure: form => form.ActivePane.TreeEnabled = true), async form =>
        {
            FolderTreeControl tree = await WaitForTree(form);
            await WaitUntil(() => form.ActivePane.Browser.ItemCount > 0, "list filled");
            await Task.Delay(800);
            var green = Color.FromArgb(0x00, 0x88, 0x00);

            int on = CountIn(form, tree, green);
            form.SetColorFilterEnabled(false);
            await Task.Delay(500);
            int off = CountIn(form, tree, green);

            Assert.True(on > off + 30, $"folder names are green with colouring on ({on} pixels) and not with it off ({off})");
        });
    }

    private static int CountIn(MainForm form, FolderTreeControl tree, Color color)
    {
        using Bitmap shot = Capture(form);
        Rectangle area = form.RectangleToScreen(Rectangle.Empty);
        Rectangle treeBounds = tree.RectangleToScreen(tree.ClientRectangle);
        treeBounds.Offset(-form.Left, -form.Top);
        treeBounds.Intersect(new Rectangle(Point.Empty, shot.Size));
        using Bitmap crop = shot.Clone(treeBounds, shot.PixelFormat);
        GC.KeepAlive(area);
        return CountPixels(crop, color, 40);
    }

    [Fact]
    public void ShiftWheel_ScrollsTheListSideways_PlainWheelIsLeftAlone()
    {
        Run(() => CreateForm(WindowsDir, width: 420), async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount > 20, "Windows folder listed");
            await Task.Delay(500);
            IntPtr list = NativeMethods.FindDescendant(browser.Handle, "SysListView32");
            Assert.NotEqual(IntPtr.Zero, list);
            Assert.Equal(0, GetScrollPos(list, SbHorz));

            IntPtr wheelDown = Wheel(-120, shift: true);
            IntPtr wheelDownPlain = Wheel(-120, shift: false);

            Assert.False(ListScroll.TryScrollSideways(list, wheelDownPlain), "no Shift: the shell scrolls vertically itself");
            Assert.Equal(0, GetScrollPos(list, SbHorz));

            Assert.True(ListScroll.TryScrollSideways(list, wheelDown));
            Assert.True(GetScrollPos(list, SbHorz) > 0, "scrolled to the right");
            int right = GetScrollPos(list, SbHorz);

            IntPtr wheelUp = Wheel(120, shift: true);
            Assert.True(ListScroll.TryScrollSideways(list, wheelUp));
            Assert.True(GetScrollPos(list, SbHorz) < right, "and back to the left");

            Assert.False(ListScroll.TryScrollSideways(form.Handle, wheelDown), "only lists are scrolled");
        });
    }

    [Fact]
    public void PreFilter_HandlesTheShiftWheelOnAList()
    {
        Run(() => CreateForm(WindowsDir, width: 420), async form =>
        {
            var browser = form.ActivePane.Browser;
            await WaitUntil(() => browser.ItemCount > 20, "Windows folder listed");
            await Task.Delay(500);
            IntPtr list = NativeMethods.FindDescendant(browser.Handle, "SysListView32");
            var message = Message.Create(list, ListScroll.WM_MOUSEWHEEL, Wheel(-120, shift: true), IntPtr.Zero);

            bool handled = ((IMessageFilter)form).PreFilterMessage(ref message);

            Assert.True(handled);
            Assert.True(GetScrollPos(list, SbHorz) > 0);
        });
    }
}
