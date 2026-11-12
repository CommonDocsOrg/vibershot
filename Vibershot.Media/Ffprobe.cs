using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Vibershot.Media;

/// <summary>What ffprobe reports about a media file.</summary>
public sealed record MediaInfo(
    string Path,
    TimeSpan Duration,
    int Width,
    int Height,
    FrameRate? FrameRate,
    string? VideoCodec,
    string? AudioCodec,
    bool HasAudio,
    int Rotation,
    long FileSize,
    string? FormatName)
{
    public bool HasVideo => VideoCodec is not null;
}

/// <summary>Runs <c>ffprobe -print_format json -show_streams -show_format</c> and reads the result.</summary>
public static class Ffprobe
{
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        string ffprobe = FfmpegLocator.FfprobePath
            ?? throw new FfmpegNotFoundException("ffprobe.exe was not found next to ffmpeg.exe.");
        var info = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in Arguments(path)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffprobe.");
        using var registration = cancellationToken.Register(() => { try { process.Kill(); } catch { } });
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string json = await stdout.ConfigureAwait(false);
        string errors = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe failed for {path}: {errors.Trim()}");
        }
        return Parse(path, json);
    }

    /// <summary>The ffprobe argument list, exposed so tests can pin it.</summary>
    public static string[] Arguments(string path) =>
        new[] { "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path };

    /// <summary>Parses ffprobe's JSON output. Public so it can be tested without a binary.</summary>
    public static MediaInfo Parse(string path, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        TimeSpan duration = TimeSpan.Zero;
        long size = 0;
        string? formatName = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (TryDouble(format, "duration", out double seconds)) duration = TimeSpan.FromSeconds(seconds);
            if (TryLong(format, "size", out long bytes)) size = bytes;
            if (format.TryGetProperty("format_name", out var fn) && fn.ValueKind == JsonValueKind.String) formatName = fn.GetString();
        }
        int width = 0, height = 0, rotation = 0;
        FrameRate? rate = null;
        string? videoCodec = null, audioCodec = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                string type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                string? codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                if (type == "video" && videoCodec is null)
                {
                    videoCodec = codec ?? "unknown";
                    if (stream.TryGetProperty("width", out var w) && w.TryGetInt32(out int wi)) width = wi;
                    if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out int hi)) height = hi;
                    foreach (var key in new[] { "avg_frame_rate", "r_frame_rate" })
                    {
                        if (stream.TryGetProperty(key, out var fr) && fr.ValueKind == JsonValueKind.String &&
                            FrameRate.TryParse(fr.GetString(), out var parsed))
                        {
                            rate = parsed;
                            break;
                        }
                    }
                    rotation = ReadRotation(stream);
                    if (duration == TimeSpan.Zero && TryDouble(stream, "duration", out double streamSeconds)) duration = TimeSpan.FromSeconds(streamSeconds);
                }
                else if (type == "audio" && audioCodec is null)
                {
                    audioCodec = codec ?? "unknown";
                }
            }
        }
        if (size == 0)
        {
            try { if (File.Exists(path)) size = new FileInfo(path).Length; } catch { }
        }
        return new MediaInfo(path, duration, width, height, rate, videoCodec, audioCodec, audioCodec is not null, rotation, size, formatName);
    }

    private static int ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (TryDouble(item, "rotation", out double r)) return Normalize((int)Math.Round(r));
            }
        }
        if (stream.TryGetProperty("tags", out var tags) && TryDouble(tags, "rotate", out double tagged)) return Normalize((int)Math.Round(tagged));
        return 0;

        static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;
    }

    private static bool TryDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var prop)) return false;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var prop)) return false;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }
}
