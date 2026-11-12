using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vibershot.Editing;

/// <summary>
/// Pixel data referenced by <see cref="ImageItem"/>s. Bitmaps are frozen so document snapshots
/// can share them freely across threads and undo states.
/// </summary>
public sealed class AssetStore
{
    private readonly Dictionary<Guid, BitmapSource> _assets = new();

    public IReadOnlyCollection<Guid> Ids => _assets.Keys;

    public Guid Add(BitmapSource image)
    {
        var id = Guid.NewGuid();
        Add(id, image);
        return id;
    }

    public void Add(Guid id, BitmapSource image) => _assets[id] = Freeze(image);

    public BitmapSource? Get(Guid id) => _assets.TryGetValue(id, out var image) ? image : null;

    public bool Contains(Guid id) => _assets.ContainsKey(id);

    /// <summary>Returns a frozen copy of <paramref name="image"/> in a plain 32-bit format.</summary>
    public static BitmapSource Freeze(BitmapSource image)
    {
        if (image.IsFrozen && (image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32)) return image;
        // CachedBitmap copies the pixels so the result no longer depends on the source (which may be a live clipboard object).
        BitmapSource converted = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var cached = new CachedBitmap(converted, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }
}
