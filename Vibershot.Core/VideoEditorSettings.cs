using System.Text.Json.Serialization;

namespace Vibershot.Core;

/// <summary>How large the video editor decodes preview frames. Export always uses the originals.</summary>
public enum PreviewQuality
{
    /// <summary>The smallest of full, half and quarter that still fills the preview on screen.</summary>
    Auto,
    Full,
    Half,
    Quarter,
}

/// <summary>
/// Video editor settings under <see cref="AppConfig.VideoEditor"/>. Absent from older config
/// files, so every property has the default a fresh install should get.
/// </summary>
public sealed class VideoEditorSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PreviewQuality PreviewQuality { get; set; } = PreviewQuality.Auto;

    /// <summary>Preview from the 720p proxies (fast seeking, light on the GPU) whenever the quality is not Full.</summary>
    public bool UseProxies { get; set; } = true;

    /// <summary>Decode preview frames on the GPU. Off by default: copying frames back usually costs more than it saves.</summary>
    public bool HardwareDecode { get; set; }

    /// <summary>1-100, mapped per encoder like the recording quality.</summary>
    public int ExportQuality { get; set; } = 80;

    /// <summary>The preview scale for a quality, given how many project pixels one screen pixel covers.</summary>
    public static double Scale(PreviewQuality quality, double viewportToProject) => quality switch
    {
        PreviewQuality.Full => 1,
        PreviewQuality.Half => 0.5,
        PreviewQuality.Quarter => 0.25,
        _ => viewportToProject <= 0.25 ? 0.25 : viewportToProject <= 0.5 ? 0.5 : 1,
    };
}
