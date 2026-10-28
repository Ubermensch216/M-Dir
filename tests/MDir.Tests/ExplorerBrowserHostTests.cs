using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MDir.Panes;
using MDir.Shell;
using Xunit.Abstractions;

namespace MDir.Tests;

/// <summary>
/// Integration tests that host the real Explorer view in a form on an STA thread.
/// They show a window briefly on the desktop.
/// </summary>
public partial class ExplorerBrowserHostTests(ITestOutputHelper output)
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void Navigation_EnumeratesItemsAndTracksHistory()
    {
        RunInForm(async (form, host) =>
        {
            await NavigateAndWait(host, () => host.Navigate(WindowsDir));
            Assert.Equal(WindowsDir, host.CurrentLocation!.FileSystemPath, ignoreCase: true);
            await WaitUntil(() => host.ItemCount > 0, "items enumerated");

            string screenshot = SaveScreenshot(form, "explorer-host.png");
            output.WriteLine($"Screenshot: {screenshot}");

            await NavigateAndWait(host, host.GoUp);
            Assert.Equal(Path.GetPathRoot(WindowsDir), host.CurrentLocation!.FileSystemPath, ignoreCase: true);
            Assert.True(host.CanGoBack);

            await NavigateAndWait(host, host.GoBack);
            Assert.Equal(WindowsDir, host.CurrentLocation!.FileSystemPath, ignoreCase: true);
            Assert.True(host.CanGoForward);

            await NavigateAndWait(host, host.GoForward);
            Assert.Equal(Path.GetPathRoot(WindowsDir), host.CurrentLocation!.FileSystemPath, ignoreCase: true);
        });
    }

    [Fact]
    public void Keyboard_ArrowsReachShellViewAndBackspaceGoesBack()
    {
        RunInForm(async (form, host) =>
        {
            await NavigateAndWait(host, () => host.Navigate(WindowsDir));
            await NavigateAndWait(host, () => host.Navigate(Path.Combine(WindowsDir, "System32")));
            await WaitUntil(() => host.ItemCount > 2, "items enumerated");

            Assert.True(host.SelectItem(0));
            host.FocusView();
            IntPtr focus = GetFocus();
            Assert.True(host.ContainsFocus, $"focus is outside the host ({focus})");

            // A Down arrow must not be swallowed by WinForms dialog-key handling.
            PostMessage(focus, NativeMethods.WM_KEYDOWN, (IntPtr)Keys.Down, IntPtr.Zero);
            await WaitUntil(() => host.FocusedItemIndex == 1, "Down arrow moved the focused item");

            PostMessage(focus, NativeMethods.WM_KEYDOWN, (IntPtr)Keys.Back, IntPtr.Zero);
            await WaitUntil(() => string.Equals(host.CurrentLocation!.FileSystemPath, WindowsDir, StringComparison.OrdinalIgnoreCase),
                "Backspace navigated back");
        });
    }

    [Fact]
    public void SetViewMode_AppliesToViewAndPersistsAcrossNavigation()
    {
        RunInForm(async (_, host) =>
        {
            await NavigateAndWait(host, () => host.Navigate(WindowsDir));
            host.SetViewMode(FolderViewMode.Icon, 96);
            await NavigateAndWait(host, host.GoUp);

            await WaitUntil(() => host.ViewMode == FolderViewMode.Icon && host.IconSize == 96, "view mode kept after navigation");
        });
    }

    private static void RunInForm(Func<Form, ExplorerBrowserHost, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var form = new Form { Width = 900, Height = 600, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir test" };
            var host = new ExplorerBrowserHost { Dock = DockStyle.Fill };
            form.Controls.Add(host);
            form.Shown += async (_, _) =>
            {
                try
                {
                    await body(form, host);
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

    private static async Task NavigateAndWait(ExplorerBrowserHost host, Action navigate)
    {
        var completed = new TaskCompletionSource();
        EventHandler onCompleted = (_, _) => completed.TrySetResult();
        EventHandler<NavigationFailedEventArgs> onFailed = (_, e) => completed.TrySetException(new Exception($"Navigation failed: {e.Location}"));
        host.NavigationCompleted += onCompleted;
        host.NavigationFailed += onFailed;
        try
        {
            navigate();
            await completed.Task.WaitAsync(StepTimeout);
        }
        finally
        {
            host.NavigationCompleted -= onCompleted;
            host.NavigationFailed -= onFailed;
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string description)
    {
        DateTime deadline = DateTime.UtcNow + StepTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {description}");
            await Task.Delay(50);
        }
    }

    private static string SaveScreenshot(Form form, string fileName)
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

    private const uint PW_RENDERFULLCONTENT = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetFocus();
}
