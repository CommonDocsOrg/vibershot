using System.Text.Json.Serialization;

namespace Vibershot.Core;

/// <summary>
/// Automatic tidying of the library. Off by default; only ever touches the library's own
/// Screenshots and Recordings folders and the recording recovery folder, never custom save
/// destinations, watched folders or projects. Files go to the Recycle Bin.
/// </summary>
public sealed class CleanupSettings
{
    public bool Enabled { get; set; } = false;

    public bool Screenshots { get; set; } = true;
    public int ScreenshotDays { get; set; } = 30;

    public bool Recordings { get; set; } = false;
    public int RecordingDays { get; set; } = 14;

    /// <summary>Recovery copies of recordings that were never saved.</summary>
    public bool Recovery { get; set; } = true;
    public int RecoveryDays { get; set; } = 7;

    /// <summary>When the last pass ran; a pass runs at most once a day.</summary>
    public DateTime? LastRun { get; set; }

    public static int ClampDays(int days) => Math.Clamp(days, 1, 3650);
}

/// <summary>
/// Gallery section of <see cref="AppConfig"/>. Legacy configs without this section
/// deserialize to these defaults and are then moved to the old library root by
/// <see cref="AppConfig.Load"/>.
/// </summary>
public sealed class GallerySettings
{
    /// <summary>The library root used before captures moved to the Pictures folder.</summary>
    public static string LegacyLibraryRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibershot");

    /// <summary>
    /// Default library: next to the Windows Screenshots folder under Pictures, so it sits where
    /// people already look for captures (and syncs when Pictures is backed up). Falls back to
    /// AppData when Pictures cannot be resolved.
    /// </summary>
    public static string DefaultLibraryRoot()
    {
        string pictures;
        try { pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); }
        catch { pictures = ""; }
        if (string.IsNullOrWhiteSpace(pictures)) return LegacyLibraryRoot;
        return Path.Combine(pictures, "Screenshots", "Vibershot Gallery");
    }

    /// <summary>Root of the library; Screenshots, Recordings and Projects live under it.</summary>
    public string LibraryRoot { get; set; } = DefaultLibraryRoot();

    /// <summary>Additional folders scanned and watched by the gallery.</summary>
    public List<string> ExtraFolders { get; set; } = new();

    /// <summary>Tile width in device-independent pixels.</summary>
    public int ThumbnailSize { get; set; } = 200;

    public bool ShowDetailPane { get; set; } = true;

    /// <summary>Pre-2026-11 switch; now <see cref="AfterCaptureSettings.OpenGallery"/>. Read for migration, never written.</summary>
    [JsonPropertyName("OpenGalleryAfterCapture"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyOpenGalleryAfterCapture { get; set; }

    private CleanupSettings _cleanup = new();
    public CleanupSettings Cleanup { get => _cleanup; set => _cleanup = value ?? new CleanupSettings(); }

    // Last window placement; null until the gallery has been closed once.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    [JsonIgnore] public string ScreenshotsFolder => Path.Combine(LibraryRoot, "Screenshots");
    [JsonIgnore] public string RecordingsFolder => Path.Combine(LibraryRoot, "Recordings");
    [JsonIgnore] public string ProjectsFolder => Path.Combine(LibraryRoot, "Projects");

    /// <summary>Adds a watched folder unless it is already listed or inside the library.</summary>
    public bool AddExtraFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (ExtraFolders.Any(f => AfterCaptureSettings.SameFolder(f, folder))) return false;
        if (AfterCaptureSettings.SameFolder(folder, LibraryRoot) || AfterCaptureSettings.SameFolder(folder, ScreenshotsFolder) || AfterCaptureSettings.SameFolder(folder, RecordingsFolder) || AfterCaptureSettings.SameFolder(folder, ProjectsFolder)) return false;
        ExtraFolders.Add(folder);
        return true;
    }
}
