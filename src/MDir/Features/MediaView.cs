using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace MDir.Features;

/// <summary>
/// Plays a video or audio file in the preview pane with the Windows Media Player control (the same ActiveX control
/// Q-Dir embeds). It does not start by itself; the player's own buttons play, pause and seek. The control is driven
/// through late binding, so no interop assembly is needed; where Windows Media Player is not installed
/// <see cref="TryOpen"/> returns false and the pane shows the file's facts instead.
/// </summary>
internal sealed class MediaView : AxHost
{
    private const string WindowsMediaPlayerClassId = "6BF52A52-394A-11D3-B153-00C04F79FAA6";

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".mid", ".midi",
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".mpg", ".mpeg", ".asf", ".webm", ".3gp",
    };

    public MediaView() : base(WindowsMediaPlayerClassId)
    {
    }

    public static bool IsMedia(string path) => MediaExtensions.Contains(Path.GetExtension(path));

    /// <summary>The player has a file loaded.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// Loads <paramref name="path"/> without playing it. False when the player control is missing or refuses the file.
    /// The control must already be inside a window (it is created on demand).
    /// </summary>
    public bool TryOpen(string path)
    {
        try
        {
            CreateControl();
            dynamic player = GetOcx()!;
            player.settings.autoStart = false;
            player.uiMode = "full";
            player.URL = path;
            IsOpen = true;
            return true;
        }
        catch (Exception ex) when (ex is InvalidActiveXStateException or COMException or RuntimeBinderException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Stops playing (used when another file is selected or the pane is hidden).</summary>
    public void Stop()
    {
        if (!IsOpen)
            return;

        try
        {
            dynamic player = GetOcx()!;
            player.controls.stop();
        }
        catch (Exception ex) when (ex is InvalidActiveXStateException or COMException or RuntimeBinderException or InvalidOperationException)
        {
        }
    }

    /// <summary>The player's state as text ("wmppsPlaying", "wmppsStopped"...), or null when it cannot be read; for tests and diagnostics.</summary>
    internal int? PlayState
    {
        get
        {
            try
            {
                dynamic player = GetOcx()!;
                return (int)player.playState;
            }
            catch (Exception ex) when (ex is InvalidActiveXStateException or COMException or RuntimeBinderException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && IsOpen)
        {
            // Closing the player releases the file and the audio device immediately.
            try
            {
                dynamic player = GetOcx()!;
                player.close();
            }
            catch (Exception ex) when (ex is InvalidActiveXStateException or COMException or RuntimeBinderException or InvalidOperationException)
            {
            }
        }

        base.Dispose(disposing);
    }
}
