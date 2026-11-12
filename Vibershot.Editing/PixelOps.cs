using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vibershot.Editing;

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
