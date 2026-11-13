using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vibershot.Media;

/// <summary>
/// Low-resolution stand-ins for source videos: at most 720 lines with a keyframe every 15 frames,
/// so the editor can seek in tens of milliseconds whatever the original's size or GOP. One proxy
/// per source file, keyed by path, size and modified time, built once in the background.
/// </summary>
public sealed class ProxyCache
{
    public const int MaxHeight = 720;
    public const int KeyframeInterval = 15;

    private readonly FfmpegRunner _runner;
    private readonly ConcurrentDictionary<string, Task<string?>> _building = new(StringComparer.OrdinalIgnoreCase);

    public ProxyCache(FfmpegRunner runner, string? folder = null)
    {
        _runner = runner;
        Folder = folder ?? DefaultFolder;
    }

    public string Folder { get; }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Proxies");

    /// <summary>The cache file for a source, whether or not it exists yet.</summary>
    public string PathFor(string sourcePath) => Path.Combine(Folder, KeyFor(sourcePath) + ".mp4");

    /// <summary>A name for cached work on a file that changes whenever the file's path, size or modified time does.</summary>
    public static string KeyFor(string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        string identity = string.Create(CultureInfo.InvariantCulture,
            $"{file.FullName.ToLowerInvariant()}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}");
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)))[..20].ToLowerInvariant();
    }

    /// <summary>The finished proxy for a source, or null when it has not been built.</summary>
    public string? TryGet(string sourcePath)
    {
        string path = PathFor(sourcePath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Builds the proxy if needed. Concurrent calls for one source share a single encode. Null when the encode fails.</summary>
    public Task<string?> GetOrCreateAsync(MediaInfo source, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string target = PathFor(source.Path);
        if (File.Exists(target)) return Task.FromResult<string?>(target);
        return _building.GetOrAdd(target, _ => BuildAsync(source, target, progress, cancellationToken));
    }

    private async Task<string?> BuildAsync(MediaInfo source, string target, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string partial = target + ".part";
        try
        {
            Directory.CreateDirectory(Folder);
            var encoder = await EncoderProbe.ResolveAsync(_runner, preferHardware: true, cancellationToken).ConfigureAwait(false);
            if (encoder is null) return null;
            var report = progress is null ? null : new Progress<FfmpegProgress>(p =>
            {
                if (source.Duration > TimeSpan.Zero) progress.Report(Math.Clamp(p.OutTime / source.Duration, 0, 1));
            });
            var result = await _runner.RunAsync(Arguments(source, encoder, partial), report, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded) { TryDelete(partial); return null; }
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception)
        {
            TryDelete(partial);
            return null;
        }
        finally
        {
            _building.TryRemove(target, out _);
        }
    }

    /// <summary>Pure argument builder, pinned by tests.</summary>
    public static string[] Arguments(MediaInfo source, EncoderInfo encoder, string outputPath)
    {
        int height = FrameReader.Even(Math.Min(MaxHeight, source.Height > 0 ? source.Height : MaxHeight));
        var rate = source.FrameRate ?? FrameRate.Fps30;
        var options = new RecordingOptions
        {
            Region = new PixelRect(0, 0, Math.Max(2, height * 16 / 9), height),
            FrameRate = rate,
            Quality = 45,
            Encoder = encoder,
            OutputPath = outputPath,
        };
        var args = new List<string>(FfmpegRunner.StandardPrefix) { "-i", source.Path, "-vf", $"scale=-2:{height}:flags=bilinear" };
        var encode = RecordingArguments.EncoderArguments(options).ToList();
        // The recording arguments end in a one-second GOP; a proxy wants a short one for fast seeks.
        int g = encode.IndexOf("-g");
        if (g >= 0 && g + 1 < encode.Count) encode[g + 1] = KeyframeInterval.ToString(CultureInfo.InvariantCulture);
        args.AddRange(encode);
        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart", "-f", "mp4", "-y", outputPath });
        return args.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }
}
