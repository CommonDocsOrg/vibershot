using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Vibershot.Editing;

namespace Vibershot.UI;

/// <summary>
/// Hosts one <see cref="DrawingVisual"/> per document item under a zoom/pan transform, plus a
/// screen-space overlay the window paints selection chrome into. Hit testing goes through
/// <see cref="VisualTreeHelper"/> so clicks resolve to the top-most item under the pointer.
/// </summary>
public sealed class EditorCanvas : FrameworkElement
{
    private const double MinZoom = 0.05, MaxZoom = 32;
    private readonly VisualCollection _children;
    private readonly ContainerVisual _content = new();
    private readonly DrawingVisual _backdrop = new();
    private readonly ContainerVisual _layers = new();
    private readonly DrawingVisual _overlay = new();
    private readonly Dictionary<Guid, (Item Item, DrawingVisual Visual)> _visuals = new();
    private readonly Dictionary<DrawingVisual, Guid> _ids = new();
    private readonly HashSet<Guid> _locked = new();
    private readonly Pen _edgePen = new(new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xD3)), 1);
    private readonly Brush _cropDim = new SolidColorBrush(Color.FromArgb(0xB0, 0xFB, 0xFB, 0xFC));
    private ImageDocument? _doc;
    private AssetStore? _assets;
    private Guid? _hidden;

    public EditorCanvas()
    {
        _children = new VisualCollection(this) { _content, _overlay };
        _content.Children.Add(_backdrop);
        _content.Children.Add(_layers);
        ClipToBounds = true;
        Focusable = true;
        _edgePen.Freeze();
        _cropDim.Freeze();
    }

    public double Zoom { get; private set; } = 1;

    /// <summary>Screen position of the document origin.</summary>
    public Vector Offset { get; private set; }

    public event Action? ViewChanged;

    /// <summary>Paints selection handles, marquees and crop chrome in screen coordinates.</summary>
    public Action<DrawingContext>? OverlayPainter { get; set; }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    // A transparent fill so mouse events arrive even over empty space.
    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateOverlay();
    }

    /// <summary>Shows a new document snapshot, re-rendering only the items whose records changed.</summary>
    public void SetDocument(ImageDocument doc, AssetStore assets)
    {
        _doc = doc;
        _assets = assets;
        _locked.Clear();
        var seen = new HashSet<Guid>();
        // Item visuals are reused across snapshots, so detach them from the old layer containers first.
        foreach (var old in _layers.Children.OfType<ContainerVisual>()) old.Children.Clear();
        _layers.Children.Clear();
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) continue;
            var container = new ContainerVisual { Opacity = layer.Opacity };
            foreach (var item in layer.Items)
            {
                seen.Add(item.Id);
                if (layer.Locked) _locked.Add(item.Id);
                if (!_visuals.TryGetValue(item.Id, out var entry))
                {
                    entry = (item, new DrawingVisual());
                    RenderOptions.SetBitmapScalingMode(entry.Visual, BitmapScalingMode.HighQuality);
                    _ids[entry.Visual] = item.Id;
                    RenderItem(entry.Visual, item);
                }
                else if (!ReferenceEquals(entry.Item, item))
                {
                    RenderItem(entry.Visual, item);
                }
                entry.Visual.Opacity = _hidden == item.Id ? 0 : 1;
                _visuals[item.Id] = (item, entry.Visual);
                container.Children.Add(entry.Visual);
            }
            _layers.Children.Add(container);
        }
        foreach (var stale in _visuals.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _ids.Remove(_visuals[stale].Visual);
            _visuals.Remove(stale);
        }
        RenderBackdrop();
        InvalidateOverlay();
    }

    private void RenderItem(DrawingVisual visual, Item item)
    {
        using var dc = visual.RenderOpen();
        DocumentRenderer.RenderItem(dc, item, _assets!, hitTestable: true);
    }

    /// <summary>Hides one item's visual while a text box edits it in place.</summary>
    public void SetHidden(Guid? id)
    {
        _hidden = id;
        foreach (var (itemId, entry) in _visuals) entry.Visual.Opacity = itemId == id ? 0 : 1;
    }

    private void RenderBackdrop()
    {
        using var dc = _backdrop.RenderOpen();
        if (_doc == null) return;
        var canvas = new Rect(_doc.Size);
        if (_doc.Background.A > 0)
        {
            var brush = new SolidColorBrush(_doc.Background);
            brush.Freeze();
            dc.DrawRectangle(brush, null, canvas);
        }
    }

    public void InvalidateOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (_doc != null)
        {
            var canvas = ToScreen(new Rect(_doc.Size));
            if (_doc.CropRect is { } crop)
            {
                var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(canvas), new RectangleGeometry(ToScreen(crop)));
                dc.DrawGeometry(_cropDim, null, outside);
                dc.DrawRectangle(null, _edgePen, Snap(ToScreen(crop)));
            }
            dc.DrawRectangle(null, _edgePen, Snap(canvas));
        }
        OverlayPainter?.Invoke(dc);
    }

    private static Rect Snap(Rect r) => new(Math.Round(r.X) + 0.5, Math.Round(r.Y) + 0.5, Math.Round(r.Width), Math.Round(r.Height));

    public Point ToDoc(Point screen) => new((screen.X - Offset.X) / Zoom, (screen.Y - Offset.Y) / Zoom);
    public Point ToScreen(Point doc) => new(doc.X * Zoom + Offset.X, doc.Y * Zoom + Offset.Y);
    public Rect ToScreen(Rect doc) => new(ToScreen(doc.TopLeft), ToScreen(doc.BottomRight));
    public Rect ToDoc(Rect screen) => new(ToDoc(screen.TopLeft), ToDoc(screen.BottomRight));

    /// <summary>The document point at the middle of the viewport.</summary>
    public Point ViewCenter => ToDoc(new Point(ActualWidth / 2, ActualHeight / 2));

    public void SetView(double zoom, Vector offset)
    {
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Offset = new Vector(Math.Round(offset.X), Math.Round(offset.Y));
        _content.Transform = new MatrixTransform(Zoom, 0, 0, Zoom, Offset.X, Offset.Y);
        InvalidateOverlay();
        ViewChanged?.Invoke();
    }

    /// <summary>Changes the zoom while keeping the document point under <paramref name="screen"/> fixed.</summary>
    public void ZoomAt(Point screen, double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var anchor = ToDoc(screen);
        SetView(zoom, new Vector(screen.X - anchor.X * zoom, screen.Y - anchor.Y * zoom));
    }

    public void Pan(Vector delta) => SetView(Zoom, Offset + delta);

    /// <summary>Centres <paramref name="docRect"/> at the largest zoom that fits, capped at <paramref name="maxZoom"/>.</summary>
    public void Fit(Rect docRect, double maxZoom = double.PositiveInfinity, double padding = 24)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || docRect.Width <= 0 || docRect.Height <= 0) return;
        double zoom = Math.Min((ActualWidth - padding * 2) / docRect.Width, (ActualHeight - padding * 2) / docRect.Height);
        zoom = Math.Clamp(Math.Min(zoom, maxZoom), MinZoom, MaxZoom);
        Center(docRect, zoom);
    }

    public void Center(Rect docRect, double zoom)
    {
        var offset = new Vector((ActualWidth - docRect.Width * zoom) / 2 - docRect.X * zoom, (ActualHeight - docRect.Height * zoom) / 2 - docRect.Y * zoom);
        SetView(zoom, offset);
    }

    /// <summary>The top-most unlocked item under a screen point, or null.</summary>
    public Guid? HitItem(Point screen)
    {
        Guid? found = null;
        VisualTreeHelper.HitTest(this, null, result =>
        {
            if (result.VisualHit is DrawingVisual visual && _ids.TryGetValue(visual, out var id) && !_locked.Contains(id))
            {
                found = id;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(screen));
        return found;
    }

    public bool IsLocked(Guid id) => _locked.Contains(id);
}
