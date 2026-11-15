using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vibershot.Editing.Video;

/// <summary>
/// The shape a visual clip is cut to. The preview clips to <see cref="Outline"/>; the export
/// draws the same outline into a grey picture that ffmpeg merges in as the clip's transparency,
/// so both show the same edge.
/// </summary>
public static class FrameMask
{
    /// <summary>The outline inside <paramref name="box"/>: the whole box when there is no frame.</summary>
    public static Geometry Outline(ClipFrame? frame, Rect box)
    {
        switch (frame?.Shape)
        {
            case FrameShape.Ellipse:
                return new EllipseGeometry(box);
            case FrameShape.Rounded:
                double radius = Math.Clamp(frame.Radius, 0, 0.5) * Math.Min(box.Width, box.Height);
                return new RectangleGeometry(box, radius, radius);
            default:
                return new RectangleGeometry(box);
        }
    }

    /// <summary>
    /// Writes the mask for a clip drawn at the given pixel size (white shows, black hides) and
    /// returns its path; null when the clip has no frame and needs no mask. Must run on a thread
    /// that can use WPF.
    /// </summary>
    public static string? Export(ClipFrame? frame, int width, int height, string folder)
    {
        if (frame == null || frame.Shape == FrameShape.Rectangle) return null;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"mask_{frame.Shape}_{frame.Radius:0.###}_{width}x{height}.png").ToLowerInvariant());
        if (File.Exists(path)) return path;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var box = new Rect(0, 0, width, height);
            dc.DrawRectangle(Brushes.Black, null, box);
            dc.DrawGeometry(Brushes.White, null, Outline(frame, box));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
