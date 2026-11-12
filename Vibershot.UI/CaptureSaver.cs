using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Core;
using Vibershot.Editing;
using Vibershot.Gallery;

namespace Vibershot.UI;

/// <summary>
/// Writes a capture to disk in the configured format. Flat images go through the Core encoder;
/// <see cref="ImageFormat.Vibeshot"/> wraps the full-quality pixels in an editable project with
/// an embedded preview. The gallery sidecar is written beside either.
/// </summary>
public static class CaptureSaver
{
    /// <summary>Saves with a timestamped name into <paramref name="folder"/>.</summary>
    public static SavedFile Save(BitmapSource image, string folder, ImageFormat format, AppConfig config, CaptureSidecar? sidecar)
    {
        var created = DateTime.Now;
        var path = FileSaveService.NextPath(folder, format, created);
        return SaveTo(image, path, format, config, sidecar, created);
    }

    /// <summary>Saves to an exact path (from a Save As dialog).</summary>
    public static SavedFile SaveTo(BitmapSource image, string path, ImageFormat format, AppConfig config, CaptureSidecar? sidecar, DateTime? created = null)
    {
        var when = created ?? DateTime.Now;
        if (format == ImageFormat.Vibeshot)
        {
            var assets = new AssetStore();
            var document = DocumentOps.FromImage(image, assets, CanvasColor(config));
            ProjectFile.Save(path, document, assets);
        }
        else
        {
            using var stream = File.Create(path);
            ImageEncoding.Encode(image, format, config.WebpQuality, stream);
        }
        if (sidecar != null) { sidecar.Created = when; sidecar.TryWrite(path); }
        return new SavedFile(path, image.PixelWidth, image.PixelHeight, format, when);
    }

    /// <summary>The configured canvas colour for new documents (Settings → Editor).</summary>
    public static Color CanvasColor(AppConfig config)
    {
        uint argb = config.Editor.CanvasBackgroundArgb();
        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    /// <summary>Format implied by a file name's extension; null when unknown.</summary>
    public static ImageFormat? FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png,
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.Webp,
        ".vibeshot" => ImageFormat.Vibeshot,
        _ => null,
    };
}
