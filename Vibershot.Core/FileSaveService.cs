using System.Windows.Media.Imaging;

namespace Vibershot.Core;

/// <summary>What <see cref="FileSaveService.SaveFile"/> wrote; enough for a gallery sidecar.</summary>
public sealed record SavedFile(string Path, int Width, int Height, ImageFormat Format, DateTime Created);

public static class FileSaveService
{
    public static string Save(BitmapSource image, string folder, ImageFormat format, int webpQuality = 80) =>
        SaveFile(image, folder, format, webpQuality).Path;

    /// <summary>A timestamped, not-yet-existing path in <paramref name="folder"/> for the format; creates the folder.</summary>
    public static string NextPath(string folder, ImageFormat format, DateTime created, string stem = "screenshot")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{stem}_{created:yyyyMMdd_HHmmss_fffffff}.{ImageEncoding.Extension(format)}");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{stem}_{created:yyyyMMdd_HHmmss_fffffff} ({i}).{ImageEncoding.Extension(format)}");
        return path;
    }

    /// <summary>Writes a flat image (PNG, JPEG or WebP). Project files are written by the UI through the editing library.</summary>
    public static SavedFile SaveFile(BitmapSource image, string folder, ImageFormat format, int webpQuality = 80)
    {
        if (!ImageEncoding.IsFlat(format)) throw new NotSupportedException("Use the project writer for .vibeshot files.");
        var created = DateTime.Now;
        var path = NextPath(folder, format, created);

        using var stream = File.Create(path);
        ImageEncoding.Encode(image, format, webpQuality, stream);

        return new SavedFile(path, image.PixelWidth, image.PixelHeight, format, created);
    }
}
