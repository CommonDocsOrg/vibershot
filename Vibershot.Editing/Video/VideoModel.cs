using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Vibershot.Media;

namespace Vibershot.Editing.Video;

public enum TrackKind { Visual, Audio }

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
    public long FileSize { get; init; }

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
}

/// <summary>
/// One piece of a source placed on a track. <see cref="Start"/> and <see cref="Duration"/> are
/// whole frames at the project rate; <see cref="SourceIn"/> is seconds into the source file.
/// Clips that share a <see cref="LinkId"/> (a video and its sound) move, trim and split together.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VideoClip), "video")]
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

public sealed record VideoClip : Clip;

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
