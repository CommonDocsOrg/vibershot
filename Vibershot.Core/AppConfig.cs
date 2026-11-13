using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vibershot.Core;

public enum ImageFormat
{
    Png,
    Jpeg,

    /// <summary>Lossy WebP at <see cref="AfterCaptureSettings.WebpQuality"/> percent.</summary>
    Webp,

    /// <summary>Lossless WebP (exact pixels, smaller than PNG).</summary>
    WebpLossless,

    /// <summary>The editable <c>.vibeshot</c> project: full-quality pixels plus layers, text and shapes.</summary>
    Vibeshot,
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
/// Configs written before November 2026 kept flat fields (OutputFolder, Autosave, …); those
/// are read once by <see cref="Load"/>, folded into the sections and never written again.
/// </summary>
public sealed class AppConfig
{
    // ---- Legacy flat fields: read for migration only. Null means "not in the file". ----
    [JsonPropertyName("OutputFolder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LegacyOutputFolder { get; set; }
    [JsonPropertyName("Autosave"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? LegacyAutosave { get; set; }
    [JsonPropertyName("ShowSaveDialog"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? LegacyShowSaveDialog { get; set; }
    [JsonPropertyName("CopyToClipboard"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? LegacyCopyToClipboard { get; set; }
    [JsonPropertyName("Format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImageFormat? LegacyFormat { get; set; }
    [JsonPropertyName("WebpQuality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? LegacyWebpQuality { get; set; }
    [JsonPropertyName("HotkeyDescription"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LegacyHotkeyDescription { get; set; }

    /// <summary>After selecting a region, ask whether to take a screenshot, a video or a scrolling photo.</summary>
    public bool ShowCaptureActions { get; set; } = true;

    // On by default: pressing Print Screen opens the region tool. Windows may still hold the key
    // for Snipping Tool, in which case registration fails and the launcher explains what to change.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PrintScreenAction PrintScreen { get; set; } = PrintScreenAction.CaptureTool;

    /// <summary>The last custom fixed size typed into the region tool, as "1280x720" (screen pixels).</summary>
    public string CustomRegionSize { get; set; } = "1280x720";

    /// <summary>Parses <see cref="CustomRegionSize"/>; falls back to 1280 x 720 when unreadable.</summary>
    public (int Width, int Height) CustomRegionPixels()
    {
        var parts = (CustomRegionSize ?? "").ToLowerInvariant().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int w) && int.TryParse(parts[1].Trim(), out int h) && w >= 16 && h >= 16 && w <= 32768 && h <= 32768) return (w, h);
        return (1280, 720);
    }

    private bool? _setupCompleted;
    /// <summary>True once the first-run page has been answered (or the config predates it).</summary>
    public bool SetupCompleted { get => _setupCompleted ?? false; set => _setupCompleted = value; }

    /// <summary>
    /// True when the Windows installer chose the library and startup options, so the welcome page
    /// skips those cards. Set by <see cref="InstallChoices"/> on the first launch after installing.
    /// </summary>
    public bool InstalledByInstaller { get; set; }

    // Automatic update check; missing in older configs, so the defaults (daily check) apply.
    private UpdateSettings _updates = new();
    public UpdateSettings Updates { get => _updates; set => _updates = value ?? new UpdateSettings(); }

    private AfterCaptureSettings _afterCapture = new();
    private bool _afterCaptureRead;
    public AfterCaptureSettings AfterCapture { get => _afterCapture; set { _afterCapture = value ?? new AfterCaptureSettings(); _afterCaptureRead = true; } }

    private HotkeySettings _hotkeys = new();
    public HotkeySettings Hotkeys { get => _hotkeys; set => _hotkeys = value ?? new HotkeySettings(); }

    // Image editor defaults; missing in older configs, so it falls back to EditorSettings defaults.
    private EditorSettings _editor = new();
    public EditorSettings Editor { get => _editor; set => _editor = value ?? new EditorSettings(); }

    // Recording settings; missing from legacy configs, so the setter keeps a null out.
    private VideoSettings _video = new();
    public VideoSettings Video { get => _video; set => _video = value ?? new VideoSettings(); }

    // Video editor preview and export preferences; missing in older configs, so defaults apply.
    private VideoEditorSettings _videoEditor = new();
    public VideoEditorSettings VideoEditor { get => _videoEditor; set => _videoEditor = value ?? new VideoEditorSettings(); }

    // Gallery library, thumbnails and window placement. Missing in legacy configs: defaults apply.
    private GallerySettings _gallery = new();
    private bool _galleryRead;
    public GallerySettings Gallery { get => _gallery; set { _gallery = value ?? new GallerySettings(); _galleryRead = true; } }

    // ---- Convenience views used across the app. ----

    /// <summary>Where dialogs start and "Open folder" goes: the library copy, else the first custom destination.</summary>
    [JsonIgnore] public string OutputFolder => AfterCapture.PrimaryFolder(Gallery);

    [JsonIgnore] public ImageFormat Format => AfterCapture.Format;
    [JsonIgnore] public int WebpQuality => AfterCapture.WebpQuality;

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
                var cfg = FromJson(json);
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

        return FreshInstall(InstallChoices.FilePath);
    }

    /// <summary>
    /// A brand-new config. When the installer left its choices in <paramref name="seedPath"/>,
    /// they are applied, the config is saved so they stick, and the seed is removed.
    /// </summary>
    public static AppConfig FreshInstall(string seedPath)
    {
        var cfg = new AppConfig();
        var choices = InstallChoices.TryRead(seedPath);
        if (choices is null) return cfg;
        choices.ApplyTo(cfg);
        try { cfg.Save(); } catch { }
        InstallChoices.Delete(seedPath);
        return cfg;
    }

    /// <summary>Reads a config that already exists on disk, folding legacy flat fields into the sections.</summary>
    public static AppConfig? FromJson(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json);
        cfg?.MigrateLegacy();
        return cfg;
    }

    /// <summary>
    /// Folds pre-section fields into <see cref="AfterCapture"/>, <see cref="Hotkeys"/> and
    /// <see cref="Gallery"/>. Only runs for an existing file; a fresh install keeps the new defaults.
    /// </summary>
    public void MigrateLegacy()
    {
        // An existing user has already been through setup in spirit: never show first run to them.
        _setupCompleted ??= true;

        if (!_galleryRead)
        {
            // Older installs kept the library in AppData; leave their files where they are.
            _gallery.LibraryRoot = GallerySettings.LegacyLibraryRoot;
        }

        if (!_afterCaptureRead)
        {
            var ac = _afterCapture;
            ac.SaveAsFile = LegacyAutosave ?? true;
            ac.AskWhereToSave = LegacyShowSaveDialog ?? false;
            if (ac.AskWhereToSave) ac.SaveAsFile = true;
            ac.CopyToClipboard = LegacyCopyToClipboard ?? true;
            // Existing users keep the flat image format they chose; only new installs default to projects.
            ac.Format = LegacyFormat ?? ImageFormat.Png;
            ac.WebpQuality = LegacyWebpQuality ?? 80;
            ac.OpenGallery = _gallery.LegacyOpenGalleryAfterCapture ?? false;
            ac.OpenEditor = _editor.LegacyOpenEditorAfterCapture ?? false;

            // No saved output folder means the old default, which was the library's Screenshots folder.
            var legacyFolder = LegacyOutputFolder;
            if (legacyFolder is null || AfterCaptureSettings.SameFolder(legacyFolder, _gallery.ScreenshotsFolder))
            {
                ac.SaveToLibrary = true;
            }
            else
            {
                ac.SaveToLibrary = false;
                ac.Destinations.Add(new SaveDestination { Path = legacyFolder, Enabled = true });
                // Their shots used to show in the gallery; keep that true by watching the folder.
                _gallery.AddExtraFolder(legacyFolder);
            }
        }

        if (!string.IsNullOrWhiteSpace(LegacyHotkeyDescription) && HotkeyGesture.TryParse(LegacyHotkeyDescription, out var gesture) && gesture.IsValid)
            _hotkeys.CaptureRegion = gesture.ToString();

        LegacyOutputFolder = null; LegacyAutosave = null; LegacyShowSaveDialog = null; LegacyCopyToClipboard = null;
        LegacyFormat = null; LegacyWebpQuality = null; LegacyHotkeyDescription = null;
        _gallery.LegacyOpenGalleryAfterCapture = null;
        _editor.LegacyOpenEditorAfterCapture = null;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(ConfigPath, ToJson());
    }

    public string ToJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };
        return JsonSerializer.Serialize(this, options);
    }
}
