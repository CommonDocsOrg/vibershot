using System.Text.Json.Serialization;

namespace Vibershot.Core;

public enum VideoFormat
{
    /// <summary>H.264 MP4 through ffmpeg at the chosen frame rate.</summary>
    Mp4,

    /// <summary>Legacy Motion JPEG AVI from the built-in GDI recorder (10 fps).</summary>
    Avi,
}

/// <summary>
/// Recording settings under <see cref="AppConfig.Video"/>. Absent from older config files, so
/// every property has the default a fresh install should get.
/// </summary>
public sealed class VideoSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoFormat Format { get; set; } = VideoFormat.Mp4;

    /// <summary>A FrameRate preset as text: "24", "25", "29.97", "30", "50", "59.94", "60" or "30000/1001".</summary>
    public string FrameRate { get; set; } = "30";

    /// <summary>1-100; mapped to CRF / CQ per encoder.</summary>
    public int Quality { get; set; } = 75;

    public bool PreferHardwareEncoder { get; set; } = true;

    public bool ShowCursor { get; set; } = true;

    public static int ClampQuality(int quality) => Math.Clamp(quality, 1, 100);
}
