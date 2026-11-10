using System.Windows.Media.Imaging;

namespace Vibershot.Core;

public static class FileSaveService
{
    // WPF ships PNG and JPEG encoders. WebP is not available in-box, so it falls
    // back to PNG (noted) until a real encoder is added in Phase 1+.
    public static string Save(BitmapSource image, string folder, ImageFormat format)
    {
        Directory.CreateDirectory(folder);

        var ext = format switch
        {
            ImageFormat.Jpeg => "jpg",
            ImageFormat.Webp => "png", // fallback; see note above
            _ => "png",
        };

        var path = Path.Combine(folder, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.{ext}");

        BitmapEncoder encoder = format switch
        {
            ImageFormat.Jpeg => new JpegBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = File.Create(path);
        encoder.Save(stream);

        return path;
    }
}
