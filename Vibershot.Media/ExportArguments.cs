using System.Globalization;
using System.Text;

namespace Vibershot.Media;

/// <summary>
/// One picture (video or still) on the output timeline. Times are seconds. X, Y, Width and Height
/// are the box, in output pixels, that the visible part of the picture is stretched into before
/// it is turned about the box's centre.
/// </summary>
public sealed record ExportVideoClip(string Path, double SourceIn, double Duration, double Start, double X, double Y, int Width, int Height)
{
    /// <summary>Degrees clockwise.</summary>
    public double Rotation { get; init; }

    public double Opacity { get; init; } = 1;

    /// <summary>Fractions of the source cut from each side before scaling.</summary>
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropRight { get; init; }
    public double CropBottom { get; init; }

    /// <summary>A still picture: looped for the clip's length instead of seeked.</summary>
    public bool IsImage { get; init; }
}

/// <summary>
/// A ready-timed run of same-sized transparent pictures (captions), listed in an ffconcat file
/// that covers the whole output from time zero, drawn over everything else with its top-left
/// corner at X, Y in output pixels.
/// </summary>
public sealed record ExportOverlayStream(string ListPath, double X, double Y);

/// <summary>One audio clip on the output timeline. Times are seconds; volume 1 is unchanged.</summary>
public sealed record ExportAudioClip(string Path, double SourceIn, double Duration, double Start, double Volume = 1);

/// <summary>A flattened timeline ready to render: layers bottom to top, audio clips in any order.</summary>
public sealed record ExportPlan
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required FrameRate FrameRate { get; init; }

    /// <summary>Length of the output in seconds.</summary>
    public required double Duration { get; init; }

    /// <summary>Canvas colour as ffmpeg understands it ("black", "0x282A36").</summary>
    public string Background { get; init; } = "black";

    /// <summary>Video clips, lowest layer first: a later clip is drawn over an earlier one.</summary>
    public IReadOnlyList<ExportVideoClip> Video { get; init; } = Array.Empty<ExportVideoClip>();

    /// <summary>Picture streams drawn over every video clip, in order.</summary>
    public IReadOnlyList<ExportOverlayStream> Overlays { get; init; } = Array.Empty<ExportOverlayStream>();

    public IReadOnlyList<ExportAudioClip> Audio { get; init; } = Array.Empty<ExportAudioClip>();
}

/// <summary>
/// Pure builders that turn an <see cref="ExportPlan"/> into ffmpeg arguments: one filter graph
/// that lays every clip over a colour canvas at its timeline position and mixes the audio.
/// </summary>
public static class ExportArguments
{
    public const int AudioRate = 48000;

    private static string N(double value) => Math.Max(0, value).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A fraction between 0 and 1.</summary>
    private static string F(double value) => Math.Clamp(value, 0, 1).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A signed position: a box may hang off the canvas.</summary>
    private static string S(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Arguments for the final MP4.</summary>
    public static string[] Build(ExportPlan plan, EncoderInfo encoder, int quality, string outputPath, bool includeAudio = true)
    {
        var args = new List<string>(FfmpegRunner.StandardPrefix);
        var graph = new StringBuilder();
        int input = 0;
        string rate = plan.FrameRate.ToFfmpegString();
        graph.Append(CultureInfo.InvariantCulture, $"color=c={plan.Background}:s={plan.Width}x{plan.Height}:r={rate}:d={N(plan.Duration)},format=yuv420p[bg]");
        string last = "bg";
        foreach (var clip in plan.Video)
        {
            if (clip.IsImage) args.AddRange(new[] { "-loop", "1", "-framerate", rate, "-t", N(clip.Duration), "-i", clip.Path });
            else args.AddRange(new[] { "-ss", N(clip.SourceIn), "-t", N(clip.Duration), "-i", clip.Path });
            // Crop, stretch into the clip's box, fade and turn it, shift it to its place on the
            // timeline, then draw it about the box's centre for exactly its own span.
            graph.Append(CultureInfo.InvariantCulture, $";[{input}:v]fps={rate}");
            if (clip.CropLeft > 0 || clip.CropTop > 0 || clip.CropRight > 0 || clip.CropBottom > 0)
                graph.Append(CultureInfo.InvariantCulture, $",crop=iw*{F(1 - clip.CropLeft - clip.CropRight)}:ih*{F(1 - clip.CropTop - clip.CropBottom)}:iw*{F(clip.CropLeft)}:ih*{F(clip.CropTop)}");
            graph.Append(CultureInfo.InvariantCulture, $",scale={clip.Width}:{clip.Height}:flags=lanczos,setsar=1");
            bool turned = Math.Abs(clip.Rotation % 360) > 0.005;
            if (clip.Opacity < 0.999 || turned) graph.Append(",format=rgba");
            if (clip.Opacity < 0.999) graph.Append(CultureInfo.InvariantCulture, $",colorchannelmixer=aa={F(clip.Opacity)}");
            if (turned)
            {
                string angle = (clip.Rotation * Math.PI / 180).ToString("0.######", CultureInfo.InvariantCulture);
                graph.Append(CultureInfo.InvariantCulture, $",rotate={angle}:ow=rotw({angle}):oh=roth({angle}):c=none");
            }
            graph.Append(CultureInfo.InvariantCulture, $",setpts=PTS-STARTPTS+{N(clip.Start)}/TB[v{input}]");
            graph.Append(CultureInfo.InvariantCulture,
                $";[{last}][v{input}]overlay=x={S(clip.X + clip.Width / 2.0)}-w/2:y={S(clip.Y + clip.Height / 2.0)}-h/2:eof_action=pass:enable='gte(t,{N(clip.Start)})*lt(t,{N(clip.Start + clip.Duration)})'[o{input}]");
            last = "o" + input.ToString(CultureInfo.InvariantCulture);
            input++;
        }
        foreach (var stream in plan.Overlays)
        {
            args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", stream.ListPath });
            // The list times each picture; fps turns that into one frame per output frame.
            graph.Append(CultureInfo.InvariantCulture, $";[{input}:v]fps={rate},format=rgba[v{input}];[{last}][v{input}]overlay=x={S(stream.X)}:y={S(stream.Y)}:eof_action=pass[o{input}]");
            last = "o" + input.ToString(CultureInfo.InvariantCulture);
            input++;
        }
        graph.Append(CultureInfo.InvariantCulture, $";[{last}]format=yuv420p[vout]");
        bool audio = includeAudio && plan.Audio.Count > 0;
        if (audio) AppendAudio(plan, args, graph, ref input);
        args.AddRange(new[] { "-filter_complex", graph.ToString(), "-map", "[vout]" });
        if (audio) args.AddRange(new[] { "-map", "[aout]" });
        args.AddRange(RecordingArguments.EncoderArguments(new RecordingOptions
        {
            Region = new PixelRect(0, 0, plan.Width, plan.Height),
            FrameRate = plan.FrameRate,
            Quality = quality,
            Encoder = encoder,
            OutputPath = outputPath,
        }));
        args.AddRange(new[] { "-r", rate, "-pix_fmt", "yuv420p" });
        if (audio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        args.AddRange(new[] { "-t", N(plan.Duration), "-movflags", "+faststart", "-f", "mp4", "-y", outputPath });
        return args.ToArray();
    }

    /// <summary>Arguments that render only the audio mix to a 16-bit WAV, for preview playback.</summary>
    public static string[] BuildAudioMix(ExportPlan plan, string outputPath)
    {
        var args = new List<string>(FfmpegRunner.StandardPrefix);
        var graph = new StringBuilder();
        int input = 0;
        if (plan.Audio.Count == 0)
        {
            graph.Append(CultureInfo.InvariantCulture, $"anullsrc=r={AudioRate}:cl=stereo,atrim=0:{N(Math.Max(plan.Duration, 0.1))}[aout]");
        }
        else
        {
            AppendAudio(plan, args, graph, ref input);
        }
        args.AddRange(new[] { "-filter_complex", graph.ToString().TrimStart(';'), "-map", "[aout]", "-c:a", "pcm_s16le", "-f", "wav", "-y", outputPath });
        return args.ToArray();
    }

    private static void AppendAudio(ExportPlan plan, List<string> args, StringBuilder graph, ref int input)
    {
        var labels = new StringBuilder();
        foreach (var clip in plan.Audio)
        {
            args.AddRange(new[] { "-ss", N(clip.SourceIn), "-t", N(clip.Duration), "-vn", "-i", clip.Path });
            long delay = (long)Math.Round(clip.Start * 1000);
            graph.Append(CultureInfo.InvariantCulture,
                $";[{input}:a]aresample={AudioRate},aformat=sample_fmts=fltp:channel_layouts=stereo,volume={N(clip.Volume)},adelay={delay}:all=1[a{input}]");
            labels.Append(CultureInfo.InvariantCulture, $"[a{input}]");
            input++;
        }
        graph.Append(CultureInfo.InvariantCulture,
            $";{labels}amix=inputs={plan.Audio.Count}:normalize=0:duration=longest,apad,atrim=0:{N(plan.Duration)}[aout]");
    }
}
