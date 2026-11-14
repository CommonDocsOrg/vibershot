using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Vibershot.Editing;

public enum TextAlign { Left, Center, Right }

/// <summary>
/// One object on a layer. Items are immutable records: editing produces a new item with the
/// same <see cref="Id"/>. The JSON discriminator lets later item kinds (callout, stamp,
/// blur, highlight) be added as further derived records.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ImageItem), "image")]
[JsonDerivedType(typeof(RectItem), "rect")]
[JsonDerivedType(typeof(EllipseItem), "ellipse")]
[JsonDerivedType(typeof(LineItem), "line")]
[JsonDerivedType(typeof(ArrowItem), "arrow")]
[JsonDerivedType(typeof(TextItem), "text")]
[JsonDerivedType(typeof(CalloutItem), "callout")]
[JsonDerivedType(typeof(StampItem), "stamp")]
public abstract record Item
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Unrotated bounds in document pixels.</summary>
    public Rect Bounds { get; init; }

    /// <summary>Degrees, clockwise, about the centre of <see cref="Bounds"/>.</summary>
    public double Rotation { get; init; }

    public Color? Stroke { get; init; }
    public double StrokeWidth { get; init; }
    public Color? Fill { get; init; }
    public double Opacity { get; init; } = 1;
    public bool Shadow { get; init; }

    [JsonIgnore] public Point Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
    [JsonIgnore] public bool HasStroke => Stroke is { A: > 0 } && StrokeWidth > 0;
}

/// <summary>A placed bitmap; the pixels live in the <see cref="AssetStore"/> under <see cref="AssetId"/>.</summary>
public sealed record ImageItem : Item
{
    public Guid AssetId { get; init; }
}

public sealed record RectItem : Item
{
    public double CornerRadius { get; init; }
}

public sealed record EllipseItem : Item;

/// <summary>
/// A straight line from one corner of <see cref="Item.Bounds"/> to the opposite one: top-left
/// to bottom-right normally, bottom-left to top-right when <see cref="Flipped"/>.
/// </summary>
public record LineItem : Item
{
    public bool Flipped { get; init; }

    [JsonIgnore] public Point Start => Flipped ? new Point(Bounds.Left, Bounds.Bottom) : Bounds.TopLeft;
    [JsonIgnore] public Point End => Flipped ? new Point(Bounds.Right, Bounds.Top) : Bounds.BottomRight;

    /// <summary>Builds the bounds and flip flag for a line between two points.</summary>
    public static (Rect Bounds, bool Flipped) FromPoints(Point start, Point end)
    {
        var bounds = new Rect(start, end);
        bool flipped = (end.X - start.X) * (end.Y - start.Y) < 0;
        return (bounds, flipped);
    }

    /// <summary>Returns the item re-laid between two points (keeps direction: the line starts at <paramref name="start"/>).</summary>
    public LineItem WithPoints(Point start, Point end)
    {
        var (bounds, flipped) = FromPoints(start, end);
        bool reversed = end.X < start.X || (end.X == start.X && end.Y < start.Y);
        return this with { Bounds = bounds, Flipped = flipped, Reversed = reversed };
    }

    /// <summary>True when the line runs right-to-left (the arrow head sits at the left end).</summary>
    public bool Reversed { get; init; }

    [JsonIgnore] public Point Tail => Reversed ? End : Start;
    [JsonIgnore] public Point Head => Reversed ? Start : End;
}

public sealed record ArrowItem : LineItem
{
    public double HeadSize { get; init; } = 14;

    /// <summary>How rounded the head's three corners are: 0 is a sharp triangle, 1 rounds it into a blob.</summary>
    public double HeadRoundness { get; init; } = 0.25;
}

public record TextItem : Item
{
    public string Text { get; init; } = "";
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 18;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public Color Color { get; init; } = Colors.Black;
    [JsonConverter(typeof(JsonStringEnumConverter<TextAlign>))]
    public TextAlign Alignment { get; init; } = TextAlign.Left;
    public Color? Background { get; init; }
    public double Padding { get; init; } = 6;

    /// <summary>Colour of the line drawn round the letters.</summary>
    public Color OutlineColor { get; init; } = Colors.Black;

    /// <summary>How far the line round the letters reaches beyond them, in document pixels; 0 draws none.</summary>
    public double OutlineWidth { get; init; }

    /// <summary>A soft dark copy of the letters, down and to the right. (<see cref="Item.Shadow"/> is the shadow of the box.)</summary>
    public bool TextShadow { get; init; }

    [JsonIgnore] public bool HasOutline => OutlineColor.A > 0 && OutlineWidth > 0;
}

public enum CalloutKind { Label, Speech, Oval, Thought }

/// <summary>
/// A text box with a body shape and pointers. <see cref="TextItem.Background"/> fills the body and
/// <see cref="Item.Stroke"/> outlines it. Each entry of <see cref="Targets"/> is a point in document
/// space the callout points at: a <see cref="CalloutKind.Label"/> draws an arrow to it, the speech
/// and thought kinds grow a tail towards it. Targets do not rotate with the body.
/// </summary>
public sealed record CalloutItem : TextItem
{
    [JsonConverter(typeof(JsonStringEnumConverter<CalloutKind>))]
    public CalloutKind Kind { get; init; } = CalloutKind.Label;
    public ImmutableList<Point> Targets { get; init; } = ImmutableList<Point>.Empty;
    public double CornerRadius { get; init; } = 8;
    public double HeadSize { get; init; } = 12;
    public double HeadRoundness { get; init; } = 0.25;
}

/// <summary>
/// A vector drawing from the <see cref="StampLibrary"/>, stretched over <see cref="Item.Bounds"/>.
/// <see cref="Item.Fill"/> overrides the stamp's main colour and <see cref="Item.Stroke"/> its line colour.
/// </summary>
public sealed record StampItem : Item
{
    public string StampId { get; init; } = "";
}

public sealed record Layer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Layer";
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }
    public double Opacity { get; init; } = 1;
    public ImmutableList<Item> Items { get; init; } = ImmutableList<Item>.Empty;
}

/// <summary>The whole editable picture: a canvas size, an optional crop and layers bottom to top.</summary>
public sealed record ImageDocument
{
    /// <summary>Current <c>document.json</c> schema version written by <see cref="ProjectFile"/>.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version of the file this document came from; lets later versions migrate older projects.</summary>
    public int Version { get; init; } = CurrentVersion;

    public Size Size { get; init; } = new(1280, 720);

    /// <summary>Non-destructive crop; null shows the whole canvas.</summary>
    public Rect? CropRect { get; init; }

    /// <summary>Canvas colour under every layer; transparent shows the checkerboard and exports with alpha.</summary>
    public Color Background { get; init; } = Colors.Transparent;
    public ImmutableList<Layer> Layers { get; init; } = ImmutableList<Layer>.Empty;

    /// <summary>The rectangle export produces: the crop when set, otherwise the full canvas.</summary>
    [JsonIgnore] public Rect ContentRect => CropRect ?? new Rect(Size);

    [JsonIgnore] public IEnumerable<Item> AllItems => Layers.SelectMany(layer => layer.Items);
}
