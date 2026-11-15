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

    /// <summary>Record what the computer plays, on its own track.</summary>
    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>Record a microphone, on its own track. Off until the user turns it on.</summary>
    public bool RecordMicrophone { get; set; }

    /// <summary>The microphone last picked (a Windows endpoint id); null follows the default device.</summary>
    public string? MicrophoneId { get; set; }

    /// <summary>Microphone level as a percentage: 100 leaves it unchanged, 200 doubles it.</summary>
    public int MicrophoneVolume { get; set; } = 100;

    /// <summary>Record a webcam as its own video layer. Off until the user turns it on.</summary>
    public bool RecordCamera { get; set; }

    /// <summary>The camera last picked, by name; null takes the first one found.</summary>
    public string? CameraName { get; set; }

    /// <summary>"Circle", "Rounded" or "Rectangle": the shape of the camera bubble and of the camera clip in the editor.</summary>
    public string CameraShape { get; set; } = "Circle";

    /// <summary>Show and record the camera mirrored, the way a mirror would show you.</summary>
    public bool CameraMirror { get; set; } = true;

    /// <summary>Height of the camera bubble in screen pixels.</summary>
    public int CameraSize { get; set; } = 220;

    /// <summary>
    /// Skip the choices shown before a recording and start at once with the remembered ones. The
    /// choices are still shown, so they can be changed for next time while recording.
    /// </summary>
    public bool StartImmediately { get; set; }

    /// <summary>What a recording will capture besides the screen, in a few words for the main window.</summary>
    public string Summary()
    {
        if (Format != VideoFormat.Mp4) return "Screen only (AVI)";
        var parts = new List<string> { "Screen" };
        if (RecordSystemAudio) parts.Add("system audio");
        if (RecordMicrophone) parts.Add("microphone");
        if (RecordCamera) parts.Add("camera");
        return string.Join(" + ", parts);
    }

    public static int ClampQuality(int quality) => Math.Clamp(quality, 1, 100);
}
