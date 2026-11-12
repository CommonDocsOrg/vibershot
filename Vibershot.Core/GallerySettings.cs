namespace Vibershot.Core;

/// <summary>
/// Gallery section of <see cref="AppConfig"/>. Legacy configs without this section
/// deserialize to these defaults.
/// </summary>
public sealed class GallerySettings
{
    /// <summary>Root of the library; Screenshots, Recordings and Projects live under it.</summary>
    public string LibraryRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibershot");

    /// <summary>Additional folders scanned and watched by the gallery.</summary>
    public List<string> ExtraFolders { get; set; } = new();

    /// <summary>Tile width in device-independent pixels.</summary>
    public int ThumbnailSize { get; set; } = 200;

    public bool ShowDetailPane { get; set; } = true;

    /// <summary>When set, a capture opens the gallery with the new item selected instead of the preview.</summary>
    public bool OpenGalleryAfterCapture { get; set; } = false;

    // Last window placement; null until the gallery has been closed once.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    public string ProjectsFolder => Path.Combine(LibraryRoot, "Projects");
}
