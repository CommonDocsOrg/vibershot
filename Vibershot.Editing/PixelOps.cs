using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;


namespace Vibershot.Editing;

/// <summary>How <see cref="PixelOps.ObscurePatch"/> hides what is under it.</summary>
public enum ObscureMode { Blur, Pixelate }

/// <summary>
/// Pixel-level edits behind the marquee tool: lifting a rectangle of the picture into its own
/// movable image item, and clearing a rectangle to transparent. Only image items are touched;
/// vector items keep being vector items. Bitmaps are never modified in place: an edited image
/// gets a fresh asset so earlier undo states still render.
/// </summary>
public static class PixelOps
{
    /// <summary>Outcome of <see cref="Lift"/>: the new document and the floating item that now holds the pixels.</summary>
    public readonly record struct LiftResult(ImageDocument Document, ImageItem? Lifted);

    /// <summary>
    /// Cuts <paramref name="region"/> out of every editable image item it overlaps and places those
    /// pixels, exactly as they looked, in a new image item at the same spot on the top-most
    /// affected layer. Returns the input unchanged when no image item lies under the region.
    /// </summary>
    public static LiftResult Lift(ImageDocument doc, AssetStore assets, Rect region, Func<Layer, bool>? layerFilter = null)
    {
        region = Snap(Rect.Intersect(region, new Rect(doc.Size)));
        if (region.IsEmpty || region.Width < 1 || region.Height < 1) return new LiftResult(doc, null);
        layerFilter ??= Editable;

        var affectedLayers = new List<Layer>();
        foreach (var layer in doc.Layers)
        {
            if (!layerFilter(layer)) continue;
            var hits = layer.Items.OfType<ImageItem>().Where(item => Overlaps(item, region)).ToList();
            if (hits.Count > 0) affectedLayers.Add(layer with { Opacity = 1, Visible = true, Items = [.. hits] });
        }
        if (affectedLayers.Count == 0) return new LiftResult(doc, null);

        var copy = DocumentRenderer.Flatten(new ImageDocument { Size = doc.Size, Layers = [.. affectedLayers] }, assets, region);
        var lifted = new ImageItem { AssetId = assets.Add(copy), Bounds = region };
        var erased = Erase(doc, assets, region, layerFilter);
        int target = DocumentOps.LayerIndexOf(erased, affectedLayers[^1]);
        return new LiftResult(DocumentOps.AddItem(erased, lifted, target), lifted);
    }

    /// <summary>Clears <paramref name="region"/> to transparent in every editable image item it overlaps.</summary>
    public static ImageDocument Erase(ImageDocument doc, AssetStore assets, Rect region, Func<Layer, bool>? layerFilter = null)
    {
        region = Snap(Rect.Intersect(region, new Rect(doc.Size)));
        if (region.IsEmpty || region.Width < 1 || region.Height < 1) return doc;
        layerFilter ??= Editable;
        var layers = doc.Layers;
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (!layerFilter(layer)) continue;
            var items = layer.Items;
            for (int j = 0; j < items.Count; j++)
            {
                if (items[j] is not ImageItem image || !Overlaps(image, region)) continue;
                var bitmap = assets.Get(image.AssetId);
                if (bitmap == null) continue;
                var pixelRect = ToPixelRect(image, bitmap, region);
                if (pixelRect.Width <= 0 || pixelRect.Height <= 0) continue;
                var cleared = ClearRect(bitmap, pixelRect);
                items = items.SetItem(j, image with { AssetId = assets.Add(cleared) });
            }
            if (!ReferenceEquals(items, layer.Items)) layers = layers.SetItem(i, layer with { Items = items });
        }
        return ReferenceEquals(layers, doc.Layers) ? doc : doc with { Layers = layers };
    }

    /// <summary>Returns a copy of <paramref name="source"/> with <paramref name="rect"/> set to transparent black.</summary>
    public static BitmapSource ClearRect(BitmapSource source, Int32Rect rect)
    {
        rect = Clamp(rect, source.PixelWidth, source.PixelHeight);
        var writable = new WriteableBitmap(AssetStore.Freeze(source));
        if (rect.Width > 0 && rect.Height > 0)
        {
            int stride = rect.Width * 4;
            writable.WritePixels(rect, new byte[stride * rect.Height], stride, 0);
        }
        writable.Freeze();
        return writable;
    }

    /// <summary>
    /// Builds an image item that hides <paramref name="region"/>: the picture as it looks up to and
    /// including layer <paramref name="throughLayer"/> is flattened, blurred or pixelated, and returned
    /// as a patch to place over the same spot. The pixels in the patch really are obscured, so an
    /// export cannot be un-blurred by removing an effect. <paramref name="mask"/> (document space,
    /// filled, or stroked with <paramref name="maskPen"/>) limits the patch to a painted shape and
    /// <paramref name="feather"/> softens its edge. Returns null when the region misses the canvas.
    /// </summary>
    public static ImageItem? ObscurePatch(ImageDocument doc, AssetStore assets, Rect region, ObscureMode mode, double strength, int throughLayer,
        Geometry? mask = null, Pen? maskPen = null, double feather = 0)
    {
        var canvas = new Rect(doc.Size);
        region = Snap(Rect.Intersect(region, canvas));
        if (region.IsEmpty || region.Width < 1 || region.Height < 1) return null;
        int amount = Math.Max(1, (int)Math.Round(strength));

        // Blur needs the pixels around the region too, or its edges would fade towards the border.
        int margin = mode == ObscureMode.Blur ? amount * 2 : 0;
        var padded = Snap(Rect.Intersect(Rect.Inflate(region, margin, margin), canvas));
        var below = doc with { CropRect = null, Layers = doc.Layers.Take(Math.Clamp(throughLayer + 1, 1, doc.Layers.Count)).ToImmutableList() };
        var source = DocumentRenderer.Flatten(below, assets, padded);

        int pw = source.PixelWidth, ph = source.PixelHeight;
        var pixels = new byte[pw * ph * 4];
        source.CopyPixels(pixels, pw * 4, 0);
        if (mode == ObscureMode.Blur) BoxBlur(pixels, pw, ph, Math.Max(1, (int)Math.Round(amount * 0.6)), 4);
        else Pixelate(pixels, pw, ph, Math.Max(2, amount), (int)(region.X - padded.X), (int)(region.Y - padded.Y));

        int w = (int)region.Width, h = (int)region.Height, ox = (int)(region.X - padded.X), oy = (int)(region.Y - padded.Y);
        var patch = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(pixels, ((y + oy) * pw + ox) * 4, patch, y * w * 4, w * 4);

        if (mask != null)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
                dc.DrawGeometry(maskPen == null ? Brushes.White : null, maskPen, mask);
                dc.Pop();
            }
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var alpha = new byte[w * h * 4];
            target.CopyPixels(alpha, w * 4, 0);
            if (feather >= 1) BoxBlur(alpha, w, h, (int)Math.Round(feather), 4);
            for (int i = 0; i < patch.Length; i += 4)
            {
                int a = alpha[i + 3];
                if (a == 255) continue;
                patch[i] = (byte)(patch[i] * a / 255); patch[i + 1] = (byte)(patch[i + 1] * a / 255);
                patch[i + 2] = (byte)(patch[i + 2] * a / 255); patch[i + 3] = (byte)(patch[i + 3] * a / 255);
            }
        }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, patch, w * 4);
        bitmap.Freeze();
        return new ImageItem { AssetId = assets.Add(bitmap), Bounds = region };
    }

    /// <summary>
    /// Blurs interleaved pixels in place with three box passes each way (close to a Gaussian),
    /// clamping at the edges. Works on premultiplied colour, so transparent areas do not bleed dark.
    /// </summary>
    public static void BoxBlur(byte[] pixels, int width, int height, int radius, int channels)
    {
        if (radius < 1 || width < 1 || height < 1) return;
        var line = new byte[Math.Max(width, height) * channels];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < height; y++) BlurLine(pixels, y * width * channels, channels, width, radius, channels, line);
            for (int x = 0; x < width; x++) BlurLine(pixels, x * channels, width * channels, height, radius, channels, line);
        }
    }

    private static void BlurLine(byte[] pixels, int start, int step, int count, int radius, int channels, byte[] line)
    {
        int window = radius * 2 + 1;
        for (int c = 0; c < channels; c++)
        {
            int sum = 0;
            for (int k = -radius; k <= radius; k++) sum += pixels[start + Math.Clamp(k, 0, count - 1) * step + c];
            for (int i = 0; i < count; i++)
            {
                line[i * channels + c] = (byte)((sum + window / 2) / window);
                int leaving = Math.Max(0, i - radius), entering = Math.Min(count - 1, i + radius + 1);
                sum += pixels[start + entering * step + c] - pixels[start + leaving * step + c];
            }
        }
        for (int i = 0; i < count; i++)
            for (int c = 0; c < channels; c++) pixels[start + i * step + c] = line[i * channels + c];
    }

    /// <summary>Replaces each <paramref name="block"/>-sized square (aligned to the given origin) with its average colour.</summary>
    public static void Pixelate(byte[] pixels, int width, int height, int block, int originX = 0, int originY = 0)
    {
        if (block < 2) return;
        int firstX = originX % block - block, firstY = originY % block - block;
        Span<long> sum = stackalloc long[4];
        for (int by = firstY; by < height; by += block)
        for (int bx = firstX; bx < width; bx += block)
        {
            int x0 = Math.Max(0, bx), y0 = Math.Max(0, by), x1 = Math.Min(width, bx + block), y1 = Math.Min(height, by + block);
            if (x1 <= x0 || y1 <= y0) continue;
            sum.Clear();
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * width + x) * 4;
                sum[0] += pixels[i]; sum[1] += pixels[i + 1]; sum[2] += pixels[i + 2]; sum[3] += pixels[i + 3];
            }
            int n = (x1 - x0) * (y1 - y0);
            byte b = (byte)(sum[0] / n), g = (byte)(sum[1] / n), r = (byte)(sum[2] / n), a = (byte)(sum[3] / n);
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = a;
            }
        }
    }

    /// <summary>True for layers the marquee may edit: visible and unlocked.</summary>
    public static bool Editable(Layer layer) => layer is { Visible: true, Locked: false };

    /// <summary>Unrotated image items whose bounds meet the region. Rotated images are left alone: their pixels do not map to an axis-aligned rectangle.</summary>
    private static bool Overlaps(ImageItem item, Rect region) =>
        item.Rotation == 0 && item.Bounds.Width > 0 && item.Bounds.Height > 0 && item.Bounds.IntersectsWith(region) && !Rect.Intersect(item.Bounds, region).IsEmpty;

    /// <summary>Maps a document-space rectangle into the bitmap's pixel grid, allowing for the item being scaled.</summary>
    private static Int32Rect ToPixelRect(ImageItem item, BitmapSource bitmap, Rect region)
    {
        var overlap = Rect.Intersect(item.Bounds, region);
        double sx = bitmap.PixelWidth / item.Bounds.Width, sy = bitmap.PixelHeight / item.Bounds.Height;
        int x0 = (int)Math.Floor((overlap.X - item.Bounds.X) * sx + 1e-6);
        int y0 = (int)Math.Floor((overlap.Y - item.Bounds.Y) * sy + 1e-6);
        int x1 = (int)Math.Ceiling((overlap.Right - item.Bounds.X) * sx - 1e-6);
        int y1 = (int)Math.Ceiling((overlap.Bottom - item.Bounds.Y) * sy - 1e-6);
        return Clamp(new Int32Rect(x0, y0, x1 - x0, y1 - y0), bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static Int32Rect Clamp(Int32Rect r, int width, int height)
    {
        int x0 = Math.Clamp(r.X, 0, width), y0 = Math.Clamp(r.Y, 0, height);
        int x1 = Math.Clamp(r.X + r.Width, 0, width), y1 = Math.Clamp(r.Y + r.Height, 0, height);
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private static Rect Snap(Rect r) => r.IsEmpty ? r : new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));
}
