using System.IO.Pipes;
using System.Text.Json;
using System.Threading;

namespace Vibershot.UI;

/// <summary>
/// Keeps one Vibershot per user session. The first process owns a named mutex and listens on a
/// named pipe; a second launch (Start Menu, the Run key, a double-clicked .vibeshot file) sends its
/// arguments down the pipe and exits, so the running copy opens the editor or shows the launcher
/// instead of registering its hotkeys twice.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Vibershot.SingleInstance";
    private const string PipeName = "Vibershot.Commands";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancel = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Returns the owner when this is the first instance; null when another copy runs (and received <paramref name="args"/>).</summary>
    public static SingleInstance? TryAcquire(string[] args)
    {
        Mutex mutex;
        bool first;
        try { mutex = new Mutex(true, MutexName, out first); }
        catch (Exception) { return new SingleInstance(new Mutex()); }
        if (first) return new SingleInstance(mutex);
        mutex.Dispose();
        Forward(args);
        return null;
    }

    private static void Forward(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(args);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
        }
        catch (Exception)
        {
            // The owner is starting up or wedged; nothing more to do from a second copy.
        }
    }

    /// <summary>Starts accepting forwarded argument lists; <paramref name="onArgs"/> runs on the caller's dispatcher.</summary>
    public void Listen(Action<string[]> onArgs)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var token = _cancel.Token;
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    using var memory = new MemoryStream();
                    server.CopyTo(memory);
                    var args = JsonSerializer.Deserialize<string[]>(memory.ToArray()) ?? Array.Empty<string>();
                    dispatcher.BeginInvoke(() => onArgs(args));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) return;
                    Thread.Sleep(250);
                }
            }
        }) { IsBackground = true, Name = "Vibershot single-instance pipe" };
        thread.Start();
    }

    public void Dispose()
    {
        _cancel.Cancel();
        try { _mutex.ReleaseMutex(); } catch (Exception) { }
        _mutex.Dispose();
    }
}
