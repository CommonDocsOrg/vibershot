using System.Windows;
using System.Windows.Media;

namespace Vibershot.Editing;

/// <summary>
/// A named look shared by arrows, shapes and callouts: the colours, line width and corner radii
/// that every preset in the editor's library, and every drawing tool's next item, is built from.
/// </summary>
public sealed record StyleTheme(string Id, string Name, Color Primary, Color Tint, Color Ink, Color OnPrimary, Color Highlight, double StrokeWidth, double CornerRadius, double CalloutRadius);

/// <summary>The tabs of the editor's library.</summary>
public enum PresetGroup { Arrows, Shapes, Callouts, Stamps }

/// <summary>
/// One ready-made item in the library. <see cref="Sample"/> is a small drawing inside
/// <see cref="StyleLibrary.SampleSize"/> for the preview tile; <see cref="Style"/> carries the
/// real settings (colours, widths, radii) an inserted item, or the tool's next item, takes on.
/// </summary>
public sealed record StylePreset(string Id, string Name, PresetGroup Group, Item Sample, Item Style);

/// <summary>The style each drawing tool gives its next item.</summary>
public sealed record ToolStyles(ArrowItem Arrow, LineItem Line, RectItem Rect, EllipseItem Ellipse, TextItem Text, CalloutItem Callout);

/// <summary>The built-in themes and the presets each theme produces.</summary>
public static class StyleLibrary
{
    public const string DefaultThemeId = "default";

    /// <summary>The box every <see cref="StylePreset.Sample"/> is drawn inside.</summary>
    public static readonly Size SampleSize = new(96, 64);

    private static readonly Color Dark = Color.FromRgb(0x15, 0x18, 0x1C);

    public static readonly IReadOnlyList<StyleTheme> BuiltIn =
    [
        new("vibershot", "Vibershot", Rgb(0x7B52D1), Rgb(0xEDE5FF), Rgb(0x241640), Colors.White, Rgb(0xFFD84D), 4, 10, 10),
        new("signal", "Signal red", Rgb(0xE5484D), Rgb(0xFFE9E9), Rgb(0x3B1113), Colors.White, Rgb(0xFFE066), 5, 4, 6),
        new("blueprint", "Blueprint", Rgb(0x2F6FEB), Rgb(0xE3EEFF), Rgb(0x0E2350), Colors.White, Rgb(0x7EE0FF), 3, 0, 2),
        new("marker", "Marker", Rgb(0x1B1B1F), Rgb(0xFFD84D), Rgb(0x1B1B1F), Rgb(0xFFD84D), Rgb(0xFF8A3D), 4, 14, 14),
    ];

    /// <summary>The theme built from Settings → Editor's stroke colour and width, so existing defaults keep working.</summary>
    public static StyleTheme FromDefaults(Color stroke, double strokeWidth)
    {
        double luminance = (0.299 * stroke.R + 0.587 * stroke.G + 0.114 * stroke.B) / 255;
        return new StyleTheme(DefaultThemeId, "Default", stroke, Mix(stroke, Colors.White, 0.85), Dark, luminance > 0.62 ? Dark : Colors.White,
            Rgb(0xF2C94C), Math.Clamp(strokeWidth, 1, 1000), 0, 8);
    }

    /// <summary>Every theme on offer: the Settings defaults first, then the built-in ones.</summary>
    public static IReadOnlyList<StyleTheme> Themes(Color defaultStroke, double defaultStrokeWidth) => [FromDefaults(defaultStroke, defaultStrokeWidth), .. BuiltIn];

    /// <summary>The style each drawing tool starts with under a theme.</summary>
    public static ToolStyles Defaults(StyleTheme theme)
    {
        var arrows = Presets(theme, PresetGroup.Arrows);
        var shapes = Presets(theme, PresetGroup.Shapes);
        var callouts = Presets(theme, PresetGroup.Callouts);
        return new ToolStyles(
            (ArrowItem)Style(arrows, "arrow"), (LineItem)Style(arrows, "line"), (RectItem)Style(shapes, "outline"), (EllipseItem)Style(shapes, "ellipse"),
            new TextItem { Color = theme.Primary }, (CalloutItem)Style(callouts, "outline-label"));
    }

    private static Item Style(IReadOnlyList<StylePreset> presets, string id) => presets.First(preset => preset.Id == id).Style;

    public static StylePreset? Find(StyleTheme theme, PresetGroup group, string id) => Presets(theme, group).FirstOrDefault(preset => preset.Id == id);

    /// <summary>The presets of one library tab, drawn in a theme. Stamps keep their own colours.</summary>
    public static IReadOnlyList<StylePreset> Presets(StyleTheme theme, PresetGroup group)
    {
        double w = theme.StrokeWidth;
        switch (group)
        {
            case PresetGroup.Arrows:
            {
                Point tail = new(14, 52), head = new(82, 12);
                StylePreset Arrow(string id, string name, ArrowItem style) => new(id, name, group, style.WithPoints(tail, head), style);
                var plain = new ArrowItem { Stroke = theme.Primary, StrokeWidth = w, HeadSize = Math.Max(14, w * 3.5) };
                var line = new LineItem { Stroke = theme.Primary, StrokeWidth = w };
                var marker = new LineItem { Stroke = theme.Highlight, StrokeWidth = Math.Max(14, w * 4), Opacity = 0.55 };
                return
                [
                    Arrow("arrow", "Arrow", plain),
                    Arrow("bold", "Bold", plain with { StrokeWidth = w + 3, HeadSize = Math.Max(20, (w + 3) * 3.5) }),
                    Arrow("fine", "Fine", plain with { StrokeWidth = 2, HeadSize = 10 }),
                    Arrow("rounded", "Rounded", plain with { HeadRoundness = 1 }),
                    new("line", "Line", group, line.WithPoints(tail, head), line),
                    new("highlighter", "Highlighter", group, marker.WithPoints(new Point(14, 34), new Point(82, 34)), marker),
                ];
            }
            case PresetGroup.Shapes:
            {
                var box = new Rect(12, 8, 72, 48);
                StylePreset Shape(string id, string name, Item style, Rect? bounds = null)
                {
                    Item sample = style with { Bounds = bounds ?? box };
                    if (sample is RectItem rect) sample = rect with { CornerRadius = Math.Min(rect.CornerRadius, 12) };
                    return new StylePreset(id, name, group, sample, style);
                }
                var outline = new RectItem { Stroke = theme.Primary, StrokeWidth = w, CornerRadius = theme.CornerRadius };
                var ellipse = new EllipseItem { Stroke = theme.Primary, StrokeWidth = w };
                return
                [
                    Shape("outline", "Outline", outline),
                    Shape("tinted", "Tinted", outline with { Fill = theme.Tint }),
                    Shape("solid", "Solid", outline with { Fill = theme.Primary, Stroke = null, StrokeWidth = 0 }),
                    Shape("ellipse", "Ellipse", ellipse),
                    Shape("tinted-ellipse", "Tinted ellipse", ellipse with { Fill = theme.Tint }),
                    Shape("highlight", "Highlight", new RectItem { Fill = theme.Highlight, Opacity = 0.5, CornerRadius = 3 }, new Rect(8, 20, 80, 24)),
                ];
            }
            case PresetGroup.Callouts:
            {
                double line = Math.Min(w, 4);
                StylePreset Callout(string id, string name, string label, CalloutItem style)
                {
                    bool isLabel = style.Kind == CalloutKind.Label;
                    var sample = style with
                    {
                        Text = label, FontSize = 11.5, Bold = true, Padding = 3, StrokeWidth = Math.Min(style.StrokeWidth, 3), HeadSize = 9,
                        CornerRadius = Math.Min(style.CornerRadius, 10),
                        Bounds = isLabel ? new Rect(32, 6, 58, 26) : new Rect(10, 4, 76, 40),
                        Targets = [isLabel ? new Point(10, 58) : new Point(20, 62)],
                    };
                    return new StylePreset(id, name, group, sample, style);
                }
                var solid = new CalloutItem
                {
                    Kind = CalloutKind.Label, Alignment = TextAlign.Center, Padding = 10, Color = theme.OnPrimary, Background = theme.Primary,
                    Stroke = theme.Primary, StrokeWidth = line, CornerRadius = theme.CalloutRadius, HeadSize = Math.Max(10, line * 3),
                };
                var outline = solid with { Color = theme.Ink, Background = Colors.White };
                var tinted = solid with { Color = theme.Ink, Background = theme.Tint };
                double bubble = Math.Max(theme.CalloutRadius, 18);
                return
                [
                    Callout("label", "Label", "Label", solid),
                    Callout("outline-label", "Outline label", "Label", outline),
                    Callout("speech", "Speech", "Speech", solid with { Kind = CalloutKind.Speech, CornerRadius = bubble }),
                    Callout("outline-speech", "Outline speech", "Speech", outline with { Kind = CalloutKind.Speech, CornerRadius = bubble }),
                    Callout("oval", "Oval", "Oval", tinted with { Kind = CalloutKind.Oval }),
                    Callout("thought", "Thought", "Hmm", tinted with { Kind = CalloutKind.Thought }),
                ];
            }
            default:
                return StampLibrary.All.Select(stamp =>
                {
                    var style = new StampItem { StampId = stamp.Id };
                    return new StylePreset(stamp.Id, stamp.Name, group, style with { Bounds = new Rect(22, 6, 52, 52) }, style);
                }).ToList();
        }
    }

    /// <summary>
    /// A new item in <paramref name="style"/>, centred on <paramref name="centre"/>. Arrows, shapes and
    /// stamps take the sample's geometry enlarged by <paramref name="scale"/>; a callout is sized for
    /// its own font and, when the sample points at something, sits up and to the right of its pointer.
    /// </summary>
    public static Item Place(Item style, Item sample, Point centre, double scale)
    {
        centre = new Point(Math.Round(centre.X), Math.Round(centre.Y));
        if (style is CalloutItem callout)
        {
            bool round = callout.Kind is CalloutKind.Oval or CalloutKind.Thought;
            bool points = sample is CalloutItem { Targets.Count: > 0 };
            var item = callout with { Id = Guid.NewGuid(), Text = "", Bounds = new Rect(0, 0, round ? 240 : 200, 10) };
            double width = item.Bounds.Width, height = Math.Ceiling(Math.Max(DocumentRenderer.MeasureText(item).Height, round ? 120 : 56));
            var body = points ? centre + new Vector(75, -55) : centre;
            return item with
            {
                Bounds = new Rect(Math.Round(body.X - width / 2), Math.Round(body.Y - height / 2), width, height),
                Targets = points ? [centre + new Vector(-75, 55)] : [],
            };
        }
        double w = Math.Max(1, Math.Round(sample.Bounds.Width * scale)), h = Math.Max(1, Math.Round(sample.Bounds.Height * scale));
        // A level line has no height to scale; its bounds stay flat.
        if (sample.Bounds.Height == 0) h = 0;
        if (sample.Bounds.Width == 0) w = 0;
        var placed = style with { Id = Guid.NewGuid(), Bounds = new Rect(Math.Round(centre.X - w / 2), Math.Round(centre.Y - h / 2), w, h) };
        return placed is LineItem line && sample is LineItem drawn ? line with { Flipped = drawn.Flipped, Reversed = drawn.Reversed } : placed;
    }

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
