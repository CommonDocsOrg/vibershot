using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Media;

namespace Vibershot.Editing.Video;

/// <summary>
/// Turns graphics and captions into pictures ffmpeg can lay over the video. They are drawn by the
/// same <see cref="DocumentRenderer"/> the preview uses, so the export matches it pixel for pixel.
/// A graphic becomes one transparent PNG shown for its clip; a caption track becomes one stream of
/// same-sized PNGs (blank between captions) read through ffmpeg's concat demuxer, so a film with
/// hundreds of captions is still a single input. Must run on a thread that can use WPF.
/// </summary>
public static class GraphicExport
{
    private static readonly AssetStore NoAssets = new();

    /// <summary>
    /// The whole-pixel part of the canvas an item can paint on: its turned box, its pointers, its
    /// line width and shadow, and the outline and shadow of its letters. Empty when it lies entirely off the canvas.
    /// </summary>
    public static Rect Extent(Item item, int canvasWidth, int canvasHeight)
    {
        var bounds = item.Bounds;
        if (item is TextItem text) bounds.Union(new Rect(bounds.TopLeft, new Size(bounds.Width, Math.Max(bounds.Height, DocumentRenderer.MeasureText(text).Height))));
        var rotate = new RotateTransform(item.Rotation, item.Center.X, item.Center.Y);
        var extent = Rect.Empty;
        foreach (var corner in new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft }) extent.Union(rotate.Transform(corner));
        if (item is CalloutItem callout) foreach (var target in callout.Targets) extent.Union(target);
        double head = item switch { ArrowItem arrow => arrow.HeadSize, CalloutItem pointer => pointer.HeadSize, _ => 0 };
        double margin = item.StrokeWidth + head + 12;
        // The letters' outline and shadow reach past the box when the padding is small.
        if (item is TextItem words) margin += DocumentRenderer.TextOverhang(words);
        extent.Inflate(margin, margin);
        extent.Intersect(new Rect(0, 0, canvasWidth, canvasHeight));
        if (extent.IsEmpty) return Rect.Empty;
        double left = Math.Floor(extent.Left), top = Math.Floor(extent.Top);
        var whole = new Rect(left, top, Math.Ceiling(extent.Right) - left, Math.Ceiling(extent.Bottom) - top);
        return whole.Width < 1 || whole.Height < 1 ? Rect.Empty : whole;
    }

    /// <summary>Draws items over transparency: the given canvas region, scaled, as a bitmap of exactly the given pixel size.</summary>
    public static BitmapSource Render(IEnumerable<Item> items, Rect region, int pixelWidth, int pixelHeight)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixelWidth / region.Width, pixelHeight / region.Height));
            dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
            foreach (var item in items) DocumentRenderer.RenderItem(dc, item, NoAssets);
            dc.Pop();
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, pixelWidth), Math.Max(1, pixelHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>One graphic clip as a still picture on the export timeline, or null when nothing of it is on the canvas.</summary>
    public static ExportVideoClip? Graphic(VideoProject project, GraphicClip clip, double scaleX, double scaleY, string folder)
    {
        var region = Extent(clip.Item, project.Width, project.Height);
        if (region.IsEmpty || clip.Item.Opacity <= 0) return null;
        int width = Math.Max(2, (int)Math.Round(region.Width * scaleX)), height = Math.Max(2, (int)Math.Round(region.Height * scaleY));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"g_{Hash(DocumentJson.Serialize(clip.Item) + region + width + "x" + height)}.png");
        if (!File.Exists(path)) SavePng(Render(new[] { clip.Item }, region, width, height), path);
        return new ExportVideoClip(path, 0, project.Seconds(clip.Duration), project.Seconds(clip.Start), region.X * scaleX, region.Y * scaleY, width, height) { IsImage = true };
    }

    /// <summary>
    /// A caption track as one overlay stream: every caption drawn into a band of the canvas just
    /// big enough for the largest, with a blank picture wherever no caption shows. Null when the
    /// track has no captions.
    /// </summary>
    public static ExportOverlayStream? Captions(VideoProject project, Track track, double scaleX, double scaleY, string folder)
    {
        var captions = track.Clips.OfType<CaptionClip>().Where(clip => !string.IsNullOrWhiteSpace(clip.Text)).OrderBy(clip => clip.Start).ToList();
        if (captions.Count == 0) return null;
        var items = captions.Select(clip => TimelineOps.CaptionItem(project, track, clip)).ToList();
        var band = Rect.Empty;
        foreach (var item in items) band.Union(Extent(item, project.Width, project.Height));
        if (band.IsEmpty) return null;
        int width = Math.Max(2, (int)Math.Round(band.Width * scaleX)), height = Math.Max(2, (int)Math.Round(band.Height * scaleY));
        Directory.CreateDirectory(folder);
        string key = Hash(track.Id + "|" + band + "|" + width + "x" + height + "|" + DocumentJson.Serialize(TimelineOps.CaptionStyleOf(project, track)));
        string blank = $"c_{key}_blank.png";
        if (!File.Exists(Path.Combine(folder, blank))) SavePng(Render(Array.Empty<Item>(), band, width, height), Path.Combine(folder, blank));

        var list = new StringBuilder("ffconcat version 1.0\n");
        string last = blank;
        // Each picture is read at the project's frame rate. Left alone, ffmpeg reads stills at 25
        // per second and rounds every caption's start to a 25th, which lands a frame late at 30 or 60.
        string picture = $"option framerate {project.Rate.ToFfmpegString()}\n";
        void Entry(string file, long frames)
        {
            if (frames <= 0) return;
            list.Append(CultureInfo.InvariantCulture, $"file '{file}'\n{picture}duration {project.Seconds(frames).ToString("0.000000", CultureInfo.InvariantCulture)}\n");
            last = file;
        }
        long at = 0;
        for (int i = 0; i < captions.Count; i++)
        {
            var clip = captions[i];
            Entry(blank, clip.Start - at);
            string file = $"c_{key}_{Hash(clip.Text)}.png";
            if (!File.Exists(Path.Combine(folder, file))) SavePng(Render(new[] { items[i] }, band, width, height), Path.Combine(folder, file));
            Entry(file, clip.Duration);
            at = clip.End;
        }
        // The film runs on after the last caption with nothing showing. The demuxer ignores the
        // last entry's duration, so that entry is named once more.
        Entry(blank, Math.Max(1, project.Duration - at));
        list.Append(CultureInfo.InvariantCulture, $"file '{last}'\n{picture}");
        string listPath = Path.Combine(folder, $"c_{key}.ffconcat");
        File.WriteAllText(listPath, list.ToString(), new UTF8Encoding(false));
        return new ExportOverlayStream(listPath, band.X * scaleX, band.Y * scaleY);
    }
}
