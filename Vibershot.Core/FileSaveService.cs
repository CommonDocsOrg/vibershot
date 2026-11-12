using System.Windows.Media.Imaging;

namespace Vibershot.Core;

public static class FileSaveService
{
    public static string Save(BitmapSource image, string folder, ImageFormat format, int webpQuality = 80)
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.{ImageEncoding.Extension(format)}");

        using var stream = File.Create(path);
        ImageEncoding.Encode(image, format, webpQuality, stream);

        return path;
    }
}
