using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Vibershot.Editing;

/// <summary>
/// The <c>.vibeshot</c> project: a zip holding <c>document.json</c> and one PNG per referenced
/// asset under <c>assets/</c>. Everything stays editable after a round trip.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".vibeshot";
    public const string DocumentEntry = "document.json";
    public const string AssetFolder = "assets/";

    /// <summary>Flattened picture of the document at save time, at most <see cref="PreviewEdge"/> px on its longest side, for galleries and shells.</summary>
    public const string PreviewEntry = "preview.png";
    public const int PreviewEdge = 512;

    public static void Save(string path, ImageDocument document, AssetStore assets)
    {
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry(DocumentEntry, CompressionLevel.Optimal).Open())
                JsonSerializer.Serialize(entry, document, DocumentJson.Options);

            // A small flattened preview so the gallery never has to load the whole project to draw a tile.
            try
            {
                var preview = RenderPreview(document, assets);
                using var previewBuffer = new MemoryStream();
                var previewEncoder = new PngBitmapEncoder();
                previewEncoder.Frames.Add(BitmapFrame.Create(preview));
                previewEncoder.Save(previewBuffer);
                previewBuffer.Position = 0;
                using var previewEntry = zip.CreateEntry(PreviewEntry, CompressionLevel.NoCompression).Open();
                previewBuffer.CopyTo(previewEntry);
            }
            catch (Exception) { /* The preview is a convenience; the document and assets are what matter. */ }

            var referenced = document.AllItems.OfType<ImageItem>().Select(item => item.AssetId).Distinct();
            foreach (var id in referenced)
            {
                var bitmap = assets.Get(id);
                if (bitmap == null) continue;
                // The encoder needs a seekable stream, which a zip entry is not; PNG is already
                // compressed, so the entry is stored rather than deflated again.
                using var buffer = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(buffer);
                buffer.Position = 0;
                using var entry = zip.CreateEntry(AssetFolder + id.ToString("N") + ".png", CompressionLevel.NoCompression).Open();
                buffer.CopyTo(entry);
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Flattens the content rectangle and scales it down to the preview size.</summary>
    public static BitmapSource RenderPreview(ImageDocument document, AssetStore assets)
    {
        var flat = DocumentRenderer.Flatten(document, assets);
        int w = flat.PixelWidth, h = flat.PixelHeight;
        if (w <= PreviewEdge && h <= PreviewEdge) return flat;
        double scale = (double)PreviewEdge / Math.Max(w, h);
        var scaled = new TransformedBitmap(flat, new System.Windows.Media.ScaleTransform(scale, scale));
        var cached = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }

    /// <summary>Reads the embedded preview without loading the document; null for projects saved without one.</summary>
    public static BitmapSource? ReadPreview(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry(PreviewEntry);
            if (entry is null) return null;
            using var png = entry.Open();
            using var buffer = new MemoryStream();
            png.CopyTo(buffer);
            buffer.Position = 0;
            var decoder = new PngBitmapDecoder(buffer, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch { return null; }
    }

    /// <summary>The canvas size recorded in the document, read without decoding any pixels.</summary>
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry(DocumentEntry);
            if (entry is null) return null;
            using var json = entry.Open();
            var document = JsonSerializer.Deserialize<ImageDocument>(json, DocumentJson.Options);
            if (document is null) return null;
            var rect = document.ContentRect;
            return ((int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
        }
        catch { return null; }
    }

    public static (ImageDocument Document, AssetStore Assets) Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static (ImageDocument Document, AssetStore Assets) Load(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var documentEntry = zip.GetEntry(DocumentEntry) ?? throw new InvalidDataException("Not a Vibershot project: document.json is missing.");
        ImageDocument document;
        using (var entry = documentEntry.Open())
            document = JsonSerializer.Deserialize<ImageDocument>(entry, DocumentJson.Options) ?? throw new InvalidDataException("The project document is empty.");

        var assets = new AssetStore();
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(AssetFolder, StringComparison.OrdinalIgnoreCase) || !entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(entry.FullName);
            if (!Guid.TryParse(name, out var id)) continue;
            using var png = entry.Open();
            using var buffer = new MemoryStream();
            png.CopyTo(buffer);
            buffer.Position = 0;
            var decoder = new PngBitmapDecoder(buffer, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            assets.Add(id, decoder.Frames[0]);
        }
        return (document, assets);
    }
}
