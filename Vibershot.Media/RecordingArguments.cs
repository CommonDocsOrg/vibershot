using System.Globalization;
using System.Text;

namespace Vibershot.Media;

/// <summary>Which screen grabber feeds ffmpeg.</summary>
public enum Grabber
{
    /// <summary>Desktop Duplication through the lavfi <c>ddagrab</c> source (hardware, 60 fps capable).</summary>
    Ddagrab,

    /// <summary>GDI <c>gdigrab</c> input device; slower but works everywhere (RDP, old drivers).</summary>
    Gdigrab,
}

/// <summary>Everything needed to build one recording command line.</summary>
public sealed record RecordingOptions
{
    /// <summary>Region in virtual-desktop pixels. Rounded to even dimensions by the builders.</summary>
    public required PixelRect Region { get; init; }

    /// <summary>The monitor the region sits on; ddagrab coordinates are relative to it.</summary>
    public DisplayInfo? Display { get; init; }

    public FrameRate FrameRate { get; init; } = FrameRate.Fps30;

    /// <summary>1-100, mapped per encoder to CRF / CQ / QP.</summary>
    public int Quality { get; init; } = 75;

    public bool ShowCursor { get; init; } = true;

    public required EncoderInfo Encoder { get; init; }

    public required string OutputPath { get; init; }
}

/// <summary>
/// Pure builders for ffmpeg argument lists. Each returns a fresh <c>string[]</c> so tests can
/// assert exact arguments; no shell quoting is involved because <see cref="FfmpegRunner"/>
/// passes them through <c>ProcessStartInfo.ArgumentList</c>.
/// </summary>
public static class RecordingArguments
{
    /// <summary>The ddagrab lavfi graph: region as offset/size on the chosen output, then download to BGRA for the encoder.</summary>
    public static string DdagrabSource(RecordingOptions options)
    {
        var region = options.Region.ToEven();
        var display = options.Display?.Bounds ?? new PixelRect(0, 0, int.MaxValue / 2, int.MaxValue / 2);
        int index = options.Display?.Index ?? 0;
        int x = Math.Max(0, region.X - display.X), y = Math.Max(0, region.Y - display.Y);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"ddagrab=output_idx={index}:framerate={options.FrameRate.ToFfmpegString()}:draw_mouse={(options.ShowCursor ? 1 : 0)}");
        sb.Append(CultureInfo.InvariantCulture, $":offset_x={x}:offset_y={y}:video_size={region.Width}x{region.Height}");
        sb.Append(",hwdownload,format=bgra");
        return sb.ToString();
    }

    public static string[] Ddagrab(RecordingOptions options)
    {
        var args = new List<string>(FfmpegRunner.StandardPrefix);
        args.AddRange(new[] { "-f", "lavfi", "-i", DdagrabSource(options) });
        args.AddRange(Output(options));
        return args.ToArray();
    }

    public static string[] Gdigrab(RecordingOptions options)
    {
        var region = options.Region.ToEven();
        var args = new List<string>(FfmpegRunner.StandardPrefix);
        args.AddRange(new[]
        {
            "-f", "gdigrab",
            "-framerate", options.FrameRate.ToFfmpegString(),
            "-draw_mouse", options.ShowCursor ? "1" : "0",
            "-offset_x", region.X.ToString(CultureInfo.InvariantCulture),
            "-offset_y", region.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{region.Width}x{region.Height}",
            "-i", "desktop",
        });
        args.AddRange(Output(options));
        return args.ToArray();
    }

    public static string[] For(Grabber grabber, RecordingOptions options) =>
        grabber == Grabber.Ddagrab ? Ddagrab(options) : Gdigrab(options);

    /// <summary>
    /// Encoder, constant output rate (gdigrab under load delivers fewer frames than asked; -r
    /// duplicates so the file still plays at the chosen rate), pixel format, fragmented MP4
    /// (playable even if the process dies) and the output path.
    /// </summary>
    private static IEnumerable<string> Output(RecordingOptions options)
    {
        foreach (var arg in EncoderArguments(options)) yield return arg;
        yield return "-r"; yield return options.FrameRate.ToFfmpegString();
        yield return "-pix_fmt"; yield return "yuv420p";
        yield return "-movflags"; yield return "frag_keyframe+empty_moov";
        yield return "-f"; yield return "mp4";
        yield return "-y"; yield return options.OutputPath;
    }

    /// <summary>Quality 1-100 to an H.264 CRF/CQ/QP: 100 → 19, 75 → 23, 1 → 35.</summary>
    public static int QualityToCrf(int quality)
    {
        int q = Math.Clamp(quality, 1, 100);
        return (int)Math.Round(35 - 16 * (q - 1) / 99.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Bitrate in kbit/s for encoders without a quality mode, from pixel rate and quality.</summary>
    public static int QualityToKbps(RecordingOptions options)
    {
        var region = options.Region.ToEven();
        double bitsPerPixel = 0.03 + 0.17 * Math.Clamp(options.Quality, 1, 100) / 100.0;
        double bps = region.Width * (double)region.Height * options.FrameRate.Value * bitsPerPixel;
        return Math.Max(500, (int)Math.Round(bps / 1000));
    }

    public static string[] EncoderArguments(RecordingOptions options)
    {
        string crf = QualityToCrf(options.Quality).ToString(CultureInfo.InvariantCulture);
        // One-second GOP: fragments flush every second, so a crash loses at most that much.
        string gop = options.FrameRate.Rounded.ToString(CultureInfo.InvariantCulture);
        return options.Encoder.Name switch
        {
            "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", crf, "-b:v", "0", "-g", gop },
            "h264_qsv" => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", crf, "-g", gop },
            "h264_amf" => new[] { "-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", crf, "-qp_p", crf, "-g", gop },
            "libx264" => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", crf, "-g", gop },
            "h264_mf" => new[] { "-c:v", "h264_mf", "-rate_control", "quality", "-quality", Math.Clamp(options.Quality, 1, 100).ToString(CultureInfo.InvariantCulture), "-g", gop },
            "libopenh264" => new[] { "-c:v", "libopenh264", "-b:v", QualityToKbps(options) + "k", "-g", gop },
            _ => new[] { "-c:v", options.Encoder.Name, "-g", gop },
        };
    }

    /// <summary>Stream-copy a fragmented recording into a normal faststart MP4.</summary>
    public static string[] Remux(string inputPath, string outputPath) =>
        FfmpegRunner.StandardPrefix.Concat(new[]
        {
            "-i", inputPath,
            "-c", "copy",
            "-movflags", "+faststart",
            "-f", "mp4",
            "-y", outputPath,
        }).ToArray();

    /// <summary>Join pause segments with the concat demuxer (no re-encode) into a faststart MP4.</summary>
    public static string[] Concat(string listPath, string outputPath) =>
        FfmpegRunner.StandardPrefix.Concat(new[]
        {
            "-f", "concat",
            "-safe", "0",
            "-i", listPath,
            "-c", "copy",
            "-movflags", "+faststart",
            "-f", "mp4",
            "-y", outputPath,
        }).ToArray();

    /// <summary>The concat demuxer list file body: one <c>file '…'</c> line per segment, quotes escaped.</summary>
    public static string ConcatList(IEnumerable<string> segmentPaths)
    {
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var path in segmentPaths)
        {
            sb.Append("file '").Append(path.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
        }
        return sb.ToString();
    }
}
