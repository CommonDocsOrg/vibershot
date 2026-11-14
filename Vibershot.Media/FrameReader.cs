using System.Globalization;

namespace Vibershot.Media;

/// <summary>What one preview decode reads: a file from a start time, scaled, at a fixed rate.</summary>
public sealed record FrameReaderOptions
{
    public required string Path { get; init; }

    /// <summary>Seconds into the file where decoding starts.</summary>
    public double Start { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required FrameRate FrameRate { get; init; }

    /// <summary>Decode on the GPU (d3d11va). Off by default: the download back to system memory usually costs more than it saves.</summary>
    public bool HardwareDecode { get; init; }

    /// <summary>Stop after this many frames; 0 reads to the end of the file.</summary>
    public int MaxFrames { get; init; }
}

/// <summary>
/// Reads decoded BGRA frames from an ffmpeg child process: the editor preview's frame source.
/// Frames arrive at <see cref="FrameReaderOptions.FrameRate"/>, so the n-th frame read is exactly
/// n frames after the start time.
/// </summary>
public sealed class FrameReader : IDisposable
{
    private readonly RawFfmpegProcess _process;

    public FrameReader(FfmpegRunner runner, FrameReaderOptions options)
    {
        Options = options;
        FrameBytes = options.Width * options.Height * 4;
        _process = runner.StartRaw(Arguments(options));
    }

    public FrameReaderOptions Options { get; }
    public int FrameBytes { get; }
    public string ErrorText => _process.ErrorText;

    /// <summary>Pure argument builder, pinned by tests.</summary>
    public static string[] Arguments(FrameReaderOptions options)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (options.HardwareDecode) args.AddRange(new[] { "-hwaccel", "d3d11va" });
        args.AddRange(new[]
        {
            "-ss", Seconds(options.Start),
            "-i", options.Path,
            "-an", "-sn",
            "-vf", $"fps={options.FrameRate.ToFfmpegString()},scale={options.Width}:{options.Height}:flags=fast_bilinear",
        });
        if (options.MaxFrames > 0) args.AddRange(new[] { "-frames:v", options.MaxFrames.ToString(CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-pix_fmt", "bgra", "-f", "rawvideo", "-" });
        return args.ToArray();
    }

    /// <summary>Fills <paramref name="buffer"/> with the next frame. False at end of stream.</summary>
    public async ValueTask<bool> ReadFrameAsync(byte[] buffer, CancellationToken cancellationToken = default)
    {
        int read = 0;
        while (read < FrameBytes)
        {
            int n = await _process.Output.ReadAsync(buffer.AsMemory(read, FrameBytes - read), cancellationToken).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public static string Seconds(double value) => Math.Max(0, value).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The largest even size with the source's aspect that fits inside the box.</summary>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (Even(boxWidth), Even(boxHeight));
        double scale = Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
        return (Even((int)Math.Round(sourceWidth * scale)), Even((int)Math.Round(sourceHeight * scale)));
    }

    public static int Even(int value) => Math.Max(2, value & ~1);

    public void Dispose() => _process.Dispose();
}
