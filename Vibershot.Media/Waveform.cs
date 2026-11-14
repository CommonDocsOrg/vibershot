namespace Vibershot.Media;

/// <summary>
/// The loudness outline of a file's sound: one peak (0 to 255) for every
/// <see cref="BucketSeconds"/> of audio, drawn inside audio clips on the timeline.
/// </summary>
public sealed record Waveform(double BucketSeconds, byte[] Peaks)
{
    /// <summary>The loudest peak between two times in the file, as 0 to 1.</summary>
    public double PeakBetween(double fromSeconds, double toSeconds)
    {
        if (Peaks.Length == 0) return 0;
        int from = Math.Clamp((int)(fromSeconds / BucketSeconds), 0, Peaks.Length - 1);
        int to = Math.Clamp((int)(toSeconds / BucketSeconds), from, Peaks.Length - 1);
        byte peak = 0;
        for (int i = from; i <= to; i++) if (Peaks[i] > peak) peak = Peaks[i];
        return peak / 255.0;
    }
}

/// <summary>
/// Builds waveforms with ffmpeg (mono, 8 kHz, raw samples on a pipe) and keeps them on disk,
/// keyed like proxies by path, size and modified time, so a file is only read once.
/// </summary>
public sealed class WaveformCache
{
    public const int SampleRate = 8000, SamplesPerBucket = 80;

    private readonly FfmpegRunner _runner;

    public WaveformCache(FfmpegRunner runner, string? folder = null)
    {
        _runner = runner;
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Waveforms");
    }

    public string Folder { get; }

    /// <summary>Pure argument builder, pinned by tests.</summary>
    public static string[] Arguments(string path) => new[]
    {
        "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-vn", "-sn", "-ac", "1", "-ar", "8000", "-f", "s16le", "-",
    };

    /// <summary>The waveform of a file's first audio stream, from the cache or freshly read. Null when it has no readable sound.</summary>
    public async Task<Waveform?> GetOrCreateAsync(string path, CancellationToken cancellationToken = default)
    {
        string cached = Path.Combine(Folder, ProxyCache.KeyFor(path) + ".peaks");
        try
        {
            if (File.Exists(cached)) return new Waveform((double)SamplesPerBucket / SampleRate, await File.ReadAllBytesAsync(cached, cancellationToken).ConfigureAwait(false));
            var peaks = new List<byte>();
            using (var process = _runner.StartRaw(Arguments(path)))
            {
                var buffer = new byte[SamplesPerBucket * 2 * 64];
                int filled = 0, peak = 0, inBucket = 0;
                while (true)
                {
                    int n = await process.Output.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken).ConfigureAwait(false);
                    if (n == 0) break;
                    filled += n;
                    int whole = filled & ~1;
                    for (int i = 0; i < whole; i += 2)
                    {
                        int sample = Math.Abs((int)(short)(buffer[i] | buffer[i + 1] << 8));
                        if (sample > peak) peak = sample;
                        if (++inBucket == SamplesPerBucket) { peaks.Add((byte)Math.Min(255, peak * 255 / 32767)); peak = 0; inBucket = 0; }
                    }
                    // An odd trailing byte is the first half of the next sample.
                    if (whole < filled) buffer[0] = buffer[whole];
                    filled -= whole;
                }
                if (inBucket > 0) peaks.Add((byte)Math.Min(255, peak * 255 / 32767));
            }
            if (peaks.Count == 0) return null;
            var bytes = peaks.ToArray();
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(cached + ".part", bytes, cancellationToken).ConfigureAwait(false);
            File.Move(cached + ".part", cached, overwrite: true);
            return new Waveform((double)SamplesPerBucket / SampleRate, bytes);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
