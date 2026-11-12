using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Vibershot.Gallery;

/// <summary>
/// Produces a frozen preview for a video file. The MP4 work can plug an ffmpeg-based
/// implementation in here; <see cref="FilmTileThumbnailer"/> is the dependency-free default.
/// </summary>
public interface IVideoThumbnailer
{
    /// <summary>Returns a frozen bitmap no larger than <paramref name="longestEdge"/>, or null when the file cannot be read.</summary>
    BitmapSource? CreateThumbnail(string path, int longestEdge);
}

/// <summary>Default video preview: a neutral film tile carrying the file name. Never touches the video.</summary>
public sealed class FilmTileThumbnailer : IVideoThumbnailer
{
    public BitmapSource? CreateThumbnail(string path, int longestEdge) => TileDrawing.FilmTile(Path.GetFileName(path), longestEdge);
}

/// <summary>Draws placeholder tiles (film strip, project) with WPF visuals; safe on worker threads.</summary>
public static class TileDrawing
{
    private static readonly Color Slate = Color.FromRgb(0x2A, 0x2E, 0x34);
    private static readonly Color Paper = Color.FromRgb(0xFB, 0xFB, 0xFC);
    private static readonly Color Ink3 = Color.FromRgb(0x9A, 0xA2, 0xAB);
    private static readonly Color Well = Color.FromRgb(0xEE, 0xF0, 0xF3);

    public static BitmapSource FilmTile(string label, int longestEdge)
    {
        int w = longestEdge, h = (int)(longestEdge * 9 / 16.0);
        return Render(w, h, dc =>
        {
            dc.DrawRectangle(Freeze(new SolidColorBrush(Slate)), null, new Rect(0, 0, w, h));
            var hole = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x63, 0x6D)));
            for (double x = 10; x + 14 <= w - 10; x += 26)
            {
                dc.DrawRoundedRectangle(hole, null, new Rect(x, 8, 14, 9), 2, 2);
                dc.DrawRoundedRectangle(hole, null, new Rect(x, h - 17, 14, 9), 2, 2);
            }
            DrawLabel(dc, label, w, h, Paper, 13);
        });
    }

    public static BitmapSource ProjectTile(string label, int longestEdge)
    {
        int w = longestEdge, h = (int)(longestEdge * 9 / 16.0);
        return Render(w, h, dc =>
        {
            dc.DrawRectangle(Freeze(new SolidColorBrush(Well)), null, new Rect(0, 0, w, h));
            var edge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xD3))), 1));
            dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Paper)), edge, new Rect(w * 0.3, h * 0.12, w * 0.4, h * 0.46), 6, 6);
            DrawLabel(dc, label, w, h, Ink3, 12, h * 0.72);
        });
    }

    private static void DrawLabel(DrawingContext dc, string label, int w, int h, Color color, double size, double? top = null)
    {
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Cascadia Mono, Consolas"), size, Freeze(new SolidColorBrush(color)), 1.0)
        {
            MaxTextWidth = Math.Max(10, w - 32), MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(text, new Point(16, top ?? (h - text.Height) / 2));
    }

    private static BitmapSource Render(int w, int h, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}

/// <summary>
/// Disk cache of 320 px JPEG previews under %LOCALAPPDATA%\Vibershot\Thumbs, keyed by path,
/// size and modification time. Generation runs on the thread pool with limited concurrency;
/// results are posted back as frozen bitmaps.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    public const int LongestEdge = 320;

    private readonly string _folder;
    private readonly Action<Action> _post;
    private readonly IVideoThumbnailer _video;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    public ThumbnailCache(string folder, Action<Action>? post = null, IVideoThumbnailer? video = null, int concurrency = 2)
    {
        _folder = folder;
        _post = post ?? (action => action());
        _video = video ?? new FilmTileThumbnailer();
        _gate = new SemaphoreSlim(Math.Max(1, concurrency));
    }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Thumbs");

    /// <summary>SHA-1 of path, size and modification ticks: stable across runs, different after any edit.</summary>
    public static string KeyFor(string path, long size, DateTime modified)
    {
        var bytes = Encoding.UTF8.GetBytes($"{path.ToLowerInvariant()}|{size}|{modified.ToUniversalTime().Ticks}");
        return Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    }

    public string CachePathFor(GalleryItem item) => Path.Combine(_folder, KeyFor(item.Path, item.Size, item.Modified) + ".jpg");

    /// <summary>Queues generation unless the item already has a thumbnail or one is on its way.</summary>
    public void Request(GalleryItem item)
    {
        if (_disposed || item.Thumbnail is not null || !_pending.TryAdd(item.Path, 0)) return;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                var (bitmap, size) = Generate(item);
                if (bitmap is null && size is null) return;
                _post(() =>
                {
                    if (bitmap is not null) item.Thumbnail = bitmap;
                    if (size is var (w, h)) item.SetProbedSize(w, h);
                });
            }
            catch { /* Unreadable file: the tile keeps its neutral fill. */ }
            finally { _gate.Release(); _pending.TryRemove(item.Path, out _); }
        });
    }

    /// <summary>Forgets the in-memory thumbnail so the next request regenerates (after a rename or edit).</summary>
    public void Invalidate(GalleryItem item) => item.Thumbnail = null;

    /// <summary>Removes the cached file for an item that was deleted.</summary>
    public void Delete(GalleryItem item)
    {
        try { File.Delete(CachePathFor(item)); } catch { }
    }

    private (BitmapSource? Bitmap, (int, int)? Size) Generate(GalleryItem item)
    {
        var cachePath = CachePathFor(item);
        (int, int)? size = null;
        if (item.Width == 0 && item.Kind == GalleryItemKind.Image) size = ProbeSize(item.Path);
        if (File.Exists(cachePath))
        {
            var cached = LoadFrozen(cachePath, 0);
            if (cached is not null) return (cached, size);
        }
        BitmapSource? bitmap;
        bool cache = true;
        switch (item.Kind)
        {
            case GalleryItemKind.Image:
                bitmap = LoadImage(item.Path, LongestEdge);
                if (bitmap is not null) bitmap = OnWhite(bitmap);
                break;
            case GalleryItemKind.Video:
                bitmap = _video.CreateThumbnail(item.Path, LongestEdge);
                if (bitmap is null) { bitmap = TileDrawing.FilmTile(item.Name, LongestEdge); cache = false; }
                break;
            default:
                bitmap = LoadProjectPreview(item.Path, LongestEdge);
                if (bitmap is not null) bitmap = OnWhite(bitmap);
                else { bitmap = TileDrawing.ProjectTile(item.Name, LongestEdge); cache = false; }
                break;
        }
        if (bitmap is null) return (null, size);
        if (cache) TrySaveJpeg(bitmap, cachePath);
        return (bitmap, size);
    }

    /// <summary>Reads the pixel size from the image header without decoding the pixels.</summary>
    public static (int, int)? ProbeSize(string path)
    {
        try
        {
            if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                using var codec = SKCodec.Create(path);
                return codec is null ? null : (codec.Info.Width, codec.Info.Height);
            }
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return null; }
    }

    /// <summary>
    /// Decodes an image scaled so its longest edge is at most <paramref name="longestEdge"/>
    /// (0 = full size). Large stitched captures are decoded at reduced size by the codec
    /// rather than loaded fully and shrunk.
    /// </summary>
    public static BitmapSource? LoadImage(string path, int longestEdge)
    {
        try
        {
            return path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? LoadWebp(path, longestEdge) : LoadFrozen(path, longestEdge);
        }
        catch { return null; }
    }

    private static BitmapSource? LoadFrozen(string path, int longestEdge)
    {
        using var stream = File.OpenRead(path);
        int decodeWidth = 0, decodeHeight = 0;
        if (longestEdge > 0)
        {
            var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            int w = header.PixelWidth, h = header.PixelHeight;
            if (w > longestEdge || h > longestEdge)
            {
                if (w >= h) decodeWidth = longestEdge; else decodeHeight = longestEdge;
            }
            stream.Position = 0;
        }
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        if (decodeHeight > 0) image.DecodePixelHeight = decodeHeight;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// The flattened <c>preview.png</c> a <c>.vibeshot</c> project carries, scaled to the tile size.
    /// Projects saved before previews existed return null and get the neutral tile.
    /// </summary>
    public static BitmapSource? LoadProjectPreview(string path, int longestEdge)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
            var entry = zip.GetEntry("preview.png");
            if (entry is null) return null;
            using var png = entry.Open();
            using var buffer = new MemoryStream();
            png.CopyTo(buffer);
            buffer.Position = 0;
            var header = BitmapDecoder.Create(buffer, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            int w = header.PixelWidth, h = header.PixelHeight;
            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = buffer;
            if (longestEdge > 0 && (w > longestEdge || h > longestEdge))
            {
                if (w >= h) image.DecodePixelWidth = longestEdge; else image.DecodePixelHeight = longestEdge;
            }
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static BitmapSource? LoadWebp(string path, int longestEdge)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        var info = codec.Info;
        float scale = longestEdge > 0 ? Math.Min(1f, longestEdge / (float)Math.Max(info.Width, info.Height)) : 1f;
        var dims = codec.GetScaledDimensions(scale);
        using var decoded = new SKBitmap(new SKImageInfo(dims.Width, dims.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        SKBitmap final = decoded;
        int tw = (int)Math.Round(info.Width * scale), th = (int)Math.Round(info.Height * scale);
        if (longestEdge > 0 && (decoded.Width > tw || decoded.Height > th))
            final = decoded.Resize(new SKImageInfo(Math.Max(1, tw), Math.Max(1, th), SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? decoded;
        try
        {
            var result = BitmapSource.Create(final.Width, final.Height, 96, 96, PixelFormats.Pbgra32, null, final.Bytes, final.RowBytes);
            result.Freeze();
            return result;
        }
        finally { if (!ReferenceEquals(final, decoded)) final.Dispose(); }
    }

    /// <summary>Composites transparency onto white so the JPEG cache does not turn it black.</summary>
    private static BitmapSource OnWhite(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgr24 || source.Format == PixelFormats.Rgb24 || source.Format == PixelFormats.Gray8) return source;
        var converted = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        converted.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int inverse = 255 - pixels[i + 3];
            if (inverse == 0) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] + inverse);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + inverse);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + inverse);
            pixels[i + 3] = 255;
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private void TrySaveJpeg(BitmapSource bitmap, string cachePath)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var temp = cachePath + "." + Environment.CurrentManagedThreadId + ".tmp";
            using (var stream = File.Create(temp))
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }
            File.Move(temp, cachePath, overwrite: true);
        }
        catch { /* A cache miss next time is the only consequence. */ }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
