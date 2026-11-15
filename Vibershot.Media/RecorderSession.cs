using System.Diagnostics;
using System.IO.Pipes;
using Vibershot.Media.Audio;

namespace Vibershot.Media;

public enum RecorderState { Idle, Starting, Recording, Paused, Finalizing, Stopped, Failed }

/// <summary>A snapshot of the recording for the status line.</summary>
public sealed record RecorderStatus(
    RecorderState State,
    TimeSpan Elapsed,
    long Frames,
    double Fps,
    long Bytes,
    string Encoder,
    string Grabber,
    string? CurrentFile,
    string? Message);

/// <summary>Settings for one recording session.</summary>
public sealed record RecorderSettings
{
    public required PixelRect Region { get; init; }
    public FrameRate FrameRate { get; init; } = FrameRate.Fps30;
    public int Quality { get; init; } = 75;
    public bool PreferHardwareEncoder { get; init; } = true;
    public bool ShowCursor { get; init; } = true;

    /// <summary>Where segments and the final file are written; defaults to %LOCALAPPDATA%\Vibershot\Recordings.</summary>
    public string RecordingsFolder { get; init; } = DefaultRecordingsFolder;

    /// <summary>How long to wait for the first frame before giving up on a grabber.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Time each stretch's first frame against <see cref="MediaClock"/> (see <see cref="RecorderSession.Stretches"/>),
    /// for recordings that have sound or a camera to line up with.
    /// </summary>
    public bool FrameClock { get; init; }

    /// <summary>Name of the finished file inside the recordings folder; null names it after the time.</summary>
    public string? FinalFileName { get; init; }

    public static string DefaultRecordingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Recordings");
}

/// <summary>
/// One unbroken run of recording (pauses split a recording into several).
/// </summary>
/// <param name="FileStart">Seconds into the finished file where this stretch begins.</param>
/// <param name="WallStart">The <see cref="MediaClock"/> time its first frame was taken.</param>
/// <param name="Duration">Its length in seconds.</param>
public sealed record ScreenStretch(double FileStart, double WallStart, double Duration);

/// <summary>Thrown when no grabber could start; the UI falls back to the GDI AVI recorder.</summary>
public sealed class RecorderStartException(string message) : Exception(message);

/// <summary>
/// Drives ffmpeg for one recording: Start / Pause / Resume / Stop. Each resume writes a new
/// fragmented MP4 segment so a crash leaves playable files; Stop concatenates the segments
/// (stream copy) and remuxes into a normal faststart MP4 in the recordings folder.
/// </summary>
public sealed class RecorderSession : IDisposable
{
    private readonly RecorderSettings _settings;
    private readonly FfmpegRunner _runner;
    private readonly List<string> _segments = new();
    private readonly string _stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
    private readonly Stopwatch _wall = new();
    private FfmpegProcess? _process;
    private RecordingOptions? _options;
    private Grabber _grabber;
    private EncoderInfo _encoder = new("none", "none", false);
    private TimeSpan _completedElapsed;
    private long _completedFrames;
    private bool _disposed;
    private readonly List<ScreenStretch> _stretches = new();
    private FrameClockReader? _clock;
    private bool _clockBroken;
    private double _fallbackWallStart;

    /// <summary>How long a frame takes from the screen to the frame-clock pipe at best; taken off the estimated start.</summary>
    public const double PipelineDelay = 0.03;

    /// <summary>The recorded stretches in order, complete once the session is paused or stopped.</summary>
    public IReadOnlyList<ScreenStretch> Stretches => _stretches;

    public RecorderSession(RecorderSettings settings, FfmpegRunner? runner = null)
    {
        _settings = settings;
        _runner = runner ?? new FfmpegRunner();
        Status = new RecorderStatus(RecorderState.Idle, TimeSpan.Zero, 0, 0, 0, "", "", null, null);
    }

    public event EventHandler<RecorderStatus>? StatusChanged;

    public RecorderStatus Status { get; private set; }
    public RecorderState State => Status.State;
    public IReadOnlyList<string> Segments => _segments;
    public EncoderInfo Encoder => _encoder;
    public Grabber Grabber => _grabber;
    public string RecordingsFolder => _settings.RecordingsFolder;

    /// <summary>The finished MP4 after <see cref="StopAsync"/>.</summary>
    public string? FinalPath { get; private set; }

    /// <summary>
    /// Probes the encoder, then tries ddagrab on the region's monitor and gdigrab after it.
    /// Throws <see cref="RecorderStartException"/> when both fail (stderr tail in the message).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State != RecorderState.Idle) throw new InvalidOperationException("Session already started.");
        Directory.CreateDirectory(_settings.RecordingsFolder);
        Publish(RecorderState.Starting, message: "Checking encoders…");
        var encoder = await EncoderProbe.ResolveAsync(_runner, _settings.PreferHardwareEncoder, cancellationToken).ConfigureAwait(false)
            ?? throw new RecorderStartException("No H.264 encoder works with this ffmpeg build.");
        _encoder = encoder;

        var displays = Displays.Enumerate();
        var display = Displays.Containing(_settings.Region, displays);
        _options = new RecordingOptions
        {
            Region = _settings.Region,
            Display = display,
            FrameRate = _settings.FrameRate,
            Quality = _settings.Quality,
            ShowCursor = _settings.ShowCursor,
            Encoder = encoder,
            OutputPath = "",
        };

        var errors = new List<string>();
        if (display is not null)
        {
            // ddagrab captures one DXGI output; keep the region inside it (gdigrab handles spans).
            var clipped = _settings.Region.Intersect(display.Bounds);
            if (clipped.Width >= 2 && clipped.Height >= 2)
            {
                _options = _options with { Region = clipped };
                Publish(RecorderState.Starting, message: "Starting Desktop Duplication capture…");
                string? error = await TryStartSegmentAsync(Grabber.Ddagrab, cancellationToken).ConfigureAwait(false);
                if (error is null) return;
                errors.Add("ddagrab: " + error);
            }
            _options = _options with { Region = _settings.Region };
        }
        Publish(RecorderState.Starting, message: "Starting GDI capture…");
        string? gdiError = await TryStartSegmentAsync(Grabber.Gdigrab, cancellationToken).ConfigureAwait(false);
        if (gdiError is null) return;
        errors.Add("gdigrab: " + gdiError);
        Publish(RecorderState.Failed, message: string.Join(" | ", errors));
        throw new RecorderStartException(string.Join(Environment.NewLine, errors));
    }

    public async Task PauseAsync()
    {
        if (State != RecorderState.Recording || _process is null) return;
        Publish(RecorderState.Paused, message: "Pausing…");
        await EndSegmentAsync().ConfigureAwait(false);
        Publish(RecorderState.Paused);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (State != RecorderState.Paused) return;
        Publish(RecorderState.Starting, message: "Resuming…");
        string? error = await TryStartSegmentAsync(_grabber, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            Publish(RecorderState.Paused, message: "Resume failed: " + error);
            throw new RecorderStartException(error);
        }
    }

    /// <summary>Ends the current segment and produces the final MP4. Returns its path (null when nothing was recorded).</summary>
    public async Task<string?> StopAsync(CancellationToken cancellationToken = default)
    {
        if (State is RecorderState.Stopped or RecorderState.Idle) return FinalPath;
        if (_process is not null) await EndSegmentAsync().ConfigureAwait(false);
        var segments = _segments.Where(s => { try { return new FileInfo(s).Length > 0; } catch { return false; } }).ToList();
        if (segments.Count == 0)
        {
            Publish(RecorderState.Stopped, message: "Nothing was recorded.");
            return null;
        }
        Publish(RecorderState.Finalizing, message: "Finalizing MP4…");
        string final = Path.Combine(_settings.RecordingsFolder, _settings.FinalFileName ?? $"recording_{_stamp}.mp4");
        FfmpegResult result;
        string? listPath = null;
        try
        {
            if (segments.Count == 1)
            {
                result = await _runner.RunAsync(RecordingArguments.Remux(segments[0], final), null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                listPath = Path.Combine(_settings.RecordingsFolder, $"recording_{_stamp}_segments.txt");
                await File.WriteAllTextAsync(listPath, RecordingArguments.ConcatList(segments), cancellationToken).ConfigureAwait(false);
                result = await _runner.RunAsync(RecordingArguments.Concat(listPath, final), null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (listPath is not null) { try { File.Delete(listPath); } catch { } }
        }
        if (!result.Succeeded || !File.Exists(final))
        {
            // Keep the fragmented segment(s): they are playable as they are.
            FinalPath = segments.Count == 1 ? segments[0] : null;
            Publish(RecorderState.Stopped, message: "Finalize failed; segments kept in " + _settings.RecordingsFolder + ". " + result.ErrorText(3));
            return FinalPath;
        }
        foreach (var segment in segments) { try { File.Delete(segment); } catch { } }
        FinalPath = final;
        Publish(RecorderState.Stopped, bytes: new FileInfo(final).Length, currentFile: final, message: "Saved recording.");
        return final;
    }

    private async Task<string?> TryStartSegmentAsync(Grabber grabber, CancellationToken cancellationToken)
    {
        bool withClock = _settings.FrameClock && !_clockBroken;
        string? error = await TryStartSegmentAsync(grabber, withClock, cancellationToken).ConfigureAwait(false);
        // ffmpeg could not write to the clock pipe: record without it rather than not at all.
        if (error != null && withClock && error.Contains(FrameClockReader.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            _clockBroken = true;
            error = await TryStartSegmentAsync(grabber, false, cancellationToken).ConfigureAwait(false);
        }
        return error;
    }

    private async Task<string?> TryStartSegmentAsync(Grabber grabber, bool withClock, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_settings.RecordingsFolder, $"recording_{_stamp}_part{_segments.Count + 1:D2}.mp4");
        var clock = withClock ? new FrameClockReader(_options!.FrameRate) : null;
        var options = _options! with { OutputPath = path, FrameClockPath = clock?.Path };
        FfmpegProcess process;
        try
        {
            process = _runner.Start(RecordingArguments.For(grabber, options));
        }
        catch (FfmpegNotFoundException ex)
        {
            clock?.Dispose();
            return ex.Message;
        }
        process.ProgressChanged += OnProgress;
        using var registration = cancellationToken.Register(process.Kill);
        var first = process.FirstFrame;
        var finished = await Task.WhenAny(first, process.Completion, Task.Delay(_settings.StartTimeout, CancellationToken.None)).ConfigureAwait(false);
        if (finished != first || first.IsFaulted || first.IsCanceled)
        {
            string reason;
            if (process.HasExited || finished == process.Completion)
            {
                var result = await process.Completion.ConfigureAwait(false);
                reason = $"exited with code {result.ExitCode}. {result.ErrorText(3)}";
            }
            else
            {
                reason = $"no frames within {_settings.StartTimeout.TotalSeconds:0} s. {string.Join(" ", process.StderrTail.TakeLast(2))}";
            }
            process.ProgressChanged -= OnProgress;
            process.Dispose();
            clock?.Dispose();
            try { File.Delete(path); } catch { }
            return reason.Trim();
        }
        // Without a frame clock the first progress report is the only witness; it trails the first frame by about this much.
        _fallbackWallStart = MediaClock.Now - first.Result.Frame / options.FrameRate.Value - 0.42;
        _process = process;
        _clock = clock;
        _grabber = grabber;
        _segments.Add(path);
        _wall.Restart();
        _ = WatchExitAsync(process);
        Publish(RecorderState.Recording, currentFile: path);
        return null;
    }

    /// <summary>Notes the stretch that just ended: where it sits in the file and when it was really taken.</summary>
    private void CloseStretch(FfmpegProcess process)
    {
        var clock = _clock;
        _clock = null;
        double rate = _options!.FrameRate.Value;
        long frames = clock is { Frames: > 0 } ? clock.Frames : process.LastProgress?.Frame ?? 0;
        double wallStart = clock is { Frames: > 0 } ? clock.FrameZero - PipelineDelay : _fallbackWallStart;
        clock?.Dispose();
        if (frames <= 0) return;
        double fileStart = _stretches.Count == 0 ? 0 : _stretches[^1].FileStart + _stretches[^1].Duration;
        _stretches.Add(new ScreenStretch(fileStart, wallStart, frames / rate));
    }

    private async Task WatchExitAsync(FfmpegProcess process)
    {
        var result = await process.Completion.ConfigureAwait(false);
        if (_process != process || State != RecorderState.Recording) return;
        // ffmpeg died mid-recording (device lost, disk full): keep what was written.
        _process = null;
        _completedElapsed += process.LastProgress?.OutTime ?? _wall.Elapsed;
        _completedFrames += process.LastProgress?.Frame ?? 0;
        CloseStretch(process);
        Publish(RecorderState.Paused, message: $"Recording stopped: ffmpeg exited with code {result.ExitCode}. {result.ErrorText(2)} Stop & save keeps what was captured.");
    }

    private async Task EndSegmentAsync()
    {
        var process = _process;
        if (process is null) return;
        _process = null;
        await process.StopAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        _completedElapsed += process.LastProgress?.OutTime ?? _wall.Elapsed;
        _completedFrames += process.LastProgress?.Frame ?? 0;
        CloseStretch(process);
        process.ProgressChanged -= OnProgress;
        process.Dispose();
    }

    private void OnProgress(FfmpegProgress progress)
    {
        if (State != RecorderState.Recording) return;
        Publish(RecorderState.Recording, _completedElapsed + progress.OutTime, _completedFrames + progress.Frame, progress.Fps, progress.TotalSize);
    }

    private void Publish(RecorderState state, TimeSpan? elapsed = null, long? frames = null, double? fps = null, long? bytes = null, string? currentFile = null, string? message = null)
    {
        var previous = Status;
        Status = new RecorderStatus(
            state,
            elapsed ?? (state == RecorderState.Recording ? previous.Elapsed : _completedElapsed),
            frames ?? (state == RecorderState.Recording ? previous.Frames : _completedFrames),
            fps ?? (state == RecorderState.Recording ? previous.Fps : 0),
            bytes ?? previous.Bytes,
            _encoder.DisplayName,
            _grabber == Grabber.Ddagrab ? "Desktop Duplication" : "GDI",
            currentFile ?? previous.CurrentFile,
            message);
        StatusChanged?.Invoke(this, Status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _process?.Dispose();
        _process = null;
        _clock?.Dispose();
        _clock = null;
    }
}

/// <summary>
/// The app's end of the frame clock: a named pipe ffmpeg writes one tiny frame to per video frame.
/// Frames can only arrive late, never early, so the earliest one (after taking off its place in
/// the sequence) says when frame 0 was.
/// </summary>
internal sealed class FrameClockReader : IDisposable
{
    public const string Prefix = "vibershot-clock-";

    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cancel = new();
    private readonly double _rate;
    private long _frames;
    private double _zero = double.MaxValue;

    public FrameClockReader(FrameRate rate)
    {
        _rate = rate.Value;
        string name = Prefix + Guid.NewGuid().ToString("N");
        Path = @"\\.\pipe\" + name;
        _pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 0);
        _ = Task.Run(ReadAsync);
    }

    public string Path { get; }
    public long Frames => Interlocked.Read(ref _frames);
    public double FrameZero => Volatile.Read(ref _zero);

    private async Task ReadAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_cancel.Token).ConfigureAwait(false);
            byte[] buffer = new byte[RecordingArguments.FrameClockBytes * 64];
            long bytes = 0;
            while (true)
            {
                int n = await _pipe.ReadAsync(buffer, _cancel.Token).ConfigureAwait(false);
                if (n == 0) break;
                double arrived = MediaClock.Now;
                long before = bytes / RecordingArguments.FrameClockBytes;
                bytes += n;
                long after = bytes / RecordingArguments.FrameClockBytes;
                if (after == before) continue;
                // Several frames in one read arrived together; the last of them is the least late.
                double zero = arrived - (after - 1) / _rate;
                if (zero < _zero) Volatile.Write(ref _zero, zero);
                Interlocked.Exchange(ref _frames, after);
            }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        try { _cancel.Cancel(); } catch (Exception) { }
        try { _pipe.Dispose(); } catch (Exception) { }
    }
}
