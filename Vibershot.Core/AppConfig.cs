using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vibershot.Core;

public enum ImageFormat
{
    Png,
    Jpeg,
    Webp,
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

    // Parsed by the UI into a global hotkey (Ctrl+Shift+S by default).
    public string HotkeyDescription { get; set; } = "Ctrl+Shift+S";

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
