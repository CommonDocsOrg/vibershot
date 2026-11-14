using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Windows.Media;
using Vibershot.Editing;
using Vibershot.Editing.Video;

namespace Vibershot.UI;

/// <summary>One thing in the video editor's elements palette: a key to drag, a name and a small drawing of it.</summary>
public sealed record VideoElement(string Key, string Name, string Group, Item Sample);

/// <summary>
/// The elements palette: text, callouts, shapes, arrows and stamps that can be dragged onto the
/// timeline or the picture. They are the image editor's presets (<see cref="StyleLibrary"/>),
/// designed for a 720-line picture and scaled to the project's canvas when placed.
/// </summary>
public static class VideoElements
{
    /// <summary>The canvas height the presets' sizes were chosen for.</summary>
    public const double DesignHeight = 720;

    private static readonly StyleTheme Theme = StyleLibrary.BuiltIn[0];
    private static readonly Color Slate = Color.FromArgb(0xD9, 0x15, 0x18, 0x20);

    private static readonly (string Id, string Name, TextItem Style)[] Texts =
    [
        ("plain", "Text", new TextItem { Text = "Text", FontSize = 44, Bold = true, Color = Colors.White, Alignment = TextAlign.Center, Padding = 8 }),
        ("title", "Title", new TextItem { Text = "Title", FontSize = 92, Bold = true, Color = Colors.White, Alignment = TextAlign.Center, Padding = 10 }),
        ("boxed", "Boxed text", new TextItem { Text = "Boxed text", FontSize = 36, Bold = true, Color = Colors.White, Background = Slate, Alignment = TextAlign.Center, Padding = 16 }),
        ("lower", "Lower third", new TextItem { Text = "Name\nWhat they do", FontSize = 30, Color = Colors.White, Background = Theme.Primary, Alignment = TextAlign.Left, Padding = 16 }),
    ];

    /// <summary>Everything in the palette, in display order.</summary>
    public static IReadOnlyList<VideoElement> All { get; } = Build();

    private static List<VideoElement> Build()
    {
        var list = new List<VideoElement>();
        foreach (var (id, name, style) in Texts)
        {
            // The tile shows the word small, on the same box the preset samples use.
            var sample = style with { Text = id == "lower" ? "Name" : id == "title" ? "Title" : "Text", FontSize = id == "title" ? 24 : 15, Padding = 4 };
            var size = DocumentRenderer.MeasureText(sample with { Bounds = default });
            sample = sample with { Bounds = new Rect((StyleLibrary.SampleSize.Width - size.Width) / 2, (StyleLibrary.SampleSize.Height - size.Height) / 2, size.Width, size.Height) };
            list.Add(new VideoElement("Text:" + id, name, "Text", sample));
        }
        foreach (var (group, label) in new[] { (PresetGroup.Callouts, "Callouts"), (PresetGroup.Shapes, "Shapes"), (PresetGroup.Arrows, "Arrows and lines"), (PresetGroup.Stamps, "Stamps") })
            foreach (var preset in StyleLibrary.Presets(Theme, group))
                list.Add(new VideoElement(group + ":" + preset.Id, preset.Name, label, preset.Sample));
        return list;
    }

    /// <summary>A new item for a palette key, centred on a canvas point and sized for the project. Null for an unknown key.</summary>
    public static Item? Create(VideoProject project, string key, Point centre)
    {
        double scale = project.Height / DesignHeight;
        string[] parts = key.Split(':', 2);
        if (parts.Length != 2) return null;
        Item item;
        if (parts[0] == "Text")
        {
            if (Texts.FirstOrDefault(text => text.Id == parts[1]) is not { Style: { } style }) return null;
            var size = DocumentRenderer.MeasureText(style with { Bounds = default });
            item = style with { Id = Guid.NewGuid(), Bounds = new Rect(centre.X - size.Width / 2, centre.Y - size.Height / 2, size.Width, size.Height) };
        }
        else
        {
            if (!Enum.TryParse<PresetGroup>(parts[0], out var group) || StyleLibrary.Find(Theme, group, parts[1]) is not { } preset) return null;
            item = StyleLibrary.Place(preset.Style, preset.Sample, centre, 3);
            // Dropped on a video there is nothing yet to say, so a callout starts with a word to replace.
            if (item is CalloutItem callout) item = callout with { Text = "Callout" };
        }
        return Scale(item, scale, centre);
    }

    /// <summary>Grows or shrinks an item about a point: its box, its lines, its letters and its pointers.</summary>
    public static Item Scale(Item item, double factor, Point about)
    {
        if (Math.Abs(factor - 1) < 1e-9) return item;
        Point Map(Point p) => new(about.X + (p.X - about.X) * factor, about.Y + (p.Y - about.Y) * factor);
        var b = item.Bounds;
        var topLeft = Map(b.TopLeft);
        Item scaled = item with { Bounds = new Rect(Math.Round(topLeft.X), Math.Round(topLeft.Y), Math.Round(b.Width * factor), Math.Round(b.Height * factor)), StrokeWidth = item.StrokeWidth * factor };
        return scaled switch
        {
            CalloutItem callout => callout with
            {
                FontSize = callout.FontSize * factor, Padding = callout.Padding * factor, CornerRadius = callout.CornerRadius * factor, HeadSize = callout.HeadSize * factor,
                Targets = callout.Targets.Select(Map).ToImmutableList(),
            },
            TextItem text => text with { FontSize = text.FontSize * factor, Padding = text.Padding * factor },
            RectItem rect => rect with { CornerRadius = rect.CornerRadius * factor },
            ArrowItem arrow => arrow with { HeadSize = arrow.HeadSize * factor },
            _ => scaled,
        };
    }

    /// <summary>The palette tile's picture: the element's sample drawn on its 96 x 64 box.</summary>
    public static ImageSource Thumbnail(VideoElement element)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(StyleLibrary.SampleSize));
            DocumentRenderer.RenderItem(dc, element.Sample, new AssetStore());
        }
        group.ClipGeometry = new RectangleGeometry(new Rect(StyleLibrary.SampleSize));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
