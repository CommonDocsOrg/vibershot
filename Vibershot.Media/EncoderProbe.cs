namespace Vibershot.Media;

/// <summary>An H.264 encoder ffmpeg can use on this machine.</summary>
public sealed record EncoderInfo(string Name, string DisplayName, bool IsHardware);

/// <summary>
/// Finds a working H.264 encoder once per session by running a 1-second null encode of
/// <c>nullsrc</c> through each candidate. A hardware encoder that is compiled in but fails at
/// runtime (no GPU, driver mismatch) simply drops to the next one.
/// </summary>
public static class EncoderProbe
{
    public static readonly EncoderInfo[] Hardware =
    {
        new("h264_nvenc", "NVIDIA NVENC", true),
        new("h264_qsv", "Intel Quick Sync", true),
        new("h264_amf", "AMD AMF", true),
    };

    /// <summary>libx264 exists only in GPL builds; h264_mf and libopenh264 cover LGPL builds.</summary>
    public static readonly EncoderInfo[] Software =
    {
        new("libx264", "x264", false),
        new("h264_mf", "Media Foundation H.264", false),
        new("libopenh264", "OpenH264", false),
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<EncoderInfo?>> Cache = new();

    /// <summary>Candidates in the order they are tried.</summary>
    public static IEnumerable<EncoderInfo> Candidates(bool preferHardware) =>
        preferHardware ? Hardware.Concat(Software) : Software.Concat(Hardware);

    /// <summary>Arguments for the trial encode, exposed so tests can pin them.</summary>
    public static string[] ProbeArguments(string encoder) => new[]
    {
        "-hide_banner", "-nostdin", "-loglevel", "error",
        "-f", "lavfi", "-i", "nullsrc=s=256x256:r=30",
        "-t", "1", "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-",
    };

    /// <summary>Returns the first working encoder (cached per ffmpeg path and preference), or null when none encode.</summary>
    public static Task<EncoderInfo?> ResolveAsync(FfmpegRunner runner, bool preferHardware, CancellationToken cancellationToken = default)
    {
        string key = runner.FfmpegPath + "|" + preferHardware;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached) && !(cached.IsCompleted && cached.IsFaulted)) return cached;
            var task = ProbeAllAsync(runner, preferHardware, cancellationToken);
            Cache[key] = task;
            return task;
        }
    }

    public static void ResetCache()
    {
        lock (Gate) Cache.Clear();
    }

    private static async Task<EncoderInfo?> ProbeAllAsync(FfmpegRunner runner, bool preferHardware, CancellationToken cancellationToken)
    {
        foreach (var candidate in Candidates(preferHardware))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var result = await runner.RunAsync(ProbeArguments(candidate.Name), null, timeout.Token).ConfigureAwait(false);
                if (result.Succeeded) return candidate;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Encoder hung during init; try the next one.
            }
        }
        return null;
    }
}
