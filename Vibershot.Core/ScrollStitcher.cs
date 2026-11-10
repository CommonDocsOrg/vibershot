using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vibershot.Core;

public static class ScrollStitcher
{
    // Find the vertical overlap between consecutive viewport images. Reject uncertain matches.
    public static BitmapSource Append(BitmapSource stitched, BitmapSource previous, BitmapSource next)
    {
        if (previous.PixelWidth != next.PixelWidth || previous.PixelHeight != next.PixelHeight) throw new InvalidOperationException("Capture dimensions changed.");
        var a = new FormatConvertedBitmap(previous, PixelFormats.Bgra32, null, 0);
        var b = new FormatConvertedBitmap(next, PixelFormats.Bgra32, null, 0);
        int width = a.PixelWidth, height = a.PixelHeight, stride = width * 4;
        byte[] pa = new byte[stride * height], pb = new byte[stride * height];
        a.CopyPixels(pa, stride, 0); b.CopyPixels(pb, stride, 0);
        double best = double.MaxValue; int shift = 0;
        for (int offset = 0; offset <= height * 4 / 5; offset++)
        {
            long error = 0; int count = 0;
            for (int y = 0; y < height - offset; y += Math.Max(1, height / 70))
                for (int x = width / 10; x < width * 9 / 10; x += Math.Max(1, width / 80))
                    for (int c = 0; c < 3; c++) { error += Math.Abs(pa[(y + offset) * stride + x * 4 + c] - pb[y * stride + x * 4 + c]); count++; }
            double score = (double)error / Math.Max(1, count);
            if (score < best) { best = score; shift = offset; }
        }
        if (shift < 2) throw new InvalidOperationException("No new content found. Scroll down before adding another shot.");
        if (best > 12) throw new InvalidOperationException("Could not align these shots. Scroll less (keep at least 20% overlap), and exclude sticky headers or moving content.");
        int total = checked(stitched.PixelHeight + shift);
        if ((long)width * total > 80_000_000) throw new InvalidOperationException("Image reached the 80 megapixel limit. Finish this image and start another.");
        var result = new WriteableBitmap(width, total, 96, 96, PixelFormats.Bgra32, null);
        var existing = new FormatConvertedBitmap(stitched, PixelFormats.Bgra32, null, 0);
        byte[] top = new byte[stride * existing.PixelHeight]; existing.CopyPixels(top, stride, 0);
        result.WritePixels(new Int32Rect(0, 0, width, existing.PixelHeight), top, stride, 0);
        result.WritePixels(new Int32Rect(0, existing.PixelHeight, width, shift), pb, stride, (height - shift) * stride);
        result.Freeze(); return result;
    }
}
