using System.Windows;
using System.Windows.Media;

namespace Vibershot.Editing;

/// <summary>How one part of a stamp is painted.</summary>
public enum StampPaint
{
    /// <summary>Filled with the stamp's main colour.</summary>
    Main,
    /// <summary>Filled white.</summary>
    White,
    /// <summary>Filled with the line colour (eyes, dots).</summary>
    Ink,
    /// <summary>An open stroke in the line colour.</summary>
    Stroke,
    /// <summary>An open stroke in the main colour (a tick, a cross).</summary>
    MainStroke,
}

/// <summary>
/// One shape of a stamp: SVG path data on a 24 × 24 grid, how it is painted, and the outline
/// (or stroke) thickness in grid units. An outline of 0 leaves a filled part without an edge.
/// </summary>
public sealed record StampPart(string Data, StampPaint Paint, double Line = 1.5);

/// <summary>A ready-made vector drawing: searchable by <see cref="Name"/> and <see cref="Tags"/>.</summary>
public sealed record StampDefinition(string Id, string Name, string Tags, Color MainColor, params StampPart[] Parts)
{
    /// <summary>True when every word of <paramref name="query"/> appears in the name or the tags.</summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string haystack = Name + " " + Tags;
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The built-in stamps. Every stamp is path data on a 24 × 24 grid, so it scales to any size
/// and a new one is a single entry in <see cref="All"/>.
/// </summary>
public static class StampLibrary
{
    public const double Grid = 24;

    private static readonly Color Yellow = Color.FromRgb(0xF2, 0xC9, 0x4C), Red = Color.FromRgb(0xD6, 0x45, 0x45), Green = Color.FromRgb(0x1F, 0x9D, 0x6A),
        Blue = Color.FromRgb(0x2F, 0x6F, 0xDB), Orange = Color.FromRgb(0xF0, 0x61, 0x2A), Purple = Color.FromRgb(0x8E, 0x5B, 0xD8);

    /// <summary>The colour of outlines and details unless the item sets its own.</summary>
    public static readonly Color DefaultLine = Color.FromRgb(0x15, 0x18, 0x1C);

    private const string Disc = "M2,12 a10,10 0 1 0 20,0 a10,10 0 1 0 -20,0 Z";
    private const string LeftEye = "M7.1,9.6 a1.4,1.4 0 1 0 2.8,0 a1.4,1.4 0 1 0 -2.8,0 Z";
    private const string RightEye = "M14.1,9.6 a1.4,1.4 0 1 0 2.8,0 a1.4,1.4 0 1 0 -2.8,0 Z";

    public static readonly IReadOnlyList<StampDefinition> All =
    [
        new("smiley", "Smiley", "face happy smile emoji good", Yellow,
            P(Disc, StampPaint.Main), P(LeftEye, StampPaint.Ink, 0), P(RightEye, StampPaint.Ink, 0), P("M7.4,14 Q12,18.8 16.6,14", StampPaint.Stroke, 1.6)),
        new("sad", "Sad face", "face unhappy frown emoji bad", Yellow,
            P(Disc, StampPaint.Main), P(LeftEye, StampPaint.Ink, 0), P(RightEye, StampPaint.Ink, 0), P("M7.6,16.6 Q12,12.6 16.4,16.6", StampPaint.Stroke, 1.6)),
        new("house", "House", "home building start", Blue,
            P("M2.5,11.5 L12,3 L21.5,11.5 H19 V21 H5 V11.5 Z", StampPaint.Main), P("M10,21 V15 H14 V21 Z", StampPaint.White, 1.2)),
        new("pointer", "Mouse pointer", "cursor arrow mouse click select", Colors.White,
            P("M5,2 L5,19 L9.2,15 L12,21.5 L14.6,20.4 L11.8,14.2 L17.5,14.2 Z", StampPaint.Main)),
        new("click", "Click", "cursor pointer mouse click press tap", Colors.White,
            P("M9,8 L9,21.5 L12.3,18.4 L14.4,23 L16.4,22.1 L14.3,17.5 L18.8,17.5 Z", StampPaint.Main, 1.3),
            P("M9,4.5 V1.5 M5.2,5.6 L3.1,3.5 M4.2,9 H1.2 M12.8,5.6 L14.9,3.5", StampPaint.Stroke, 1.6)),
        new("hand", "Hand pointer", "cursor link finger mouse point", Colors.White,
            P("M9,2.5 a1.6,1.6 0 0 1 3.2,0 V9.2 L17.6,10.4 a2.2,2.2 0 0 1 1.7,2.5 L18.4,19 a3,3 0 0 1 -3,2.5 H11.6 a3,3 0 0 1 -2.5,-1.3 L5,14.2 a1.5,1.5 0 0 1 2.3,-1.9 L9,14 Z", StampPaint.Main, 1.3)),
        new("check", "Check mark", "tick yes done ok correct pass", Green,
            P("M4,12.8 L9.4,18.2 L20,6.2", StampPaint.MainStroke, 3.4)),
        new("cross", "Cross", "x no wrong error fail close delete", Red,
            P("M5.5,5.5 L18.5,18.5 M18.5,5.5 L5.5,18.5", StampPaint.MainStroke, 3.4)),
        new("star", "Star", "favourite favorite rating highlight", Yellow,
            P("M12,2.5 L14.47,9.1 L21.51,9.41 L15.99,13.8 L17.88,20.59 L12,16.7 L6.12,20.59 L8.01,13.8 L2.49,9.41 L9.53,9.1 Z", StampPaint.Main)),
        new("heart", "Heart", "love like favourite favorite", Red,
            P("M12,21 C5,15.5 2,12 2,8.2 C2,5.2 4.3,3 7.1,3 C9,3 10.9,4 12,5.8 C13.1,4 15,3 16.9,3 C19.7,3 22,5.2 22,8.2 C22,12 19,15.5 12,21 Z", StampPaint.Main)),
        new("warning", "Warning", "alert caution danger attention exclamation", Yellow,
            P("M12,2.5 L22.5,21 H1.5 Z", StampPaint.Main), P("M11,9 H13 L12.6,15 H11.4 Z", StampPaint.Ink, 0),
            P("M10.9,17.8 a1.1,1.1 0 1 0 2.2,0 a1.1,1.1 0 1 0 -2.2,0 Z", StampPaint.Ink, 0)),
        new("info", "Info", "information note help about", Blue,
            P(Disc, StampPaint.Main), P("M10.8,7.5 a1.2,1.2 0 1 0 2.4,0 a1.2,1.2 0 1 0 -2.4,0 Z", StampPaint.White, 0), P("M11,10.5 H13 V17.5 H11 Z", StampPaint.White, 0)),
        new("pin", "Map pin", "location marker place here", Red,
            P("M12,22 C7,15.5 5,12.5 5,9 A7,7 0 0 1 19,9 C19,12.5 17,15.5 12,22 Z", StampPaint.Main),
            P("M9.5,9 a2.5,2.5 0 1 0 5,0 a2.5,2.5 0 1 0 -5,0 Z", StampPaint.White, 1.2)),
        new("arrow", "Block arrow", "arrow right next direction point", Orange,
            P("M3,9 H13 V4.5 L21.5,12 L13,19.5 V15 H3 Z", StampPaint.Main)),
        new("lock", "Lock", "secure private password padlock", Yellow,
            P("M8,11 V7.5 a4,4 0 0 1 8,0 V11", StampPaint.Stroke, 1.8), P("M5,11 H19 V21 H5 Z", StampPaint.Main),
            P("M10.9,15.5 a1.1,1.1 0 1 0 2.2,0 a1.1,1.1 0 1 0 -2.2,0 Z", StampPaint.Ink, 0)),
        new("flag", "Flag", "mark milestone report", Red,
            P("M6,4 H19 L16,8.5 L19,13 H6 Z", StampPaint.Main), P("M6,2.5 V21.5", StampPaint.Stroke, 1.8)),
        new("bubble", "Comment", "speech chat message note talk", Purple,
            P("M4,4 H20 a2,2 0 0 1 2,2 V15 a2,2 0 0 1 -2,2 H11 L6,21.5 V17 H4 a2,2 0 0 1 -2,-2 V6 a2,2 0 0 1 2,-2 Z", StampPaint.Main)),
        new("bolt", "Lightning", "fast power flash energy quick", Yellow,
            P("M13.5,2 L4.5,13.5 H11 L9.5,22 L19.5,9.5 H12.5 Z", StampPaint.Main)),
    ];

    private static StampPart P(string data, StampPaint paint, double line = 1.5) => new(data, paint, line);

    private static readonly Dictionary<string, Geometry> Parsed = new();

    public static StampDefinition? Find(string id) => All.FirstOrDefault(stamp => string.Equals(stamp.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<StampDefinition> Search(string? query) => All.Where(stamp => stamp.Matches(query));

    private static Geometry GeometryOf(string data)
    {
        lock (Parsed)
        {
            if (!Parsed.TryGetValue(data, out var geometry))
            {
                geometry = Geometry.Parse(data);
                geometry.Freeze();
                Parsed[data] = geometry;
            }
            return geometry;
        }
    }

    /// <summary>
    /// Draws a stamp stretched over <paramref name="bounds"/>. <paramref name="main"/> and
    /// <paramref name="line"/> override the stamp's own colours; <paramref name="silhouette"/>
    /// paints every part in one brush (a drop shadow). An unknown id draws a dashed placeholder.
    /// </summary>
    public static void Draw(DrawingContext dc, string id, Rect bounds, Color? main = null, Color? line = null, Brush? silhouette = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (Find(id) is not { } stamp)
        {
            dc.DrawRectangle(null, new Pen(Brushes.Gray, 1) { DashStyle = DashStyles.Dash }, bounds);
            return;
        }
        Brush mainBrush = silhouette ?? Solid(main ?? stamp.MainColor);
        Brush lineBrush = silhouette ?? Solid(line ?? DefaultLine);
        Brush white = silhouette ?? Brushes.White;
        dc.PushTransform(new MatrixTransform(bounds.Width / Grid, 0, 0, bounds.Height / Grid, bounds.X, bounds.Y));
        foreach (var part in stamp.Parts)
        {
            var geometry = GeometryOf(part.Data);
            Pen? Outline(Brush brush) => part.Line > 0 ? new Pen(brush, part.Line) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round } : null;
            switch (part.Paint)
            {
                case StampPaint.Main: dc.DrawGeometry(mainBrush, Outline(lineBrush), geometry); break;
                case StampPaint.White: dc.DrawGeometry(white, Outline(lineBrush), geometry); break;
                case StampPaint.Ink: dc.DrawGeometry(lineBrush, Outline(lineBrush), geometry); break;
                case StampPaint.Stroke: dc.DrawGeometry(null, Outline(lineBrush), geometry); break;
                case StampPaint.MainStroke: dc.DrawGeometry(null, Outline(mainBrush), geometry); break;
            }
        }
        dc.Pop();
    }

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
