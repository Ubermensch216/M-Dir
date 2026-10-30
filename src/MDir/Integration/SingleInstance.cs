using System.IO.Pipes;
using System.Text;

namespace MDir.Integration;

/// <summary>
/// Lets only one window of M-Dir run per Windows session: the first process owns a named mutex and listens on a
/// named pipe; a later process hands its command line to it through the pipe and exits. The pipe accepts only the
/// same Windows user.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    private Task? _listener;

    /// <param name="name">Identifies the application; tests pass their own so they never meet the real program.</param>
    public SingleInstance(string name = "M-Dir")
    {
        _pipeName = PipeNameFor(name);
        _mutex = new Mutex(initiallyOwned: true, MutexNameFor(name), out bool createdNew);
        IsFirst = createdNew;
    }

    /// <summary>True when no other instance was running when this one was created.</summary>
    public bool IsFirst { get; }

    /// <summary>Raised on a background thread with the arguments another process handed over.</summary>
    public event Action<string[]>? ArgumentsReceived;

    public static string MutexNameFor(string name) => @"Local\" + name + "-instance";

    public static string PipeNameFor(string name) => name + "-pipe";

    /// <summary>Starts accepting arguments from later processes (only meaningful for the first instance).</summary>
    public void StartListening()
    {
        if (IsFirst && _listener is null)
            _listener = Task.Run(() => ListenAsync(_stop.Token));
    }

    /// <summary>Hands <paramref name="args"/> to the running instance; false when none answered within the timeout.</summary>
    public static bool TrySend(string name, IEnumerable<string> args, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeNameFor(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (string arg in args)
                writer.Write(arg.Replace("\n", string.Empty).Replace("\r", string.Empty) + "\n");
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancel);
                using var reader = new StreamReader(server, new UTF8Encoding(false));
                string text = await reader.ReadToEndAsync(cancel);
                string[] args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // A client that vanished mid-message is ignored; wait for the next one.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from another thread than the creating one; the OS frees it when the process ends.
            }
        }

        _mutex.Dispose();
        _stop.Dispose();
    }
}
