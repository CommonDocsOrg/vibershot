using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace Vibershot.Gallery;

/// <summary>How a file in the library came to be.</summary>
public enum CaptureKind
{
    Unknown,
    Region,
    Window,
    FullDesktop,
    Scrolling,
    Video,

    /// <summary>Dropped into the gallery or copied into a library folder by hand.</summary>
    Imported,
}

/// <summary>
/// Metadata stored next to a capture as <c>&lt;name&gt;.vibe.json</c>. The media file stays
/// a plain image or video; losing the sidecar loses only this metadata.
/// </summary>
public sealed record CaptureSidecar
{
    public const string Suffix = ".vibe.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public CaptureKind Kind { get; set; } = CaptureKind.Unknown;

    /// <summary>Process name of the window under the capture, when known ("chrome", "Code").</summary>
    public string? SourceProcess { get; set; }

    /// <summary>Title of that window at capture time, when known.</summary>
    public string? SourceTitle { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Length of a video in seconds; null for images.</summary>
    public double? DurationSeconds { get; set; }

    public DateTime Created { get; set; } = DateTime.Now;

    public List<string> Tags { get; set; } = new();

    public bool Favourite { get; set; }

    public static string PathFor(string mediaPath) => mediaPath + Suffix;

    public static bool IsSidecar(string path) => path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The media file a sidecar path belongs to.</summary>
    public static string MediaPathFor(string sidecarPath) => sidecarPath[..^Suffix.Length];

    public static CaptureSidecar ForImage(BitmapSource image, CaptureKind kind, string? sourceProcess = null, string? sourceTitle = null, DateTime? created = null) =>
        new() { Kind = kind, SourceProcess = sourceProcess, SourceTitle = sourceTitle, Width = image.PixelWidth, Height = image.PixelHeight, Created = created ?? DateTime.Now };

    public static CaptureSidecar ForVideo(int width, int height, double durationSeconds, string? sourceProcess = null, string? sourceTitle = null, DateTime? created = null) =>
        new() { Kind = CaptureKind.Video, SourceProcess = sourceProcess, SourceTitle = sourceTitle, Width = width, Height = height, DurationSeconds = durationSeconds, Created = created ?? DateTime.Now };

    /// <summary>Writes the sidecar next to <paramref name="mediaPath"/>. Never throws: a capture must not fail because metadata could not be written.</summary>
    public bool TryWrite(string mediaPath)
    {
        try { Write(mediaPath); return true; }
        catch { return false; }
    }

    public void Write(string mediaPath)
    {
        var path = PathFor(mediaPath);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }

    public static CaptureSidecar? Read(string mediaPath)
    {
        var path = PathFor(mediaPath);
        if (!File.Exists(path)) return null;
        try
        {
            var sidecar = JsonSerializer.Deserialize<CaptureSidecar>(File.ReadAllText(path), Options);
            if (sidecar is null) return null;
            sidecar.Tags ??= new List<string>();
            return sidecar;
        }
        catch
        {
            return null; // Corrupt sidecar: the file still shows, without its metadata.
        }
    }

    public static string NormalizeTag(string tag) => tag.Trim().TrimStart('#');

    /// <summary>Adds a tag unless an equal one (ignoring case) exists. Returns true when the list changed.</summary>
    public bool AddTag(string tag)
    {
        tag = NormalizeTag(tag);
        if (tag.Length == 0 || HasTag(tag)) return false;
        Tags.Add(tag);
        return true;
    }

    public bool RemoveTag(string tag) => Tags.RemoveAll(t => string.Equals(t, NormalizeTag(tag), StringComparison.OrdinalIgnoreCase)) > 0;

    public bool HasTag(string tag) => Tags.Any(t => string.Equals(t, NormalizeTag(tag), StringComparison.OrdinalIgnoreCase));
}
