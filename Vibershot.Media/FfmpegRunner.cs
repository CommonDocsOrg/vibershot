using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vibershot.Media;

/// <summary>One block from ffmpeg's <c>-progress pipe:1</c> output.</summary>
public sealed record FfmpegProgress(long Frame, double Fps, TimeSpan OutTime, long TotalSize, double Speed, bool IsEnd);

/// <summary>How an ffmpeg process ended.</summary>
public sealed record FfmpegResult(int ExitCode, bool Cancelled, IReadOnlyList<string> StderrTail, FfmpegProgress? LastProgress)
{
    public bool Succeeded => ExitCode == 0 && !Cancelled;

    /// <summary>The last few stderr lines joined, for error messages.</summary>
    public string ErrorText(int lines = 6)
    {
        var tail = StderrTail.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(lines);
        return string.Join(Environment.NewLine, tail);
    }
}

/// <summary>Thrown when ffmpeg is needed but cannot be found or started.</summary>
public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>
/// The only place that spawns ffmpeg. Arguments are a typed list (no shell quoting), progress
/// comes from <c>-progress pipe:1</c> on stdout, the last ~40 stderr lines are kept for error
/// messages, and cancellation kills the process.
/// </summary>
public sealed class FfmpegRunner
{
    public const int StderrTailLines = 40;

    private readonly string _ffmpegPath;

    public FfmpegRunner(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? FfmpegLocator.FfmpegPath
            ?? throw new FfmpegNotFoundException("ffmpeg.exe was not found. Run tools\\get-ffmpeg.ps1 to download it.");
    }

    public string FfmpegPath => _ffmpegPath;

    /// <summary>Starts ffmpeg and returns a handle to watch, stop or kill it.</summary>
    public FfmpegProcess Start(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new FfmpegNotFoundException($"Could not start ffmpeg at {_ffmpegPath}: {ex.Message}");
        }
        return new FfmpegProcess(process);
    }

    /// <summary>
    /// Starts ffmpeg with stdout left as a binary stream (raw frames or samples) instead of
    /// progress text. The caller reads <see cref="RawFfmpegProcess.Output"/> and disposes the handle.
    /// </summary>
    public RawFfmpegProcess StartRaw(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        try
        {
            var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
            return new RawFfmpegProcess(process);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new FfmpegNotFoundException($"Could not start ffmpeg at {_ffmpegPath}: {ex.Message}");
        }
    }

    /// <summary>Runs ffmpeg to completion. Cancelling the token kills the process.</summary>
    public async Task<FfmpegResult> RunAsync(IReadOnlyList<string> arguments, IProgress<FfmpegProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var process = Start(arguments);
        if (progress is not null) process.ProgressChanged += progress.Report;
        using var registration = cancellationToken.Register(() => process.Kill());
        var result = await process.Completion.ConfigureAwait(false);
        return cancellationToken.IsCancellationRequested ? result with { Cancelled = true } : result;
    }

    /// <summary>Shared leading arguments: quiet banner, no stats spam, progress on stdout.</summary>
    public static string[] StandardPrefix => new[] { "-hide_banner", "-nostats", "-progress", "pipe:1" };
}

/// <summary>An ffmpeg process whose stdout carries binary data. Disposing kills it.</summary>
public sealed class RawFfmpegProcess : IDisposable
{
    private readonly Process _process;
    private readonly Queue<string> _stderr = new();

    internal RawFfmpegProcess(Process process)
    {
        _process = process;
        Output = process.StandardOutput.BaseStream;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    lock (_stderr)
                    {
                        _stderr.Enqueue(line);
                        while (_stderr.Count > FfmpegRunner.StderrTailLines) _stderr.Dequeue();
                    }
                }
            }
            catch (Exception) { }
        });
    }

    public Stream Output { get; }

    public string ErrorText { get { lock (_stderr) return string.Join(Environment.NewLine, _stderr); } }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception) { }
        _process.Dispose();
    }
}

/// <summary>A running ffmpeg process started by <see cref="FfmpegRunner"/>.</summary>
public sealed class FfmpegProcess : IDisposable
{
    private readonly Process _process;
    private readonly Queue<string> _stderr = new();
    private readonly object _gate = new();
    private readonly Task<FfmpegResult> _completion;
    private readonly TaskCompletionSource<FfmpegProgress> _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _cancelled;

    public event Action<FfmpegProgress>? ProgressChanged;
    public event Action<string>? StderrLine;

    public FfmpegProgress? LastProgress { get; private set; }
    public bool HasExited => _process.HasExited;
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>Completes when ffmpeg exits and both pipes are drained.</summary>
    public Task<FfmpegResult> Completion => _completion;

    /// <summary>Completes with the first progress block that has at least one frame.</summary>
    public Task<FfmpegProgress> FirstFrame => _firstFrame.Task;

    public IReadOnlyList<string> StderrTail { get { lock (_gate) return _stderr.ToArray(); } }

    internal FfmpegProcess(Process process)
    {
        _process = process;
        var stdout = Task.Run(ReadProgressAsync);
        var stderr = Task.Run(ReadStderrAsync);
        _completion = WaitAsync(stdout, stderr);
    }

    private async Task<FfmpegResult> WaitAsync(Task stdout, Task stderr)
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        _firstFrame.TrySetCanceled();
        return new FfmpegResult(_process.ExitCode, _cancelled, StderrTail, LastProgress);
    }

    /// <summary>Asks ffmpeg to finish cleanly ('q' on stdin), then kills it after the grace period.</summary>
    public async Task<FfmpegResult> StopAsync(TimeSpan grace)
    {
        if (!_process.HasExited)
        {
            try
            {
                await _process.StandardInput.WriteAsync("q\n").ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // stdin already closed: fall through to the wait/kill below.
            }
            var finished = await Task.WhenAny(_completion, Task.Delay(grace)).ConfigureAwait(false);
            if (finished != _completion) Kill();
        }
        return await _completion.ConfigureAwait(false);
    }

    public void Kill()
    {
        _cancelled = true;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception) { }
    }

    private async Task ReadProgressAsync()
    {
        long frame = 0, size = 0; double fps = 0, speed = 0; TimeSpan outTime = TimeSpan.Zero;
        try
        {
            var reader = _process.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "frame": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frame); break;
                    case "fps": double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps); break;
                    case "total_size": if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) size = 0; break;
                    case "out_time_us":
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)) outTime = TimeSpan.FromTicks(us * 10);
                        break;
                    case "speed":
                        double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                        break;
                    case "progress":
                        var block = new FfmpegProgress(frame, fps, outTime, size, speed, value == "end");
                        LastProgress = block;
                        if (frame > 0) _firstFrame.TrySetResult(block);
                        ProgressChanged?.Invoke(block);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // Pipe closed while the process died; the exit code tells the story.
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            var reader = _process.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (_gate)
                {
                    _stderr.Enqueue(line);
                    while (_stderr.Count > FfmpegRunner.StderrTailLines) _stderr.Dequeue();
                }
                StderrLine?.Invoke(line);
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
