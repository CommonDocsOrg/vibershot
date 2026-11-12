using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vibershot.Core;

namespace Vibershot.UI;

public static class ScreenshotSaveDialog
{
    // Filter order matches ImageFormat enum order so FilterIndex maps directly (1-based).
    public const string Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image (lossy)|*.webp|WebP image (lossless)|*.webp";

    public static int FilterIndexFor(ImageFormat format) => (int)format + 1;

    public static ImageFormat FormatFor(int filterIndex) =>
        Enum.IsDefined(typeof(ImageFormat), filterIndex - 1) ? (ImageFormat)(filterIndex - 1) : ImageFormat.Png;

    public static string? Save(BitmapSource image, AppConfig config)
    {
        var dialog = new SaveFileDialog
        {
            Filter = Filter, FilterIndex = FilterIndexFor(config.Format),
            InitialDirectory = Directory.Exists(config.OutputFolder) ? config.OutputFolder : "",
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}", AddExtension = true
        };
        if (dialog.ShowDialog() != true) return null;
        using var stream = File.Create(dialog.FileName);
        ImageEncoding.Encode(image, FormatFor(dialog.FilterIndex), config.WebpQuality, stream);
        return dialog.FileName;
    }
}
