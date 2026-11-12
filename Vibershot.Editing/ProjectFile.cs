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

    public static void Save(string path, ImageDocument document, AssetStore assets)
    {
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry(DocumentEntry, CompressionLevel.Optimal).Open())
                JsonSerializer.Serialize(entry, document, DocumentJson.Options);

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
