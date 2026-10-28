using MDir.Panes;
using MDir.Shell;
using Xunit.Abstractions;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>
/// Integration tests that host the real Explorer view in a form on an STA thread.
/// They show a window briefly on the desktop.
/// </summary>
public class ExplorerBrowserHostTests(ITestOutputHelper output)
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

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
        ExplorerBrowserHost? host = null;
        Run(() =>
        {
            var form = new Form { Width = 900, Height = 600, StartPosition = FormStartPosition.CenterScreen, Text = "M-Dir test" };
            host = new ExplorerBrowserHost { Dock = DockStyle.Fill };
            form.Controls.Add(host);
            return form;
        }, form => body(form, host!));
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
}
