using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Vibershot.Core;

/// <summary>
/// Encodes a captured bitmap into a file format. PNG and JPEG use the WPF
/// encoders; WebP uses SkiaSharp because WPF has no in-box WebP encoder.
/// </summary>
public static class ImageEncoding
{
    public const int MinWebpQuality = 1;
    public const int MaxWebpQuality = 100;

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "jpg",
        ImageFormat.Webp or ImageFormat.WebpLossless => "webp",
        ImageFormat.Vibeshot => "vibeshot",
        _ => "png",
    };

    /// <summary>Formats this class can write; the project format is written by the editing library.</summary>
    public static bool IsFlat(ImageFormat format) => format != ImageFormat.Vibeshot;

    public static string Describe(ImageFormat format) => format switch
    {
        ImageFormat.Png => "PNG · lossless",
        ImageFormat.Jpeg => "JPEG · smaller files",
        ImageFormat.Webp => "WebP · custom quality",
        ImageFormat.WebpLossless => "WebP lossless · exact pixels, smaller than PNG",
        ImageFormat.Vibeshot => "Vibershot project · full quality, stays editable",
        _ => format.ToString(),
    };

    public static int ClampWebpQuality(int quality) => Math.Clamp(quality, MinWebpQuality, MaxWebpQuality);

    /// <summary>Writes <paramref name="image"/> to <paramref name="stream"/> in the given format.</summary>
    /// <param name="webpQuality">Lossy WebP quality (1-100); ignored for other formats.</param>
    public static void Encode(BitmapSource image, ImageFormat format, int webpQuality, Stream stream)
    {
        switch (format)
        {
            case ImageFormat.Vibeshot:
                throw new NotSupportedException("Vibershot projects are written with ProjectFile, not the image encoder.");
            case ImageFormat.Webp:
                EncodeWebp(image, stream, lossless: false, ClampWebpQuality(webpQuality));
                break;
            case ImageFormat.WebpLossless:
                EncodeWebp(image, stream, lossless: true, MaxWebpQuality);
                break;
            default:
                BitmapEncoder encoder = format == ImageFormat.Jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
                break;
        }
    }

    private static void EncodeWebp(BitmapSource image, Stream stream, bool lossless, int quality)
    {
        // Skia wants premultiplied BGRA; WPF's Pbgra32 matches that layout exactly.
        var source = image.Format == PixelFormats.Pbgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var options = new SKWebpEncoderOptions(
            lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy,
            quality);

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), stride);
            using var skStream = new SKManagedWStream(stream, disposeManagedStream: false);
            if (!pixmap.Encode(skStream, options))
            {
                throw new InvalidOperationException("WebP encoding failed.");
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
