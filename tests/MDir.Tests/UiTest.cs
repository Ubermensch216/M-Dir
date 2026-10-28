using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MDir.Tests;

/// <summary>Helpers for tests that run real WinForms windows on an STA thread.</summary>
internal static partial class UiTest
{
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int MK_LBUTTON = 0x0001;
    private const uint GW_CHILD = 5;
    private const uint PW_RENDERFULLCONTENT = 2;

    /// <summary>Shows the form on a new STA thread, runs <paramref name="body"/> once it is shown, then closes it.</summary>
    public static void Run<TForm>(Func<TForm> createForm, Func<TForm, Task> body) where TForm : Form
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using TForm form = createForm();
            form.Shown += async (_, _) =>
            {
                try
                {
                    await body(form);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    form.Close();
                }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(90)))
            throw new TimeoutException("UI thread did not finish.");
        if (failure is not null)
            throw new Exception("UI test failed", failure);
    }

    /// <summary>Runs <paramref name="body"/> on an STA thread without a message loop (for controls that are never shown).</summary>
    public static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Exception("STA test failed", failure);
    }

    public static async Task WaitUntil(Func<bool> condition, string description)
    {
        DateTime deadline = DateTime.UtcNow + StepTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {description}");
            await Task.Delay(50);
        }
    }

    public static string SaveScreenshot(Form form, string fileName)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            PrintWindow(form.Handle, hdc, PW_RENDERFULLCONTENT);
            graphics.ReleaseHdc(hdc);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "screenshots", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    public static IntPtr MakePoint(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    public static IntPtr FirstChildWindow(IntPtr hwnd) => GetWindow(hwnd, GW_CHILD);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
