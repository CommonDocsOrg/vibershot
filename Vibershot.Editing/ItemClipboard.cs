using System.Windows;
using System.Windows.Media.Imaging;

namespace Vibershot.Editing;

/// <summary>
/// Items travel on the clipboard as JSON under a private format, next to a flattened PNG so
/// other applications can paste the picture.
/// </summary>
public static class ItemClipboard
{
    public const string Format = "Vibershot.Items";

    public static string Serialize(IEnumerable<Item> items) => DocumentJson.Serialize(items.ToList());

    public static IReadOnlyList<Item> Deserialize(string json) => DocumentJson.Deserialize<List<Item>>(json) ?? [];

    public static DataObject CreateDataObject(IEnumerable<Item> items, BitmapSource? flattened)
    {
        var data = new DataObject();
        data.SetData(Format, Serialize(items));
        if (flattened != null) data.SetImage(flattened);
        return data;
    }

    /// <summary>The items on <paramref name="data"/>, or null when it holds none.</summary>
    public static IReadOnlyList<Item>? TryRead(IDataObject data)
    {
        if (!data.GetDataPresent(Format)) return null;
        try
        {
            return data.GetData(Format) is string json ? Deserialize(json) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
