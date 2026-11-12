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
            case TextItem text: DrawText(dc, text, hitTestable); break;
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
        var geometry = ArrowGeometry(arrow);
        if (arrow.HasStroke)
        {
            var color = Frozen(new SolidColorBrush(arrow.Stroke!.Value));
            if (arrow.Shadow)
            {
                dc.PushTransform(new TranslateTransform(ShadowOffset.X, ShadowOffset.Y));
                dc.DrawGeometry(ShadowBrush, new Pen(ShadowBrush, arrow.StrokeWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
                dc.Pop();
            }
            dc.DrawGeometry(color, new Pen(color, arrow.StrokeWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }
        if (hitTestable) dc.DrawGeometry(Brushes.Transparent, HitPen(arrow), new LineGeometry(arrow.Tail, arrow.Head));
    }

    /// <summary>Shaft plus a filled triangular head at <see cref="LineItem.Head"/>.</summary>
    public static Geometry ArrowGeometry(ArrowItem arrow)
    {
        var tail = arrow.Tail;
        var head = arrow.Head;
        var direction = head - tail;
        double length = direction.Length;
        double headLength = arrow.HeadSize + arrow.StrokeWidth * 1.5;
        if (length < 0.01)
        {
            return new LineGeometry(tail, head);
        }
        direction.Normalize();
        headLength = Math.Min(headLength, length);
        var normal = new Vector(-direction.Y, direction.X);
        var baseCenter = head - direction * headLength;
        double halfWidth = headLength * 0.5;
        var shaftEnd = head - direction * (headLength * 0.75);

        var figure = new PathFigure { StartPoint = baseCenter + normal * halfWidth, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(head, true));
        figure.Segments.Add(new LineSegment(baseCenter - normal * halfWidth, true));
        var headGeometry = new PathGeometry([figure]);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(new LineGeometry(tail, shaftEnd));
        group.Children.Add(headGeometry);
        return group;
    }

    private static void DrawText(DrawingContext dc, TextItem item, bool hitTestable)
    {
        var bounds = item.Bounds;
        if (item.Shadow) DrawShadowRect(dc, bounds);
        if (item.Background is { A: > 0 } background) dc.DrawRectangle(Frozen(new SolidColorBrush(background)), null, bounds);
        else if (hitTestable) dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (item.HasStroke) dc.DrawRectangle(null, MakePen(item), bounds);

        if (string.IsNullOrEmpty(item.Text)) return;
        var formatted = FormatText(item, Frozen(new SolidColorBrush(item.Color)));
        var origin = new Point(bounds.X + item.Padding, bounds.Y + item.Padding);
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawText(formatted, origin);
        dc.Pop();
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
        double maxWidth = item.Bounds.Width - item.Padding * 2;
        if (maxWidth > 1) formatted.MaxTextWidth = maxWidth;
        if (item.Underline) formatted.SetTextDecorations(TextDecorations.Underline);
        return formatted;
    }

    /// <summary>The size the text needs at the item's current width, including padding.</summary>
    public static Size MeasureText(TextItem item)
    {
        var probe = string.IsNullOrEmpty(item.Text) ? item with { Text = "Ag" } : item;
        var formatted = FormatText(probe, Brushes.Black);
        return new Size(Math.Ceiling(Math.Max(formatted.WidthIncludingTrailingWhitespace, formatted.MinWidth) + item.Padding * 2),
            Math.Ceiling(formatted.Height + item.Padding * 2));
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
