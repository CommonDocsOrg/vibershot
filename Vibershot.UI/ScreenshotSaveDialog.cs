using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vibershot.Core;

namespace Vibershot.UI;

/// <summary>The Save As dialog for a capture: every flat format plus the editable project.</summary>
public static class ScreenshotSaveDialog
{
    // Filter order matches ImageFormat enum order so FilterIndex maps directly (1-based).
    public const string Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image (lossy)|*.webp|WebP image (lossless)|*.webp|Vibershot project (editable)|*.vibeshot";

    public static int FilterIndexFor(ImageFormat format) => (int)format + 1;

    public static ImageFormat FormatFor(int filterIndex) =>
        Enum.IsDefined(typeof(ImageFormat), filterIndex - 1) ? (ImageFormat)(filterIndex - 1) : ImageFormat.Png;

    /// <param name="sidecar">Gallery metadata written next to the file when provided.</param>
    public static string? Save(BitmapSource image, AppConfig config, Vibershot.Gallery.CaptureSidecar? sidecar = null, Window? owner = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = Filter, FilterIndex = FilterIndexFor(config.Format),
            InitialDirectory = Directory.Exists(config.OutputFolder) ? config.OutputFolder : "",
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}", AddExtension = true
        };
        bool? result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (result != true) return null;
        // A typed extension wins over the filter so "shot.png" under the WebP filter still writes PNG.
        var format = CaptureSaver.FormatFromExtension(dialog.FileName) ?? FormatFor(dialog.FilterIndex);
        if (format == ImageFormat.Webp && FormatFor(dialog.FilterIndex) == ImageFormat.WebpLossless) format = ImageFormat.WebpLossless;
        CaptureSaver.SaveTo(image, dialog.FileName, format, config, sidecar);
        return dialog.FileName;
    }
}
