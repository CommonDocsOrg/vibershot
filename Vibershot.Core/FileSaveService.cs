using System.Windows.Media.Imaging;

namespace Vibershot.Core;

/// <summary>What <see cref="FileSaveService.SaveFile"/> wrote; enough for a gallery sidecar.</summary>
public sealed record SavedFile(string Path, int Width, int Height, ImageFormat Format, DateTime Created);

public static class FileSaveService
{
    public static string Save(BitmapSource image, string folder, ImageFormat format, int webpQuality = 80) =>
        SaveFile(image, folder, format, webpQuality).Path;

    public static SavedFile SaveFile(BitmapSource image, string folder, ImageFormat format, int webpQuality = 80)
    {
        Directory.CreateDirectory(folder);

        var created = DateTime.Now;
        var path = Path.Combine(folder, $"screenshot_{created:yyyyMMdd_HHmmss_fffffff}.{ImageEncoding.Extension(format)}");

        using var stream = File.Create(path);
        ImageEncoding.Encode(image, format, webpQuality, stream);

        return new SavedFile(path, image.PixelWidth, image.PixelHeight, format, created);
    }
}
