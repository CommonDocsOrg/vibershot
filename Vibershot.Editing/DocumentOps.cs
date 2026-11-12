using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vibershot.Editing;

public enum ZOrderMove { BringToFront, BringForward, SendBackward, SendToBack }

/// <summary>Pure operations: every method returns a new document and leaves its input untouched.</summary>
public static class DocumentOps
{
    public const string BaseLayerName = "Background";
    public const string AnnotationLayerName = "Annotations";

    public static ImageDocument Blank(Size size, Color background) => new()
    {
        Size = size,
        Background = background,
        Layers = [new Layer { Name = BaseLayerName }, new Layer { Name = AnnotationLayerName }],
    };

    /// <summary>A document whose base layer holds <paramref name="image"/> at 1:1 pixels.</summary>
    public static ImageDocument FromImage(BitmapSource image, AssetStore assets)
    {
        var assetId = assets.Add(image);
        var baseItem = new ImageItem { AssetId = assetId, Bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight) };
        return new ImageDocument
        {
            Size = new Size(image.PixelWidth, image.PixelHeight),
            Layers =
            [
                new Layer { Name = BaseLayerName, Items = [baseItem] },
                new Layer { Name = AnnotationLayerName },
            ],
        };
    }

    /// <summary>The top-most visible, unlocked layer: where new items land. -1 when there is none.</summary>
    public static int ActiveLayerIndex(ImageDocument doc)
    {
        for (int i = doc.Layers.Count - 1; i >= 0; i--)
            if (doc.Layers[i] is { Visible: true, Locked: false }) return i;
        return doc.Layers.Count - 1;
    }

    public static Item? FindItem(ImageDocument doc, Guid id) => doc.AllItems.FirstOrDefault(item => item.Id == id);

    public static int LayerIndexOf(ImageDocument doc, Guid id)
    {
        for (int i = 0; i < doc.Layers.Count; i++)
            if (doc.Layers[i].Items.Any(item => item.Id == id)) return i;
        return -1;
    }

    public static ImageDocument SetLayer(ImageDocument doc, int index, Layer layer) =>
        doc with { Layers = doc.Layers.SetItem(index, layer) };

    public static ImageDocument AddLayer(ImageDocument doc, Layer layer) => doc with { Layers = doc.Layers.Add(layer) };

    public static ImageDocument AddItem(ImageDocument doc, Item item, int? layerIndex = null) => AddItems(doc, [item], layerIndex);

    public static ImageDocument AddItems(ImageDocument doc, IEnumerable<Item> items, int? layerIndex = null)
    {
        if (doc.Layers.Count == 0) doc = AddLayer(doc, new Layer { Name = AnnotationLayerName });
        int index = layerIndex ?? ActiveLayerIndex(doc);
        index = Math.Clamp(index, 0, doc.Layers.Count - 1);
        var layer = doc.Layers[index];
        return SetLayer(doc, index, layer with { Items = layer.Items.AddRange(items) });
    }

    public static ImageDocument RemoveItems(ImageDocument doc, IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0) return doc;
        return doc with { Layers = doc.Layers.ConvertAll(layer => layer with { Items = layer.Items.RemoveAll(item => set.Contains(item.Id)) }) };
    }

    /// <summary>Applies <paramref name="update"/> to the items with the given ids wherever they live.</summary>
    public static ImageDocument UpdateItems(ImageDocument doc, IEnumerable<Guid> ids, Func<Item, Item> update)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0) return doc;
        return doc with
        {
            Layers = doc.Layers.ConvertAll(layer => layer with
            {
                Items = layer.Items.ConvertAll(item => set.Contains(item.Id) ? update(item) with { Id = item.Id } : item),
            }),
        };
    }

    public static ImageDocument ReplaceItem(ImageDocument doc, Item item) => UpdateItems(doc, [item.Id], _ => item);

    public static ImageDocument MoveItems(ImageDocument doc, IEnumerable<Guid> ids, Vector delta) =>
        UpdateItems(doc, ids, item => item with { Bounds = Rect.Offset(item.Bounds, delta) });

    public static ImageDocument SetBounds(ImageDocument doc, Guid id, Rect bounds) =>
        UpdateItems(doc, [id], item => item with { Bounds = bounds });

    public static ImageDocument SetRotation(ImageDocument doc, Guid id, double degrees) =>
        UpdateItems(doc, [id], item => item with { Rotation = degrees });

    /// <summary>Moves an item within its layer. Index 0 is the bottom of the layer.</summary>
    public static ImageDocument Reorder(ImageDocument doc, Guid id, ZOrderMove move)
    {
        int layerIndex = LayerIndexOf(doc, id);
        if (layerIndex < 0) return doc;
        var layer = doc.Layers[layerIndex];
        int from = layer.Items.FindIndex(item => item.Id == id);
        int to = move switch
        {
            ZOrderMove.BringToFront => layer.Items.Count - 1,
            ZOrderMove.BringForward => Math.Min(layer.Items.Count - 1, from + 1),
            ZOrderMove.SendBackward => Math.Max(0, from - 1),
            _ => 0,
        };
        if (to == from) return doc;
        var item = layer.Items[from];
        var items = layer.Items.RemoveAt(from).Insert(to, item);
        return SetLayer(doc, layerIndex, layer with { Items = items });
    }

    /// <summary>Sets the crop, clamped to the canvas. A crop covering the whole canvas or an empty one clears it.</summary>
    public static ImageDocument SetCrop(ImageDocument doc, Rect? crop)
    {
        if (crop is not { } rect) return doc with { CropRect = null };
        var full = new Rect(doc.Size);
        rect = Rect.Intersect(rect, full);
        if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1) return doc with { CropRect = null };
        rect = new Rect(Math.Round(rect.X), Math.Round(rect.Y), Math.Round(rect.Width), Math.Round(rect.Height));
        if (rect == full) return doc with { CropRect = null };
        return doc with { CropRect = rect };
    }

    /// <summary>Axis-aligned bounds of an item after rotation.</summary>
    public static Rect VisualBounds(Item item)
    {
        if (item.Rotation == 0) return item.Bounds;
        var transform = new RotateTransform(item.Rotation, item.Center.X, item.Center.Y);
        return transform.TransformBounds(item.Bounds);
    }

    /// <summary>Union of the visual bounds of several items, or an empty rect when there are none.</summary>
    public static Rect UnionBounds(IEnumerable<Item> items)
    {
        var union = Rect.Empty;
        foreach (var item in items) union.Union(VisualBounds(item));
        return union;
    }

    /// <summary>Copies of the items with fresh ids (for paste or duplicate), shifted by <paramref name="offset"/>.</summary>
    public static ImmutableList<Item> Clone(IEnumerable<Item> items, Vector offset) =>
        items.Select(item => item with { Id = Guid.NewGuid(), Bounds = Rect.Offset(item.Bounds, offset) }).ToImmutableList();
}
