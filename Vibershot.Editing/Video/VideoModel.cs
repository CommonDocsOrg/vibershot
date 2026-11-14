using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Vibershot.Media;

namespace Vibershot.Editing.Video;

public enum TrackKind { Visual, Audio, Caption }

/// <summary>A media file the project links to. The file is never copied into the project.</summary>
public sealed record MediaSource
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Absolute path when the project was last saved or the file was imported.</summary>
    public string Path { get; init; } = "";

    /// <summary>Path relative to the project file, so a project folder can move as a whole.</summary>
    public string? RelativePath { get; init; }

    /// <summary>Length in seconds.</summary>
    public double Duration { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
    public string? FrameRate { get; init; }
    public bool HasVideo { get; init; } = true;
    public bool HasAudio { get; init; }

    /// <summary>A still picture (PNG, JPEG…): no duration of its own, so its clips can be any length.</summary>
    public bool IsImage { get; init; }
    public long FileSize { get; init; }

    /// <summary>True when the source can go on a visual track.</summary>
    [JsonIgnore] public bool HasPicture => HasVideo || IsImage;

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);

    public static MediaSource From(MediaInfo info) => new()
    {
        Path = info.Path,
        Duration = info.Duration.TotalSeconds,
        Width = info.Rotation is 90 or 270 ? info.Height : info.Width,
        Height = info.Rotation is 90 or 270 ? info.Width : info.Height,
        FrameRate = info.FrameRate?.ToFfmpegString(),
        HasVideo = info.HasVideo,
        HasAudio = info.HasAudio,
        FileSize = info.FileSize,
    };

    public static MediaSource ForImage(string path, int width, int height) => new()
    {
        Path = path, Width = width, Height = height, HasVideo = false, HasAudio = false, IsImage = true,
        FileSize = File.Exists(path) ? new FileInfo(path).Length : 0,
    };
}

/// <summary>
/// Where a picture sits on the canvas, in canvas pixels: the box the visible (cropped) part of
/// the picture is stretched into, turned about its centre. Crop values are fractions of the
/// source picture cut from each side.
/// </summary>
public sealed record Placement
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    /// <summary>Degrees clockwise.</summary>
    public double Rotation { get; init; }

    public double Opacity { get; init; } = 1;
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropRight { get; init; }
    public double CropBottom { get; init; }

    [JsonIgnore] public Rect Bounds => new(X, Y, Math.Max(0, Width), Math.Max(0, Height));
    [JsonIgnore] public Point Center => new(X + Width / 2, Y + Height / 2);
    [JsonIgnore] public bool IsCropped => CropLeft > 0 || CropTop > 0 || CropRight > 0 || CropBottom > 0;

    /// <summary>The box the whole uncropped picture would cover, before rotation.</summary>
    [JsonIgnore] public Rect FullBounds
    {
        get
        {
            double w = Width / Math.Max(0.01, 1 - CropLeft - CropRight), h = Height / Math.Max(0.01, 1 - CropTop - CropBottom);
            return new Rect(X - CropLeft * w, Y - CropTop * h, w, h);
        }
    }

    public Placement WithBounds(Rect bounds) => this with { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
}

/// <summary>
/// One piece of a source placed on a track. <see cref="Start"/> and <see cref="Duration"/> are
/// whole frames at the project rate; <see cref="SourceIn"/> is seconds into the source file.
/// Clips that share a <see cref="LinkId"/> (a video and its sound) move, trim and split together.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VideoClip), "video")]
[JsonDerivedType(typeof(ImageClip), "image")]
[JsonDerivedType(typeof(GraphicClip), "graphic")]
[JsonDerivedType(typeof(CaptionClip), "caption")]
[JsonDerivedType(typeof(AudioClip), "audio")]
public abstract record Clip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceId { get; init; }
    public long Start { get; init; }
    public long Duration { get; init; }
    public double SourceIn { get; init; }
    public Guid? LinkId { get; init; }

    [JsonIgnore] public long End => Start + Duration;
}

/// <summary>A clip that draws on the canvas. Without a <see cref="Placement"/> it is fitted inside the canvas and centred.</summary>
public abstract record VisualClip : Clip
{
    public Placement? Placement { get; init; }
}

public sealed record VideoClip : VisualClip;

/// <summary>A still picture held on screen for the clip's length.</summary>
public sealed record ImageClip : VisualClip;

/// <summary>
/// Text, a callout, a shape, an arrow or a stamp drawn on the canvas for the clip's length. It
/// wraps the image editor's <see cref="Item"/>, whose bounds are in canvas pixels, so
/// the same renderer draws it in the preview and for the export. It sits on a visual track and
/// layers with the pictures there.
/// </summary>
public sealed record GraphicClip : Clip
{
    public required Item Item { get; init; }
}

/// <summary>One caption on a caption track; the track's <see cref="CaptionStyle"/> decides how it looks.</summary>
public sealed record CaptionClip : Clip
{
    public string Text { get; init; } = "";
}

/// <summary>How every caption on a track is drawn. Sizes are canvas pixels.</summary>
public sealed record CaptionStyle
{
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 54;
    public bool Bold { get; init; } = true;
    public Color Color { get; init; } = Colors.White;

    /// <summary>The box behind the words; null draws the words alone. Written even when null, or "no box" would load back as the default box.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Color? Background { get; init; } = Color.FromArgb(0xB4, 0, 0, 0);

    /// <summary>Colour of the line round the letters.</summary>
    public Color OutlineColor { get; init; } = Colors.Black;

    /// <summary>Width of the line round the letters; 0 draws none.</summary>
    public double OutlineWidth { get; init; }

    /// <summary>Captions sit at the bottom of the picture, or at the top when this is set.</summary>
    public bool AtTop { get; init; }
}

public sealed record AudioClip : Clip
{
    /// <summary>Linear gain: 1 leaves the sound unchanged.</summary>
    public double Volume { get; init; } = 1;
    public bool Muted { get; init; }
}

public sealed record Track
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Track";
    [JsonConverter(typeof(JsonStringEnumConverter<TrackKind>))]
    public TrackKind Kind { get; init; }

    /// <summary>A hidden visual track is skipped by the preview and the export.</summary>
    public bool Hidden { get; init; }

    /// <summary>A muted audio track is left out of the mix.</summary>
    public bool Muted { get; init; }

    public bool Locked { get; init; }

    /// <summary>For a caption track: how its captions are drawn. Null uses the defaults scaled to the canvas.</summary>
    public CaptionStyle? CaptionStyle { get; init; }

    /// <summary>Clips in start order; clips on one track never overlap.</summary>
    public ImmutableList<Clip> Clips { get; init; } = ImmutableList<Clip>.Empty;
}

/// <summary>
/// The whole edit: an output canvas, linked sources and tracks. Visual tracks are listed bottom
/// layer first, so a later visual track covers an earlier one.
/// </summary>
public sealed record VideoProject
{
    public const int CurrentVersion = 1;
    public const int MaxDimension = 3840;

    public int Version { get; init; } = CurrentVersion;
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;

    /// <summary>Project rate as ffmpeg writes it: "30", "60" or "30000/1001".</summary>
    public string FrameRate { get; init; } = "30";

    public Color Background { get; init; } = Colors.Black;
    public ImmutableList<MediaSource> Sources { get; init; } = ImmutableList<MediaSource>.Empty;
    public ImmutableList<Track> Tracks { get; init; } = ImmutableList<Track>.Empty;

    [JsonIgnore] public Vibershot.Media.FrameRate Rate => Vibershot.Media.FrameRate.TryParse(FrameRate, out var rate) ? rate : Vibershot.Media.FrameRate.Fps30;

    [JsonIgnore] public IEnumerable<Clip> AllClips => Tracks.SelectMany(track => track.Clips);

    /// <summary>Length in frames: the end of the last clip.</summary>
    [JsonIgnore] public long Duration => Tracks.Count == 0 ? 0 : Tracks.Max(track => track.Clips.Count == 0 ? 0 : track.Clips.Max(clip => clip.End));

    public double Seconds(long frames) => frames * (double)Rate.Denominator / Rate.Numerator;

    public long Frames(double seconds) => (long)Math.Round(seconds * Rate.Numerator / Rate.Denominator, MidpointRounding.AwayFromZero);

    public MediaSource? Source(Guid id) => Sources.FirstOrDefault(source => source.Id == id);
}
