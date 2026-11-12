using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vibershot.Core;

public enum ImageFormat
{
    Png,
    Jpeg,

    /// <summary>Lossy WebP at <see cref="AppConfig.WebpQuality"/> percent.</summary>
    Webp,

    /// <summary>Lossless WebP (exact pixels, smaller than PNG).</summary>
    WebpLossless,
}

/// <summary>
/// What the Print Screen key does when Vibershot takes it over.
/// </summary>
public enum PrintScreenAction
{
    /// <summary>Leave Print Screen to Windows (Snipping Tool or clipboard copy).</summary>
    Off,

    /// <summary>Open the capture region/window tool.</summary>
    CaptureTool,

    /// <summary>Snap the full virtual desktop immediately.</summary>
    FullDesktop,
}

/// <summary>
/// Application settings persisted as JSON under %APPDATA%\Vibershot\config.json.
/// </summary>
public sealed class AppConfig
{
    public string OutputFolder { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Vibershot", "Screenshots");

    public bool ShowCaptureActions { get; set; } = true;
    public bool ShowSaveDialog { get; set; } = false;

    public bool Autosave { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    public ImageFormat Format { get; set; } = ImageFormat.Png;

    // Quality for lossy WebP, 1-100. Only used when Format is ImageFormat.Webp.
    public int WebpQuality { get; set; } = 80;

    // Parsed by the UI into a global hotkey (Ctrl+Shift+S by default).
    public string HotkeyDescription { get; set; } = "Ctrl+Shift+S";

    // Off by default so a fresh install does not silently steal Print Screen from Windows.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PrintScreenAction PrintScreen { get; set; } = PrintScreenAction.Off;

    // Recording settings; missing from legacy configs, so the setter keeps a null out.
    private VideoSettings _video = new();
    public VideoSettings Video { get => _video; set => _video = value ?? new VideoSettings(); }

    private static string ConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Vibershot", "config.json");

    public static AppConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg is not null)
                {
                    return cfg;
                }
            }
            catch
            {
                // Corrupt config -> fall back to defaults below.
            }
        }

        return new AppConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, options));
    }
}
