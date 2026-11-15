using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Vibershot.Media.Audio;

namespace Vibershot.Media.Camera;

/// <summary>A webcam as ffmpeg's DirectShow input names it.</summary>
/// <param name="Name">What the user sees.</param>
/// <param name="AlternativeName">The device path, which stays unique when two cameras share a name.</param>
public sealed record CameraDeviceInfo(string Name, string? AlternativeName)
{
    /// <summary>What goes after <c>video=</c> on the command line.</summary>
    public string InputName => AlternativeName ?? Name;
}

public static class CameraDevices
{
    private static readonly Regex DeviceLine = new("\"(?<name>.+)\"\\s+\\((?<kind>video|audio|none)\\)\\s*$", RegexOptions.Compiled);
    private static readonly Regex AlternativeLine = new("Alternative name\\s+\"(?<name>.+)\"\\s*$", RegexOptions.Compiled);

    public static string[] ListArguments => new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" };

    /// <summary>
    /// Reads the cameras out of ffmpeg's device listing. Camera drivers write their own log lines
    /// into the same stream, so only lines shaped like a device or its alternative name count.
    /// </summary>
    public static IReadOnlyList<CameraDeviceInfo> Parse(IEnumerable<string> lines)
    {
        var cameras = new List<CameraDeviceInfo>();
        bool lastWasCamera = false;
        foreach (string line in lines)
        {
            var device = DeviceLine.Match(line);
            if (device.Success)
            {
                lastWasCamera = device.Groups["kind"].Value == "video";
                if (lastWasCamera) cameras.Add(new CameraDeviceInfo(device.Groups["name"].Value, null));
                continue;
            }
            var alternative = AlternativeLine.Match(line);
            if (alternative.Success && lastWasCamera && cameras.Count > 0 && cameras[^1].AlternativeName == null)
                cameras[^1] = cameras[^1] with { AlternativeName = alternative.Groups["name"].Value };
            if (alternative.Success) lastWasCamera = false;
        }
        return cameras;
    }

    /// <summary>Asks ffmpeg which cameras exist. Empty when there are none or ffmpeg is missing.</summary>
    public static async Task<IReadOnlyList<CameraDeviceInfo>> ListAsync(FfmpegRunner runner)
    {
        try
        {
            var info = new ProcessStartInfo(runner.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8 };
            foreach (string argument in ListArguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process == null) return Array.Empty<CameraDeviceInfo>();
            _ = process.StandardOutput.ReadToEndAsync();
            string text = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return Parse(text.Split('\n').Select(line => line.TrimEnd('\r')));
        }
        catch (Exception) { return Array.Empty<CameraDeviceInfo>(); }
    }
}

/// <summary>What one finished camera file holds.</summary>
/// <param name="Start">The <see cref="MediaClock"/> time the first frame in the file was taken.</param>
public sealed record CameraResult(string Path, double Start, long Frames);

/// <summary>
/// One ffmpeg process that owns a webcam: it writes the picture to an MP4 and, from the same
/// frames, pipes a small preview back to the app (a camera can only be opened by one process, so
/// the preview and the recording have to share it). The file runs from the moment the camera
/// opens; the recording later uses the part it needs.
/// </summary>
public sealed class CameraSession : IDisposable
{
    public const int PreviewWidth = 480, PreviewHeight = 270, Rate = 30;

    /// <summary>How long a frame takes from the lens to our pipe at best; taken off the estimated start.</summary>
    public const double PipelineDelay = 0.06;

    private readonly FfmpegRunner _runner;
    private readonly EncoderInfo _encoder;
    private readonly object _gate = new();
    private readonly byte[] _latest = new byte[PreviewWidth * PreviewHeight * 4];
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RawFfmpegProcess? _process;
    private long _frames;
    private double _zero = double.MaxValue;
    private volatile bool _stopping;

    public CameraSession(FfmpegRunner runner, CameraDeviceInfo device, string outputPath, EncoderInfo encoder)
    {
        _runner = runner; _encoder = encoder;
        Device = device; OutputPath = outputPath;
        _ = Task.Run(RunAsync);
    }

    public CameraDeviceInfo Device { get; }
    public string OutputPath { get; }

    /// <summary>Raised on a background thread after each preview frame; read it with <see cref="CopyLatest"/>.</summary>
    public event Action? FrameArrived;

    /// <summary>Why the camera could not be opened or stopped; null while frames are coming.</summary>
    public string? Error { get; private set; }

    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>True once the first frame arrives; false when the camera could not be opened.</summary>
    public Task<bool> Started => _started.Task;

    /// <summary>Copies the newest preview frame (BGRA, <see cref="PreviewWidth"/> × <see cref="PreviewHeight"/>).</summary>
    public void CopyLatest(byte[] target)
    {
        lock (_gate) Buffer.BlockCopy(_latest, 0, target, 0, Math.Min(target.Length, _latest.Length));
    }

    /// <summary>
    /// Pure argument builder, pinned by tests. With <paramref name="askForMode"/> the camera is
    /// asked for 720p30 MJPEG, which nearly every webcam offers; without it the camera's default
    /// mode is taken, whatever that is.
    /// </summary>
    public static string[] Arguments(CameraDeviceInfo device, string outputPath, EncoderInfo encoder, bool askForMode)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "dshow" };
        if (askForMode) args.AddRange(new[] { "-video_size", "1280x720", "-framerate", "30", "-vcodec", "mjpeg" });
        args.AddRange(new[] { "-rtbufsize", "256M", "-i", "video=" + device.InputName });
        // One constant-rate stream feeds both outputs, so frame n is the same picture in the file and in the preview.
        args.AddRange(new[]
        {
            "-filter_complex",
            $"[0:v]fps={Rate},split[rec][pre];[pre]scale={PreviewWidth}:{PreviewHeight}:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad={PreviewWidth}:{PreviewHeight}:(ow-iw)/2:(oh-ih)/2[preview]",
            "-map", "[rec]",
        });
        args.AddRange(RecordingArguments.EncoderArguments(new RecordingOptions
        {
            Region = new PixelRect(0, 0, 1280, 720), FrameRate = FrameRate.Fps30, Quality = 75, Encoder = encoder, OutputPath = outputPath,
        }));
        args.AddRange(new[]
        {
            "-pix_fmt", "yuv420p", "-movflags", "frag_keyframe+empty_moov", "-f", "mp4", "-y", outputPath,
            "-map", "[preview]", "-pix_fmt", "bgra", "-f", "rawvideo", "-flush_packets", "1", "pipe:1",
        });
        return args.ToArray();
    }

    private async Task RunAsync()
    {
        string lastError = "";
        foreach (bool askForMode in new[] { true, false })
        {
            if (_stopping) break;
            RawFfmpegProcess process;
            try { process = _runner.StartRaw(Arguments(Device, OutputPath, _encoder, askForMode)); }
            catch (Exception ex) { lastError = ex.Message; break; }
            _process = process;
            Interlocked.Exchange(ref _frames, 0); _zero = double.MaxValue;
            byte[] frame = new byte[PreviewWidth * PreviewHeight * 4];
            try
            {
                while (true)
                {
                    int read = 0;
                    while (read < frame.Length)
                    {
                        int n = await process.Output.ReadAsync(frame.AsMemory(read, frame.Length - read)).ConfigureAwait(false);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read < frame.Length) break;
                    double arrived = MediaClock.Now;
                    long index = Interlocked.Increment(ref _frames) - 1;
                    // Delivery can only be late, never early, so the earliest frame is the truest witness of when frame 0 was.
                    lock (_gate) { _zero = Math.Min(_zero, arrived - index / (double)Rate); Buffer.BlockCopy(frame, 0, _latest, 0, frame.Length); }
                    _started.TrySetResult(true);
                    FrameArrived?.Invoke();
                }
            }
            catch (Exception) { }
            if (Frames > 0 || _stopping) { if (!_stopping) Error = "The camera stopped. " + LastLine(process.ErrorText); return; }
            lastError = LastLine(process.ErrorText);
            process.Dispose();
        }
        Error = string.IsNullOrWhiteSpace(lastError) ? "The camera could not be opened." : "The camera could not be opened: " + lastError;
        _started.TrySetResult(false);
    }

    private static string LastLine(string text) => text.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) ?? "";

    /// <summary>Ends the file cleanly. Null when no frame was ever recorded.</summary>
    public async Task<CameraResult?> StopAsync()
    {
        _stopping = true;
        var process = _process;
        if (process != null) await process.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        long frames = Frames;
        if (frames == 0 || !File.Exists(OutputPath)) return null;
        double zero; lock (_gate) zero = _zero;
        return new CameraResult(OutputPath, zero - PipelineDelay, frames);
    }

    public void Dispose()
    {
        _stopping = true;
        _process?.Dispose();
    }
}
