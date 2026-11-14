using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vibershot.Editing;

/// <summary>
/// Draws documents and items onto a <see cref="DrawingContext"/>. The editor canvas and the
/// exporter both go through here so what is saved matches what is on screen.
/// </summary>
public static class DocumentRenderer
{
    /// <summary>Export renders in bands of this many rows so a tall stitched canvas never needs one huge render target.</summary>
    public const int BandHeight = 4096;

    private static readonly Brush ShadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)));
    private static readonly Vector ShadowOffset = new(3, 3);

    public static void Render(DrawingContext dc, ImageDocument doc, AssetStore assets, bool hitTestable = false)
    {
        if (doc.Background.A > 0) dc.DrawRectangle(Frozen(new SolidColorBrush(doc.Background)), null, new Rect(doc.Size));
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) continue;
            bool faded = layer.Opacity < 1;
            if (faded) dc.PushOpacity(layer.Opacity);
            foreach (var item in layer.Items) RenderItem(dc, item, assets, hitTestable);
            if (faded) dc.Pop();
        }
    }

    /// <summary>
    /// Draws one item. With <paramref name="hitTestable"/> the item also gets invisible fills
    /// and a widened invisible stroke so clicks inside hollow shapes and near thin lines hit.
    /// </summary>
    public static void RenderItem(DrawingContext dc, Item item, AssetStore assets, bool hitTestable = false)
    {
        bool rotated = item.Rotation != 0;
        if (rotated) dc.PushTransform(new RotateTransform(item.Rotation, item.Center.X, item.Center.Y));
        bool faded = item.Opacity < 1;
        if (faded) dc.PushOpacity(Math.Clamp(item.Opacity, 0, 1));

        switch (item)
        {
            case ImageItem image: DrawImage(dc, image, assets, hitTestable); break;
            case RectItem rect: DrawShape(dc, item, new RectangleGeometry(rect.Bounds, rect.CornerRadius, rect.CornerRadius), hitTestable); break;
            case EllipseItem: DrawShape(dc, item, new EllipseGeometry(item.Bounds), hitTestable); break;
            case ArrowItem arrow: DrawArrow(dc, arrow, hitTestable); break;
            case LineItem line: DrawLine(dc, line, hitTestable); break;
            case CalloutItem callout: DrawCallout(dc, callout, hitTestable); break;
            case TextItem text: DrawText(dc, text, hitTestable); break;
            case StampItem stamp:
                if (item.Shadow)
                {
                    dc.PushTransform(new TranslateTransform(ShadowOffset.X, ShadowOffset.Y));
                    StampLibrary.Draw(dc, stamp.StampId, stamp.Bounds, null, null, ShadowBrush);
                    dc.Pop();
                }
                StampLibrary.Draw(dc, stamp.StampId, stamp.Bounds, stamp.Fill, stamp.Stroke is { A: > 0 } ? stamp.Stroke : null);
                if (hitTestable) dc.DrawRectangle(Brushes.Transparent, null, stamp.Bounds);
                break;
        }

        if (faded) dc.Pop();
        if (rotated) dc.Pop();
    }

    private static void DrawImage(DrawingContext dc, ImageItem item, AssetStore assets, bool hitTestable)
    {
        var bitmap = assets.Get(item.AssetId);
        if (item.Shadow) DrawShadowRect(dc, item.Bounds);
        if (bitmap != null) dc.DrawImage(bitmap, item.Bounds);
        else dc.DrawRectangle(Brushes.LightGray, new Pen(Brushes.Gray, 1), item.Bounds);
        if (item.HasStroke) dc.DrawRectangle(null, MakePen(item), item.Bounds);
        else if (hitTestable && bitmap == null) dc.DrawRectangle(Brushes.Transparent, null, item.Bounds);
    }

    private static void DrawShape(DrawingContext dc, Item item, Geometry geometry, bool hitTestable)
    {
        var fill = item.Fill is { A: > 0 } color ? Frozen(new SolidColorBrush(color)) : null;
        var pen = item.HasStroke ? MakePen(item) : null;
        if (item.Shadow && (fill != null || pen != null))
        {
            dc.PushTransform(new TranslateTransform(ShadowOffset.X, ShadowOffset.Y));
            dc.DrawGeometry(fill != null ? ShadowBrush : null, pen != null ? new Pen(ShadowBrush, pen.Thickness) : null, geometry);
            dc.Pop();
        }
        dc.DrawGeometry(fill ?? (hitTestable ? Brushes.Transparent : null), pen, geometry);
    }

    private static void DrawLine(DrawingContext dc, LineItem line, bool hitTestable)
    {
        var geometry = new LineGeometry(line.Start, line.End);
        if (line.HasStroke)
        {
            if (line.Shadow)
            {
                dc.PushTransform(new TranslateTransform(ShadowOffset.X, ShadowOffset.Y));
                dc.DrawGeometry(null, new Pen(ShadowBrush, line.StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
                dc.Pop();
            }
            dc.DrawGeometry(null, MakePen(line), geometry);
        }
        if (hitTestable) dc.DrawGeometry(null, HitPen(line), geometry);
    }

    private static void DrawArrow(DrawingContext dc, ArrowItem arrow, bool hitTestable)
    {
        if (arrow.HasStroke)
        {
            var color = Frozen(new SolidColorBrush(arrow.Stroke!.Value));
            if (arrow.Shadow)
            {
                dc.PushTransform(new TranslateTransform(ShadowOffset.X, ShadowOffset.Y));
                DrawArrowShape(dc, ShadowBrush, arrow.Tail, arrow.Head, arrow.HeadSize, arrow.StrokeWidth, arrow.HeadRoundness);
                dc.Pop();
            }
            DrawArrowShape(dc, color, arrow.Tail, arrow.Head, arrow.HeadSize, arrow.StrokeWidth, arrow.HeadRoundness);
        }
        if (hitTestable) dc.DrawGeometry(Brushes.Transparent, HitPen(arrow), new LineGeometry(arrow.Tail, arrow.Head));
    }

    /// <summary>
    /// Draws a shaft from <paramref name="tail"/> and a triangular head whose tip sits on
    /// <paramref name="head"/>. <paramref name="roundness"/> (0 to 1) rounds the head's corners:
    /// the triangle is shrunk towards its incentre and stroked back out with a round pen, so the
    /// flat sides stay where they were and only the corners soften.
    /// </summary>
    public static void DrawArrowShape(DrawingContext dc, Brush brush, Point tail, Point head, double headSize, double strokeWidth, double roundness)
    {
        var direction = head - tail;
        double length = direction.Length;
        if (length < 0.01) { dc.DrawEllipse(brush, null, head, strokeWidth / 2, strokeWidth / 2); return; }
        direction.Normalize();
        double headLength = Math.Min(headSize + strokeWidth * 1.5, length);
        var normal = new Vector(-direction.Y, direction.X);
        var baseCenter = head - direction * headLength;
        double halfWidth = headLength * 0.5;
        Point a = baseCenter + normal * halfWidth, b = head, c = baseCenter - normal * halfWidth;

        // Incentre and inradius of the head triangle.
        double la = (b - c).Length, lb = (a - c).Length, lc = (a - b).Length, perimeter = la + lb + lc;
        var incentre = new Point((la * a.X + lb * b.X + lc * c.X) / perimeter, (la * a.Y + lb * b.Y + lc * c.Y) / perimeter);
        double inradius = Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / perimeter;
        double r = Math.Clamp(roundness, 0, 1) * inradius;
        double keep = inradius > 0 ? (inradius - r) / inradius : 1;
        // Rounding pulls the tip back from the apex; slide the head forward by that much so the arrow still touches what it points at.
        double halfAngle = Math.Atan2(halfWidth, headLength);
        var advance = direction * (r / Math.Sin(halfAngle) - r);
        Point Inset(Point p) => incentre + (p - incentre) * keep + advance;

        // The shaft ends inside the head, wherever the head now sits.
        var shaftEnd = head - direction * (headLength * 0.75) + advance;
        if ((shaftEnd - tail) * direction > 0)
            dc.DrawLine(new Pen(brush, strokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat }, tail, shaftEnd);

        var figure = new PathFigure { StartPoint = Inset(a), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(Inset(b), true));
        figure.Segments.Add(new LineSegment(Inset(c), true));
        var triangle = new PathGeometry([figure]);
        dc.DrawGeometry(brush, r > 0.01 ? new Pen(brush, r * 2) { LineJoin = PenLineJoin.Round } : null, triangle);
    }

    private static void DrawText(DrawingContext dc, TextItem item, bool hitTestable)
    {
        var bounds = item.Bounds;
        if (item.Shadow) DrawShadowRect(dc, bounds);
        if (item.Background is { A: > 0 } background) dc.DrawRectangle(Frozen(new SolidColorBrush(background)), null, bounds);
        else if (hitTestable) dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (item.HasStroke) dc.DrawRectangle(null, MakePen(item), bounds);
        DrawTextRuns(dc, item, centred: false);
    }

    private static void DrawTextRuns(DrawingContext dc, TextItem item, bool centred)
    {
        if (string.IsNullOrEmpty(item.Text)) return;
        var area = TextArea(item);
        var formatted = FormatText(item, Frozen(new SolidColorBrush(item.Color)));
        double y = centred ? area.Y + Math.Max(0, (area.Height - formatted.Height) / 2) : area.Y;
        if (item.HasOutline || item.TextShadow) DrawTextEffects(dc, item, formatted.BuildGeometry(new Point(area.X, y)));
        dc.PushClip(new RectangleGeometry(item.Bounds));
        dc.DrawText(formatted, new Point(area.X, y));
        dc.Pop();
    }

    /// <summary>
    /// The outline and the shadow of the letters, drawn before the letters themselves. Both are
    /// plain strokes of the letter shapes, so they stay sharp at any zoom and cost no blur: the
    /// outline is a round pen twice its width (the inner half ends up under the letters), the
    /// shadow a faint wide copy under a darker tight one, which reads as a soft edge.
    /// </summary>
    private static void DrawTextEffects(DrawingContext dc, TextItem item, Geometry letters)
    {
        double outline = item.HasOutline ? item.OutlineWidth : 0, reach = TextOverhang(item);
        var clip = item.Bounds;
        clip.Inflate(reach, reach);
        dc.PushClip(new RectangleGeometry(clip));
        if (item.TextShadow)
        {
            double offset = TextShadowOffset(item);
            dc.PushTransform(new TranslateTransform(offset, offset));
            // Each pass is drawn solid and faded as a whole, so a stroke never darkens the fill it overlaps.
            dc.PushOpacity(0.22);
            dc.DrawGeometry(Brushes.Black, RoundPen(Brushes.Black, (outline + offset * 0.5) * 2), letters);
            dc.Pop();
            dc.PushOpacity(0.4);
            dc.DrawGeometry(Brushes.Black, outline > 0 ? RoundPen(Brushes.Black, outline * 2) : null, letters);
            dc.Pop();
            dc.Pop();
        }
        if (outline > 0) dc.DrawGeometry(null, RoundPen(Frozen(new SolidColorBrush(item.OutlineColor)), outline * 2), letters);
        dc.Pop();
    }

    private static Pen RoundPen(Brush brush, double thickness) => Frozen(new Pen(brush, thickness) { LineJoin = PenLineJoin.Round });

    /// <summary>How far down and right the letters' shadow sits: it grows with the letters.</summary>
    public static double TextShadowOffset(TextItem item) => Math.Max(1.5, item.FontSize * 0.06);

    /// <summary>How far the outline and the shadow of the letters can paint beyond the letters themselves.</summary>
    public static double TextOverhang(TextItem item) => (item.HasOutline ? item.OutlineWidth : 0) + (item.TextShadow ? TextShadowOffset(item) * 1.5 : 0);

    /// <summary>Share of each side an oval or cloud body keeps clear so the text stays inside the curve.</summary>
    private const double RoundBodyInset = 0.146;

    /// <summary>Where the text is laid out inside an item: the bounds less the padding, and less the curve of a round callout body.</summary>
    public static Rect TextArea(TextItem item)
    {
        var b = item.Bounds;
        double ix = item.Padding, iy = item.Padding;
        if (item is CalloutItem { Kind: CalloutKind.Oval or CalloutKind.Thought }) { ix += b.Width * RoundBodyInset; iy += b.Height * RoundBodyInset; }
        return new Rect(b.X + ix, b.Y + iy, Math.Max(0, b.Width - ix * 2), Math.Max(0, b.Height - iy * 2));
    }

    // ----- Callouts -----

    private static void DrawCallout(DrawingContext dc, CalloutItem item, bool hitTestable)
    {
        // The caller has already rotated the context about the body's centre; targets live in document space, so bring them into the body's frame.
        var points = LocalTargets(item).Where(p => !item.Bounds.Contains(p)).ToList();
        var body = CalloutBody(item, points);
        var fill = item.Background is { A: > 0 } color ? Frozen(new SolidColorBrush(color)) : null;
        var pen = item.HasStroke ? MakePen(item) : null;

        Brush lineBrush = Frozen(new SolidColorBrush(item.Stroke is { A: > 0 } stroke ? stroke : item.Color));
        double lineWidth = item.StrokeWidth > 0 ? item.StrokeWidth : 2;
        if (item.Shadow)
        {
            dc.PushTransform(new TranslateTransform(ShadowOffset.X, ShadowOffset.Y));
            if (item.Kind == CalloutKind.Label)
                foreach (var target in points) DrawArrowShape(dc, ShadowBrush, EdgePoint(item, target), target, item.HeadSize, lineWidth, item.HeadRoundness);
            if (fill != null || pen != null) dc.DrawGeometry(fill != null ? ShadowBrush : null, pen != null ? new Pen(ShadowBrush, pen.Thickness) : null, body);
            dc.Pop();
        }
        if (item.Kind == CalloutKind.Label)
        {
            foreach (var target in points)
            {
                var start = EdgePoint(item, target);
                DrawArrowShape(dc, lineBrush, start, target, item.HeadSize, lineWidth, item.HeadRoundness);
                if (hitTestable) dc.DrawLine(new Pen(Brushes.Transparent, Math.Max(lineWidth, 12)), start, target);
            }
        }
        dc.DrawGeometry(fill ?? (hitTestable ? Brushes.Transparent : null), pen, body);
        DrawTextRuns(dc, item, centred: true);
    }

    /// <summary>A callout's targets in the body's own unrotated frame.</summary>
    public static IEnumerable<Point> LocalTargets(CalloutItem item)
    {
        if (item.Rotation == 0) return item.Targets;
        var inverse = new RotateTransform(-item.Rotation, item.Center.X, item.Center.Y);
        return item.Targets.Select(inverse.Transform);
    }

    /// <summary>Where the ray from the body's centre to <paramref name="target"/> leaves the body.</summary>
    private static Point EdgePoint(CalloutItem item, Point target) => item.Center + (target - item.Center) * ExitFraction(item, target);

    /// <summary>Fraction of the centre-to-target segment that lies inside the body (1 when the target is inside).</summary>
    private static double ExitFraction(CalloutItem item, Point target)
    {
        var v = target - item.Center;
        double hw = item.Bounds.Width / 2, hh = item.Bounds.Height / 2;
        if (hw <= 0 || hh <= 0) return 1;
        double t = item.Kind is CalloutKind.Oval or CalloutKind.Thought
            ? 1 / Math.Sqrt(v.X * v.X / (hw * hw) + v.Y * v.Y / (hh * hh))
            : Math.Min(v.X == 0 ? double.PositiveInfinity : hw / Math.Abs(v.X), v.Y == 0 ? double.PositiveInfinity : hh / Math.Abs(v.Y));
        return double.IsFinite(t) ? Math.Min(1, t) : 1;
    }

    /// <summary>The outline of a callout in its own unrotated frame: the body, merged with a tail per target for the speech and thought kinds.</summary>
    public static Geometry CalloutBody(CalloutItem item, IReadOnlyList<Point> targets)
    {
        var b = item.Bounds;
        double minSide = Math.Min(b.Width, b.Height);
        double radius = Math.Min(Math.Max(0, item.CornerRadius), minSide / 2);
        Geometry body = item.Kind switch
        {
            CalloutKind.Oval => new EllipseGeometry(b),
            CalloutKind.Thought => Cloud(b),
            _ => new RectangleGeometry(b, radius, radius),
        };
        if (item.Kind == CalloutKind.Label || targets.Count == 0 || minSide <= 0) return body;

        var centre = item.Center;
        if (item.Kind == CalloutKind.Thought)
        {
            // A trail of shrinking bubbles from the cloud to the target.
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            group.Children.Add(body);
            foreach (var target in targets)
            {
                var edge = EdgePoint(item, target);
                var run = target - edge;
                double[] at = [0.22, 0.58, 0.9], size = [0.085, 0.058, 0.036];
                for (int i = 0; i < at.Length; i++)
                {
                    double r = Math.Max(2, minSide * size[i]);
                    group.Children.Add(new EllipseGeometry(edge + run * at[i], r, r));
                }
            }
            return group;
        }
        foreach (var target in targets)
        {
            var direction = target - centre;
            if (direction.Length < 0.01) continue;
            double exit = ExitFraction(item, target);
            direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            double half = Math.Max(3, minSide * 0.14);
            var root = centre + (target - centre) * (exit * 0.6);
            var figure = new PathFigure { StartPoint = root + normal * half, IsClosed = true, IsFilled = true };
            figure.Segments.Add(new LineSegment(target, true));
            figure.Segments.Add(new LineSegment(root - normal * half, true));
            body = Geometry.Combine(body, new PathGeometry([figure]), GeometryCombineMode.Union, null);
        }
        return body;
    }

    /// <summary>A cloud that fills <paramref name="b"/>: round bumps along an ellipse, merged into one outline.</summary>
    private static Geometry Cloud(Rect b)
    {
        double a = b.Width / 2, h = b.Height / 2, bump = Math.Min(a, h) * 0.5;
        if (bump <= 0) return new EllipseGeometry(b);
        var centre = new Point(b.X + a, b.Y + h);
        double ra = a - bump, rb = h - bump;
        // Ramanujan's perimeter estimate decides how many bumps keep them evenly sized on a wide cloud.
        double perimeter = Math.PI * (3 * (ra + rb) - Math.Sqrt((3 * ra + rb) * (ra + 3 * rb)));
        int count = Math.Clamp((int)Math.Round(perimeter / (bump * 1.15)), 6, 18);
        Geometry cloud = new EllipseGeometry(centre, Math.Max(1, a - bump * 0.7), Math.Max(1, h - bump * 0.7));
        for (int i = 0; i < count; i++)
        {
            double angle = Math.PI * 2 * i / count + 0.3;
            double r = bump * (i % 2 == 0 ? 1 : 0.86);
            var at = new Point(centre.X + Math.Cos(angle) * (a - r), centre.Y + Math.Sin(angle) * (h - r));
            cloud = Geometry.Combine(cloud, new EllipseGeometry(at, r, r), GeometryCombineMode.Union, null);
        }
        return cloud;
    }

    /// <summary>Builds the WPF text layout for a text item at its current width.</summary>
    public static FormattedText FormatText(TextItem item, Brush brush)
    {
        var typeface = new Typeface(new FontFamily(item.FontFamily),
            item.Italic ? FontStyles.Italic : FontStyles.Normal,
            item.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        var formatted = new FormattedText(item.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            Math.Max(1, item.FontSize), brush, 1.0)
        {
            TextAlignment = item.Alignment switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
            Trimming = TextTrimming.None,
        };
        double maxWidth = TextArea(item).Width;
        if (maxWidth > 1) formatted.MaxTextWidth = Math.Min(maxWidth, 3500000);
        if (item.Underline) formatted.SetTextDecorations(TextDecorations.Underline);
        return formatted;
    }

    /// <summary>The size the item needs to hold its text at its current width, including padding and any round-body inset.</summary>
    public static Size MeasureText(TextItem item)
    {
        var probe = string.IsNullOrEmpty(item.Text) ? item with { Text = "Ag" } : item;
        var formatted = FormatText(probe, Brushes.Black);
        double width = Math.Max(formatted.WidthIncludingTrailingWhitespace, formatted.MinWidth) + item.Padding * 2;
        double height = formatted.Height + item.Padding * 2;
        if (item is CalloutItem { Kind: CalloutKind.Oval or CalloutKind.Thought }) { width /= 1 - RoundBodyInset * 2; height /= 1 - RoundBodyInset * 2; }
        return new Size(Math.Ceiling(width), Math.Ceiling(height));
    }

    private static void DrawShadowRect(DrawingContext dc, Rect bounds) =>
        dc.DrawRectangle(ShadowBrush, null, Rect.Offset(bounds, ShadowOffset));

    private static Pen MakePen(Item item)
    {
        var pen = new Pen(Frozen(new SolidColorBrush(item.Stroke!.Value)), item.StrokeWidth)
        {
            LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static Pen HitPen(Item item) => new(Brushes.Transparent, Math.Max(item.StrokeWidth, 12)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Renders the document's content rectangle (the crop when set) to a frozen bitmap.</summary>
    public static BitmapSource Flatten(ImageDocument doc, AssetStore assets) => Flatten(doc, assets, doc.ContentRect);

    /// <summary>Renders any rectangle of document space to a frozen bitmap, banding tall regions.</summary>
    public static BitmapSource Flatten(ImageDocument doc, AssetStore assets, Rect region)
    {
        int width = Math.Max(1, (int)Math.Round(region.Width));
        int height = Math.Max(1, (int)Math.Round(region.Height));
        if (height <= BandHeight) return RenderRegion(doc, assets, region.X, region.Y, width, height);

        var result = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        int stride = width * 4;
        for (int y = 0; y < height; y += BandHeight)
        {
            int bandHeight = Math.Min(BandHeight, height - y);
            var band = RenderRegion(doc, assets, region.X, region.Y + y, width, bandHeight);
            var pixels = new byte[stride * bandHeight];
            band.CopyPixels(pixels, stride, 0);
            result.WritePixels(new Int32Rect(0, y, width, bandHeight), pixels, stride, 0);
        }
        result.Freeze();
        return result;
    }

    /// <summary>Renders only the given items (on a transparent background) cropped to their union bounds.</summary>
    public static BitmapSource FlattenItems(IEnumerable<Item> items, AssetStore assets)
    {
        var list = items.ToList();
        var bounds = DocumentOps.UnionBounds(list);
        if (bounds.IsEmpty) bounds = new Rect(0, 0, 1, 1);
        bounds.Inflate(4, 4);
        var doc = new ImageDocument { Size = new Size(bounds.Right, bounds.Bottom), Layers = [new Layer { Items = [.. list] }] };
        return Flatten(doc, assets, bounds);
    }

    private static BitmapSource RenderRegion(ImageDocument doc, AssetStore assets, double x, double y, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-x, -y));
            Render(dc, doc, assets);
            dc.Pop();
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
