using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Core;
using Vibershot.Editing;
using Vibershot.Editing.Video;
using Vibershot.Media;

namespace Vibershot.UI;

/// <summary>
/// The video editor's picture: every visible layer under the playhead, composited bottom to top
/// over the project canvas. Each video layer is fed by its own ffmpeg <see cref="FrameReader"/>,
/// read on a worker and shown on the UI thread; frames that arrive late are dropped, never queued.
/// The selected picture can be moved, resized, turned and cropped with handles drawn over it.
/// </summary>
public sealed class VideoPreview : FrameworkElement
{
    public const double HandleRadius = 8, RotateHandleDistance = 26, SnapPixels = 6, MinimumSize = 8;

    private enum Drag { None, Move, Resize, Rotate, Target }

    /// <summary>Drag format for something from the elements palette; the data is the element's key.</summary>
    public const string ElementFormat = "Vibershot.Element";

    private static readonly AssetStore NoAssets = new();

    private readonly Dictionary<Guid, LayerPlayer> _players = new();
    private readonly Dictionary<string, BitmapSource?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Brush _well, _rule, _ink3, _accent;
    private static readonly Brush Shade = Frozen(Color.FromArgb(0x99, 0x19, 0x1A, 0x21)), GuideBrush = Frozen(Color.FromRgb(0xFF, 0x79, 0xC6));
    private VideoProject _project = TimelineOps.Empty();
    private VideoProject? _drawn;
    private List<(Track Track, Clip Clip)> _layers = new();
    private int _dragTarget;
    private long _frame;
    private bool _playing, _cropMode;
    private Guid? _selected;

    private Drag _drag;
    private BoxEdges _dragEdges;
    private VideoProject? _dragBase;
    private Placement _dragPlacement = new();
    private Point _dragStart;
    private string _dragKey = "";
    private bool _dragMoved;
    private double? _guideX, _guideY;

    public FfmpegRunner? Runner { get; set; }
    public VideoEditorSettings Settings { get; set; } = new();

    /// <summary>Returns the finished proxy for a source path, or null to decode the original.</summary>
    public Func<string, string?>? ProxyFor { get; set; }

    /// <summary>Where handle drags are recorded. Without it the preview only shows.</summary>
    public History<VideoProject>? History { get; set; }

    /// <summary>Snap a moved picture to the canvas edges and centre.</summary>
    public bool Snap { get; set; } = true;

    /// <summary>The preview scale last used (1, 0.5 or 0.25), for the status line.</summary>
    public double CurrentScale { get; private set; } = 1;

    /// <summary>Raised when a click picks a picture (or nothing, with null).</summary>
    public event Action<Guid?>? ClipPicked;

    /// <summary>Raised when a media-bin source is dropped on the picture: source id and the canvas point.</summary>
    public event Action<Guid, Point>? SourceDropped;

    public event Action? CropModeChanged;

    /// <summary>Raised when an element from the palette is dropped on the picture: its key and the canvas point.</summary>
    public event Action<string, Point>? ElementDropped;

    /// <summary>Raised by a double-click on a text or callout graphic, or on a caption: the clip whose words should be edited.</summary>
    public event Action<Guid>? TextEditRequested;

    public VideoPreview()
    {
        ClipToBounds = true; AllowDrop = true; Focusable = true;
        // Outside the app (the test program) there are no theme resources; plain brushes stand in.
        Brush Find(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        _well = Find("Well"); _rule = Find("Rule"); _ink3 = Find("Ink3"); _accent = Find("Accent");
        SizeChanged += (_, _) => { if (Settings.PreviewQuality == PreviewQuality.Auto && Math.Abs(ScaleNow() - CurrentScale) > 1e-9) Reset(); };
        Unloaded += (_, _) => Reset();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The picture carrying the handles, if any.</summary>
    public Guid? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            if (_cropMode) { _cropMode = false; CropModeChanged?.Invoke(); }
            InvalidateVisual();
        }
    }

    /// <summary>While on, the handles cut the picture's edges away instead of resizing it.</summary>
    public bool CropMode
    {
        get => _cropMode;
        set
        {
            // Only pictures have edges to cut; graphics are resized instead.
            if (value && SelectedLayer() is not (_, Vibershot.Editing.Video.VisualClip)) value = false;
            if (_cropMode == value) { if (!value) CropModeChanged?.Invoke(); return; }
            _cropMode = value; CropModeChanged?.Invoke(); InvalidateVisual();
        }
    }

    /// <summary>Shows a project at a frame. Call on every edit, seek and playback tick.</summary>
    public void Show(VideoProject project, long frame, bool playing)
    {
        if (project.Width != _project.Width || project.Height != _project.Height || project.FrameRate != _project.FrameRate) Reset();
        _project = project; _frame = frame; _playing = playing;
        Sync();
    }

    /// <summary>Drops every decoder so the next <see cref="Show"/> starts fresh (settings changed, a proxy finished).</summary>
    public void Reset()
    {
        foreach (var player in _players.Values) player.Dispose();
        _players.Clear();
        InvalidateVisual();
    }

    // ---- Geometry ----

    private Rect CanvasRect()
    {
        double availableW = Math.Max(1, ActualWidth - 32), availableH = Math.Max(1, ActualHeight - 32);
        double scale = Math.Min(availableW / _project.Width, availableH / _project.Height);
        double w = _project.Width * scale, h = _project.Height * scale;
        return new Rect(Math.Round((ActualWidth - w) / 2), Math.Round((ActualHeight - h) / 2), Math.Round(w), Math.Round(h));
    }

    /// <summary>Screen pixels per canvas pixel.</summary>
    private double Zoom => CanvasRect().Width / Math.Max(1, _project.Width);

    /// <summary>A canvas point in this control's coordinates.</summary>
    public Point CanvasToScreen(Point canvas)
    {
        var rect = CanvasRect(); double k = rect.Width / Math.Max(1, _project.Width);
        return new Point(rect.X + canvas.X * k, rect.Y + canvas.Y * k);
    }

    public Point ScreenToCanvas(Point screen)
    {
        var rect = CanvasRect(); double k = rect.Width / Math.Max(1, _project.Width);
        return new Point((screen.X - rect.X) / k, (screen.Y - rect.Y) / k);
    }

    private Rect CanvasToScreen(Rect canvas) => new(CanvasToScreen(canvas.TopLeft), CanvasToScreen(canvas.BottomRight));

    /// <summary>A canvas rectangle in this control's coordinates (for laying an editor over a graphic).</summary>
    public Rect ScreenRect(Rect canvas) => CanvasToScreen(canvas);

    private double ScaleNow()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return VideoEditorSettings.Scale(Settings.PreviewQuality, CanvasRect().Width * dpi / Math.Max(1, _project.Width));
    }

    /// <summary>The size to decode a source at so it is sharp at its placed size and the preview scale, never above its own size.</summary>
    private (int Width, int Height) DecodeSize(MediaSource source, Placement placement)
    {
        int sw = Math.Max(2, source.Width), sh = Math.Max(2, source.Height);
        var full = placement.FullBounds;
        double factor = Math.Min(1, Math.Max(full.Width / sw, full.Height / sh) * CurrentScale);
        // A picture blown up far past the canvas gains nothing from more pixels than the canvas shows.
        factor = Math.Min(factor, Math.Max((double)VideoProject.MaxDimension / sw, (double)VideoProject.MaxDimension / sh));
        return (FrameReader.Even((int)Math.Round(sw * factor)), FrameReader.Even((int)Math.Round(sh * factor)));
    }

    // ---- Decoders ----

    private void Sync()
    {
        if (ActualWidth < 1) return;
        CurrentScale = ScaleNow();
        var visible = TimelineOps.DrawnAt(_project, _frame);
        // A layer that fills the canvas hides everything under it: do not decode what cannot be seen.
        int firstNeeded = 0;
        for (int i = visible.Count - 1; i > 0; i--)
        {
            if (visible[i].Clip is VisualClip picture && Covers(picture)) { firstNeeded = i; break; }
        }
        var layers = visible.Skip(firstNeeded).ToList();
        var wanted = layers.Select(layer => layer.Clip).OfType<VideoClip>().ToList();
        if (_playing)
        {
            // Warm up the clip that starts within the next half second so the cut does not stall.
            long horizon = _frame + Math.Max(1, _project.Rate.Rounded / 2);
            foreach (var track in _project.Tracks.Where(track => track.Kind == TrackKind.Visual && !track.Hidden))
                if (track.Clips.FirstOrDefault(clip => clip.Start > _frame && clip.Start <= horizon) is VideoClip next) wanted.Add(next);
        }
        var ids = wanted.Select(clip => clip.Id).ToHashSet();
        foreach (var gone in _players.Keys.Where(id => !ids.Contains(id)).ToList()) { _players[gone].Dispose(); _players.Remove(gone); }
        bool redraw = false;
        foreach (var clip in Runner == null ? new List<VideoClip>() : wanted)
        {
            if (_project.Source(clip.SourceId) is not { } source) continue;
            var (w, h) = DecodeSize(source, TimelineOps.PlacementOf(_project, clip));
            if (_players.TryGetValue(clip.Id, out var player))
            {
                // Replace a decoder that reads the wrong piece, or one made much too small or too
                // large by a resize (but not in the middle of the drag that is resizing it).
                bool wrongSize = _drag == Drag.None && (w > player.Width * 1.6 || w * 4 < player.Width);
                if (!player.Matches(clip) || wrongSize) { player.Dispose(); _players.Remove(clip.Id); player = null; }
            }
            if (player == null)
            {
                if (!File.Exists(source.Path)) continue;
                string path = Settings.PreviewQuality != PreviewQuality.Full && Settings.UseProxies ? ProxyFor?.Invoke(source.Path) ?? source.Path : source.Path;
                player = new LayerPlayer(Runner!, _project, clip, path, w, h, Settings.HardwareDecode);
                _players[clip.Id] = player;
            }
            // The bitmap repaints itself when its pixels change; only a layer's first frame needs a redraw.
            bool had = player.HasFrame;
            player.Advance(Math.Max(_frame, clip.Start));
            redraw |= !had && player.HasFrame;
        }
        foreach (var image in layers.Select(layer => layer.Clip).OfType<ImageClip>())
            if (_project.Source(image.SourceId) is { } source) LoadImage(source.Path);
        bool changed = !layers.Select(layer => layer.Clip.Id).SequenceEqual(_layers.Select(layer => layer.Clip.Id)) || !ReferenceEquals(_drawn, _project);
        _layers = layers; _drawn = _project;
        if (redraw || changed) InvalidateVisual();
    }

    /// <summary>True when a layer hides everything below it: an opaque, upright video over the whole canvas.</summary>
    private bool Covers(VisualClip clip)
    {
        if (clip is not VideoClip) return false;
        var p = TimelineOps.PlacementOf(_project, clip);
        return p.Opacity >= 1 && p.Rotation == 0 && p.X <= 1 && p.Y <= 1 && p.X + p.Width >= _project.Width - 1 && p.Y + p.Height >= _project.Height - 1;
    }

    /// <summary>Starts decoding a still picture off the UI thread; the layer appears when it is ready.</summary>
    private void LoadImage(string path)
    {
        if (_images.ContainsKey(path)) return;
        _images[path] = null;
        Task.Run(() =>
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return (BitmapSource?)bitmap;
            }
            catch (Exception) { return null; }
        }).ContinueWith(task => Dispatcher.BeginInvoke(() => { _images[path] = task.Result; InvalidateVisual(); }));
    }

    // ---- Drawing ----

    private BitmapSource? PictureOf(VisualClip clip)
    {
        if (clip is ImageClip) return _project.Source(clip.SourceId) is { } source ? _images.GetValueOrDefault(source.Path) : null;
        return _players.TryGetValue(clip.Id, out var player) && player.HasFrame ? player.Bitmap : null;
    }

    /// <summary>The selected picture or graphic, when it is showing at this frame. A caption has no handles: its place comes from its track.</summary>
    private (Track Track, Clip Clip)? SelectedLayer()
    {
        if (_selected is not Guid id) return null;
        foreach (var layer in _layers)
            if (layer.Clip.Id == id && layer.Clip is Vibershot.Editing.Video.VisualClip or GraphicClip) return layer;
        return null;
    }

    /// <summary>The selected caption, when it is showing at this frame.</summary>
    private (Vibershot.Editing.Video.Track Track, CaptionClip Clip)? SelectedCaption()
    {
        if (_selected is not Guid id) return null;
        foreach (var (track, clip) in _layers)
            if (clip.Id == id && clip is CaptionClip caption) return (track, caption);
        return null;
    }

    /// <summary>The box a caption is drawn in, in canvas pixels.</summary>
    private Rect CaptionBox(Vibershot.Editing.Video.Track track, CaptionClip caption) => TimelineOps.CaptionItem(_project, track, caption).Bounds;

    /// <summary>Where a caption showing at this frame is drawn, in this control's coordinates; null when it is not showing.</summary>
    public Rect? CaptionRect(Guid clipId)
    {
        foreach (var (track, clip) in _layers)
            if (clip.Id == clipId && clip is CaptionClip caption) return CanvasToScreen(CaptionBox(track, caption));
        return null;
    }

    /// <summary>A clip's box on the canvas in one shape, whether it is a picture's placement or a graphic's bounds.</summary>
    private Placement BoxOf(Clip clip) => clip switch
    {
        VisualClip picture => TimelineOps.PlacementOf(_project, picture),
        GraphicClip { Item: var item } => new Placement { X = item.Bounds.X, Y = item.Bounds.Y, Width = item.Bounds.Width, Height = item.Bounds.Height, Rotation = item.Rotation, Opacity = item.Opacity },
        _ => new Placement(),
    };

    /// <summary>
    /// Writes a dragged box back to its clip. A moved callout takes its pointers with it; a text
    /// box never gets shorter than its words, and <paramref name="fontScale"/> grows or shrinks
    /// its letters when a corner is dragged.
    /// </summary>
    private static VideoProject WithBox(VideoProject project, Clip original, Placement box, bool moved, double fontScale = 1)
    {
        if (original is Vibershot.Editing.Video.VisualClip) return TimelineOps.SetPlacement(project, original.Id, box);
        if (original is not GraphicClip { Item: var was }) return project;
        return TimelineOps.UpdateItem(project, original.Id, _ =>
        {
            Item item = was with { Bounds = box.Bounds, Rotation = box.Rotation };
            if (item is CalloutItem callout && moved)
            {
                var shift = new Vector(box.X - was.Bounds.X, box.Y - was.Bounds.Y);
                item = callout with { Targets = callout.Targets.Select(target => target + shift).ToImmutableList() };
            }
            if (item is TextItem text && !moved)
            {
                if (Math.Abs(fontScale - 1) > 1e-6) text = text with { FontSize = Math.Max(4, Math.Round(text.FontSize * fontScale, 1)), Padding = Math.Round(text.Padding * fontScale, 1), OutlineWidth = Math.Round(text.OutlineWidth * fontScale, 1) };
                double needed = DocumentRenderer.MeasureText(text).Height;
                item = text.Bounds.Height < needed ? text with { Bounds = new Rect(text.Bounds.X, text.Bounds.Y, text.Bounds.Width, needed) } : text;
            }
            return item;
        });
    }

    private static bool IsLine(Clip clip) => clip is GraphicClip { Item: LineItem };

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_well, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 40 || ActualHeight < 40) return;
        var canvas = CanvasRect();
        var background = new SolidColorBrush(_project.Background); background.Freeze();
        dc.DrawRectangle(background, new Pen(_rule, 1), canvas);
        dc.PushClip(new RectangleGeometry(canvas));
        double zoom = canvas.Width / Math.Max(1, _project.Width);
        foreach (var (track, drawn) in _layers)
        {
            // Graphics and captions are drawn by the image editor's renderer, in canvas pixels.
            Item? item = drawn switch { GraphicClip graphic => graphic.Item, CaptionClip caption => TimelineOps.CaptionItem(_project, track, caption), _ => null };
            if (item != null)
            {
                dc.PushTransform(new TranslateTransform(canvas.X, canvas.Y));
                dc.PushTransform(new ScaleTransform(zoom, zoom));
                DocumentRenderer.RenderItem(dc, item, NoAssets);
                dc.Pop(); dc.Pop();
                continue;
            }
            if (drawn is not VisualClip clip || PictureOf(clip) is not { } picture) continue;
            var p = TimelineOps.PlacementOf(_project, clip);
            Rect box = CanvasToScreen(p.Bounds), full = CanvasToScreen(p.FullBounds);
            dc.PushTransform(new RotateTransform(p.Rotation, box.X + box.Width / 2, box.Y + box.Height / 2));
            if (_cropMode && clip.Id == _selected && p.IsCropped)
            {
                // Show what the crop cuts away, dimmed, so the edges can be dragged back out.
                dc.PushOpacity(0.3);
                dc.DrawImage(picture, full);
                dc.Pop();
            }
            dc.PushOpacity(Math.Clamp(p.Opacity, 0, 1));
            dc.PushClip(new RectangleGeometry(box));
            dc.DrawImage(picture, full);
            dc.Pop(); dc.Pop(); dc.Pop();
        }
        dc.Pop();
        if (_project.Duration == 0)
        {
            var text = new FormattedText("Import media to start", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, _ink3, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(canvas.X + (canvas.Width - text.Width) / 2, canvas.Y + (canvas.Height - text.Height) / 2));
        }
        DrawSelection(dc, canvas);
    }

    private void DrawSelection(DrawingContext dc, Rect canvas)
    {
        if (SelectedCaption() is var (lane, caption))
        {
            // A caption is only outlined: the track's style places it, so there is nothing to drag.
            var box = CanvasToScreen(CaptionBox(lane, caption));
            dc.DrawRectangle(null, new Pen(_accent, 1.5), new Rect(Math.Round(box.X) - 0.5, Math.Round(box.Y) - 0.5, Math.Round(box.Width) + 1, Math.Round(box.Height) + 1));
            return;
        }
        if (SelectedLayer() is not var (track, clip)) return;
        var p = BoxOf(clip);
        var guide = new Pen(GuideBrush, 1) { DashStyle = DashStyles.Dash };
        if (_guideX is { } gx) { double x = Math.Round(CanvasToScreen(new Point(gx, 0)).X) + 0.5; dc.DrawLine(guide, new Point(x, canvas.Top), new Point(x, canvas.Bottom)); }
        if (_guideY is { } gy) { double y = Math.Round(CanvasToScreen(new Point(0, gy)).Y) + 0.5; dc.DrawLine(guide, new Point(canvas.Left, y), new Point(canvas.Right, y)); }
        var corners = Corners(p);
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(corners[0], false, true);
            g.PolyLineTo(new[] { corners[1], corners[2], corners[3] }, true, false);
        }
        outline.Freeze();
        dc.DrawGeometry(null, new Pen(_accent, 1.5) { DashStyle = _cropMode ? DashStyles.Dash : DashStyles.Solid }, outline);
        if (track.Locked) return;
        var pen = new Pen(_accent, 1.5);
        // A callout's pointer tips can be dragged on their own.
        foreach (var tip in TargetPoints(clip)) dc.DrawEllipse(_accent, new Pen(Brushes.White, 1.5), tip, 5.5, 5.5);
        foreach (var (edges, point) in Handles(p))
        {
            if (edges == BoxEdges.None)
            {
                dc.DrawLine(pen, point, Mid(corners[0], corners[1]));
                dc.DrawEllipse(Brushes.White, pen, point, 5, 5);
            }
            else if (_cropMode)
            {
                // Crop handles are solid bars so the mode is unmistakable.
                dc.DrawRectangle(_accent, new Pen(Brushes.White, 1), new Rect(point.X - 5, point.Y - 5, 10, 10));
            }
            else dc.DrawRectangle(Brushes.White, pen, new Rect(point.X - 4, point.Y - 4, 8, 8));
        }
        if (_cropMode)
        {
            var hint = new FormattedText("Crop: drag the edges, Enter or Esc to finish", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var at = new Point(canvas.X + 10, canvas.Y + 10);
            dc.DrawRoundedRectangle(Shade, null, new Rect(at.X - 6, at.Y - 3, hint.Width + 12, hint.Height + 6), 4, 4);
            dc.DrawText(hint, at);
        }
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>The box's corners on screen, clockwise from top left, turned with the picture.</summary>
    private Point[] Corners(Placement p)
    {
        var b = p.Bounds;
        var rotate = new RotateTransform(p.Rotation, p.Center.X, p.Center.Y);
        return new[] { b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft }.Select(point => CanvasToScreen(rotate.Transform(point))).ToArray();
    }

    /// <summary>Handle positions on screen: eight resize handles, then the rotate handle (edges None) unless cropping.</summary>
    private List<(BoxEdges Edges, Point Screen)> Handles(Placement p)
    {
        var c = Corners(p);
        var list = new List<(BoxEdges, Point)>
        {
            (BoxEdges.Left | BoxEdges.Top, c[0]), (BoxEdges.Top, Mid(c[0], c[1])), (BoxEdges.Right | BoxEdges.Top, c[1]), (BoxEdges.Right, Mid(c[1], c[2])),
            (BoxEdges.Right | BoxEdges.Bottom, c[2]), (BoxEdges.Bottom, Mid(c[2], c[3])), (BoxEdges.Left | BoxEdges.Bottom, c[3]), (BoxEdges.Left, Mid(c[3], c[0])),
        };
        if (!_cropMode)
        {
            // The rotate handle floats above the top edge, along the box's own up direction.
            var top = Mid(c[0], c[1]); var centre = CanvasToScreen(p.Center);
            var up = top - centre;
            if (up.Length < 0.01) up = new Vector(0, -1);
            up.Normalize();
            list.Add((BoxEdges.None, top + up * RotateHandleDistance));
        }
        return list;
    }

    /// <summary>Where a handle of the selected picture is on screen (the rotate handle for None), for tests and tooling.</summary>
    public Point? HandlePoint(BoxEdges edges)
    {
        if (SelectedLayer() is not var (_, clip)) return null;
        foreach (var (candidate, point) in Handles(BoxOf(clip)))
            if (candidate == edges) return point;
        return null;
    }

    /// <summary>The screen positions of the selected callout's pointer tips.</summary>
    private List<Point> TargetPoints(Clip clip) =>
        clip is GraphicClip { Item: CalloutItem callout } ? callout.Targets.Select(CanvasToScreen).ToList() : new List<Point>();

    /// <summary>True when a canvas point is on the clip. A line is given some thickness to be clickable.</summary>
    private bool Hit(Clip clip, Point canvas)
    {
        var box = BoxOf(clip);
        if (IsLine(clip) || clip is GraphicClip)
        {
            double slack = (IsLine(clip) ? 8 : 2) / Math.Max(1e-6, Zoom);
            var grown = box.Bounds; grown.Inflate(slack, slack);
            box = box.WithBounds(grown);
        }
        return Inside(box, canvas);
    }

    private static bool Inside(Placement p, Point canvas) =>
        p.Bounds.Contains(new RotateTransform(-p.Rotation, p.Center.X, p.Center.Y).Transform(canvas));

    // ---- Mouse ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Take the keyboard from a number box so Delete and the other shortcuts act on the picture.
        Focus();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2 && PointerDoubleClick(p)) return;
        if (PointerDown(p, Keyboard.Modifiers)) CaptureMouse();
    }

    /// <summary>
    /// The second press of a double-click at a point in this control (the first has already
    /// selected what is there). On a picture it switches cropping; on words, a caption's included,
    /// it asks for them to be edited. True when the selected clip was under the point.
    /// </summary>
    public bool PointerDoubleClick(Point screen)
    {
        var canvas = ScreenToCanvas(screen);
        if (SelectedCaption() is var (lane, caption))
        {
            if (lane.Locked || !CaptionBox(lane, caption).Contains(canvas)) return false;
            TextEditRequested?.Invoke(caption.Id);
            return true;
        }
        if (SelectedLayer() is not var (track, clip) || track.Locked || !Hit(clip, canvas)) return false;
        // A second click on a picture crops it; on words it edits them.
        if (clip is Vibershot.Editing.Video.VisualClip) CropMode = !CropMode;
        else if (clip is GraphicClip { Item: TextItem }) TextEditRequested?.Invoke(clip.Id);
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e) => PointerMove(e.GetPosition(this), Keyboard.Modifiers);

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        PointerUp();
    }

    /// <summary>
    /// A press at a point in this control. The mouse handlers forward here; it is public so the
    /// gesture logic can be driven without a real pointer. True when a drag began.
    /// </summary>
    public bool PointerDown(Point screen, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (History == null) return false;
        var canvas = ScreenToCanvas(screen);
        if (SelectedLayer() is var (track, selected) && !track.Locked)
        {
            var placement = BoxOf(selected);
            var tips = TargetPoints(selected);
            for (int i = 0; i < tips.Count; i++)
            {
                if ((screen - tips[i]).Length > HandleRadius) continue;
                Begin(Drag.Target, BoxEdges.None, placement, canvas);
                _dragTarget = i;
                return true;
            }
            foreach (var (edges, point) in Handles(placement))
            {
                if ((screen - point).Length > HandleRadius) continue;
                Begin(edges == BoxEdges.None ? Drag.Rotate : Drag.Resize, edges, placement, canvas);
                return true;
            }
        }
        // The topmost thing under the pointer takes the click. Captions sit above everything, and
        // take it only inside their drawn box; they are selected but never dragged.
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var (owner, clip) = _layers[i];
            if (clip is CaptionClip caption)
            {
                if (!CaptionBox(owner, caption).Contains(canvas)) continue;
                if (_selected != clip.Id) { Selected = clip.Id; ClipPicked?.Invoke(clip.Id); }
                return false;
            }
            if (clip is not (Vibershot.Editing.Video.VisualClip or GraphicClip) || !Hit(clip, canvas)) continue;
            var placement = BoxOf(clip);
            if (_selected != clip.Id) { Selected = clip.Id; ClipPicked?.Invoke(clip.Id); }
            if (owner.Locked) return false;
            // In crop mode the picture stays put: only its edges move.
            if (_cropMode) return false;
            Begin(Drag.Move, BoxEdges.None, placement, canvas);
            return true;
        }
        if (_selected != null) { Selected = null; ClipPicked?.Invoke(null); }
        return false;
    }

    private void Begin(Drag drag, BoxEdges edges, Placement placement, Point canvas)
    {
        _drag = drag; _dragEdges = edges; _dragPlacement = placement; _dragStart = canvas;
        _dragBase = _project; _dragKey = "place:" + Guid.NewGuid(); _dragMoved = false;
    }

    /// <summary>The pointer moved to a point in this control (during a drag, or hovering).</summary>
    public void PointerMove(Point screen, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (_drag == Drag.None) { UpdateCursor(screen); return; }
        if (History == null || _dragBase == null || _selected is not Guid id) return;
        var canvas = ScreenToCanvas(screen);
        if (!_dragMoved && (canvas - _dragStart).Length * Zoom < 3) return;
        _dragMoved = true;
        bool shift = modifiers.HasFlag(ModifierKeys.Shift), alt = modifiers.HasFlag(ModifierKeys.Alt);
        var p = _dragPlacement;
        if (TimelineOps.Find(_dragBase, id) is not var (_, original)) return;
        if (_drag == Drag.Target)
        {
            // One pointer tip of a callout follows the mouse; the body stays where it is.
            int index = _dragTarget;
            var tip = new Point(Math.Round(canvas.X), Math.Round(canvas.Y));
            History.Push(TimelineOps.UpdateItem(_dragBase, id, item => item is CalloutItem callout && index < callout.Targets.Count ? callout with { Targets = callout.Targets.SetItem(index, tip) } : item), _dragKey);
            InvalidateVisual();
            return;
        }
        double fontScale = 1;
        Placement result;
        _guideX = _guideY = null;
        switch (_drag)
        {
            case Drag.Move:
                var delta = canvas - _dragStart;
                // Shift keeps the move on one axis.
                bool holdX = false, holdY = false;
                if (shift) { if (Math.Abs(delta.X) >= Math.Abs(delta.Y)) { delta.Y = 0; holdY = true; } else { delta.X = 0; holdX = true; } }
                double x = p.X + delta.X, y = p.Y + delta.Y;
                if (Snap && !alt)
                {
                    double reach = SnapPixels / Math.Max(1e-6, Zoom);
                    // Centre to canvas centre always; edges to canvas edges while the box is upright.
                    var xs = new List<(double At, double Guide)> { (_project.Width / 2.0 - p.Width / 2, _project.Width / 2.0) };
                    var ys = new List<(double At, double Guide)> { (_project.Height / 2.0 - p.Height / 2, _project.Height / 2.0) };
                    if (p.Rotation == 0)
                    {
                        xs.Add((0, 0)); xs.Add((_project.Width - p.Width, _project.Width));
                        ys.Add((0, 0)); ys.Add((_project.Height - p.Height, _project.Height));
                    }
                    if (!holdX) foreach (var (at, line) in xs) if (Math.Abs(x - at) <= reach) { x = at; _guideX = line; break; }
                    if (!holdY) foreach (var (at, line) in ys) if (Math.Abs(y - at) <= reach) { y = at; _guideY = line; break; }
                }
                result = p with { X = Math.Round(x), Y = Math.Round(y) };
                break;
            case Drag.Rotate:
                double angle = HandleMath.AngleTo(CanvasToScreen(p.Center), screen, shift);
                // Without Shift, the picture still settles on the quarter turns.
                if (!shift && !alt) { double quarter = Math.Round(angle / 90) * 90; if (Math.Abs(angle - quarter) <= 2) angle = quarter == -180 ? 180 : quarter; }
                result = p with { Rotation = Math.Round(angle, 1) };
                break;
            case Drag.Resize when _cropMode:
                result = CropTo(p, canvas);
                break;
            default:
                bool corner = (_dragEdges & (BoxEdges.Left | BoxEdges.Right)) != 0 && (_dragEdges & (BoxEdges.Top | BoxEdges.Bottom)) != 0;
                // Corners keep the picture's shape unless Shift is held; side handles stretch it.
                bool flat = IsLine(original);
                var bounds = HandleMath.ResizeRotated(p.Bounds, p.Rotation, _dragEdges, _dragStart, canvas, corner && !shift && !flat);
                // A line may be flat or upright; everything else keeps some size.
                if (!flat && (bounds.Width < MinimumSize || bounds.Height < MinimumSize)) return;
                // Dragging a corner of a text box scales its letters with it.
                if (corner && !shift && original is GraphicClip { Item: TextItem } && p.Width > 0) fontScale = bounds.Width / p.Width;
                if (p.Rotation == 0) bounds = new Rect(Math.Round(bounds.X), Math.Round(bounds.Y), Math.Max(flat ? 0 : MinimumSize, Math.Round(bounds.Width)), Math.Max(flat ? 0 : MinimumSize, Math.Round(bounds.Height)));
                result = p.WithBounds(bounds);
                break;
        }
        History.Push(WithBox(_dragBase, original, result, _drag == Drag.Move, fontScale), _dragKey);
        InvalidateVisual();
    }

    /// <summary>
    /// Moves the dragged edges of the box inside the whole picture. The picture itself stays
    /// where it is on screen; only the part shown changes.
    /// </summary>
    private Placement CropTo(Placement p, Point canvas)
    {
        var full = p.FullBounds;
        var c = p.Center;
        var inverse = new RotateTransform(-p.Rotation, c.X, c.Y);
        var local = HandleMath.Resize(p.Bounds, _dragEdges, inverse.Transform(canvas) - inverse.Transform(_dragStart), false);
        local.Intersect(full);
        if (local.IsEmpty || local.Width < MinimumSize || local.Height < MinimumSize) return p;
        var centre = p.Rotation == 0 ? new Point(local.X + local.Width / 2, local.Y + local.Height / 2)
            : new RotateTransform(p.Rotation, c.X, c.Y).Transform(new Point(local.X + local.Width / 2, local.Y + local.Height / 2));
        return p with
        {
            X = centre.X - local.Width / 2, Y = centre.Y - local.Height / 2, Width = local.Width, Height = local.Height,
            CropLeft = Fraction((local.Left - full.Left) / full.Width), CropTop = Fraction((local.Top - full.Top) / full.Height),
            CropRight = Fraction((full.Right - local.Right) / full.Width), CropBottom = Fraction((full.Bottom - local.Bottom) / full.Height),
        };
    }

    private static double Fraction(double value) => value < 0.0005 ? 0 : Math.Round(Math.Clamp(value, 0, 0.99), 4);

    /// <summary>The press ended: finishes the drag as one undo step.</summary>
    public void PointerUp()
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None; _dragBase = null; _guideX = _guideY = null;
        History?.BreakCoalescing();
        InvalidateVisual();
    }

    private void UpdateCursor(Point screen)
    {
        if (SelectedLayer() is var (track, clip) && !track.Locked)
        {
            var placement = BoxOf(clip);
            foreach (var tip in TargetPoints(clip)) if ((screen - tip).Length <= HandleRadius) { Cursor = Cursors.Cross; return; }
            foreach (var (edges, point) in Handles(placement))
            {
                if ((screen - point).Length > HandleRadius) continue;
                Cursor = edges == BoxEdges.None ? Cursors.Hand : HandleCursor(edges, placement.Rotation);
                return;
            }
            if (!_cropMode && Hit(clip, ScreenToCanvas(screen))) { Cursor = Cursors.SizeAll; return; }
        }
        Cursor = Cursors.Arrow;
    }

    /// <summary>The resize arrow that best matches a handle once the box is turned.</summary>
    private static Cursor HandleCursor(BoxEdges edges, double rotation)
    {
        double angle = edges switch
        {
            BoxEdges.Top or BoxEdges.Bottom => 90,
            BoxEdges.Left or BoxEdges.Right => 0,
            (BoxEdges.Left | BoxEdges.Top) or (BoxEdges.Right | BoxEdges.Bottom) => 45,
            _ => 135,
        };
        int step = (int)Math.Round(((angle + rotation) % 180 + 180) % 180 / 45) % 4;
        return step switch { 0 => Cursors.SizeWE, 1 => Cursors.SizeNWSE, 2 => Cursors.SizeNS, _ => Cursors.SizeNESW };
    }

    // ---- Drops from the media bin ----

    protected override void OnDragOver(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TimelineView.SourceFormat) && !e.Data.GetDataPresent(ElementFormat)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        var at = ScreenToCanvas(e.GetPosition(this));
        if (e.Data.GetData(ElementFormat) is string element) { ElementDropped?.Invoke(element, at); e.Handled = true; return; }
        if (e.Data.GetData(TimelineView.SourceFormat) is not string text || !Guid.TryParse(text, out var sourceId)) return;
        SourceDropped?.Invoke(sourceId, at);
        e.Handled = true;
    }

    /// <summary>One clip's decoder and the bitmap its frames land in.</summary>
    private sealed class LayerPlayer : IDisposable
    {
        private readonly FfmpegRunner _runner;
        private readonly VideoProject _project;
        private readonly VideoClip _clip;
        private readonly string _path;
        private readonly bool _hardware;
        private readonly System.Collections.Concurrent.ConcurrentBag<byte[]> _pool = new();
        private Channel<(long Frame, byte[] Pixels)>? _frames;
        private CancellationTokenSource? _cancel;
        private FrameReader? _reader;
        private long _startedAt, _shown = -1;
        private DateTime _startedTime;
        private bool _gotFirst;

        public LayerPlayer(FfmpegRunner runner, VideoProject project, VideoClip clip, string path, int width, int height, bool hardware)
        {
            _runner = runner; _project = project; _clip = clip; _path = path; _hardware = hardware;
            Width = width; Height = height;
            Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        }

        public int Width { get; }
        public int Height { get; }
        public WriteableBitmap? Bitmap { get; }
        public bool HasFrame => _shown >= 0;

        /// <summary>True while this player still decodes the right piece of the right file.</summary>
        public bool Matches(VideoClip clip) =>
            clip.SourceId == _clip.SourceId && clip.End == _clip.End && clip.Start - _project.Frames(clip.SourceIn) == _clip.Start - _project.Frames(_clip.SourceIn);

        /// <summary>Brings the bitmap to <paramref name="frame"/> if that frame has been decoded. True when the picture changed.</summary>
        public bool Advance(long frame)
        {
            bool restart = _frames == null || frame < _shown || frame > Math.Max(_shown, _startedAt) + _project.Rate.Rounded * 2L;
            if (restart)
            {
                // A decoder that has not produced its first frame yet is about to; give it a moment
                // before throwing it away, so fast scrubbing does not starve the picture.
                if (_frames != null && !_gotFirst && (DateTime.UtcNow - _startedTime).TotalMilliseconds < 350) return Drain(frame, onlyFirst: true);
                Start(frame);
            }
            return Drain(frame, onlyFirst: false);
        }

        private bool Drain(long frame, bool onlyFirst)
        {
            if (_frames == null || Bitmap == null) return false;
            byte[]? latest = null; long latestFrame = -1;
            while (_frames.Reader.TryPeek(out var item) && (item.Frame <= frame || onlyFirst))
            {
                _frames.Reader.TryRead(out item);
                if (latest != null) _pool.Add(latest);
                latest = item.Pixels; latestFrame = item.Frame;
                _gotFirst = true;
                if (onlyFirst) break;
            }
            if (latest == null) return false;
            Bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), latest, Width * 4, 0);
            _pool.Add(latest);
            _shown = latestFrame;
            return true;
        }

        private void Start(long frame)
        {
            Stop();
            frame = Math.Clamp(frame, _clip.Start, Math.Max(_clip.Start, _clip.End - 1));
            _startedAt = frame; _shown = _shown >= 0 ? frame - 1 : -1; _gotFirst = false; _startedTime = DateTime.UtcNow;
            var cancel = _cancel = new CancellationTokenSource();
            var frames = _frames = Channel.CreateBounded<(long, byte[])>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
            FrameReader reader;
            try
            {
                reader = _reader = new FrameReader(_runner, new FrameReaderOptions
                {
                    Path = _path,
                    Start = _clip.SourceIn + _project.Seconds(frame - _clip.Start),
                    Width = Width, Height = Height, FrameRate = _project.Rate, HardwareDecode = _hardware,
                });
            }
            catch (Exception) { _frames = null; return; }
            long last = _clip.End;
            _ = Task.Run(async () =>
            {
                try
                {
                    for (long index = frame; index < last && !cancel.IsCancellationRequested; index++)
                    {
                        if (!_pool.TryTake(out var buffer) || buffer.Length != reader.FrameBytes) buffer = new byte[reader.FrameBytes];
                        if (!await reader.ReadFrameAsync(buffer, cancel.Token).ConfigureAwait(false)) break;
                        await frames.Writer.WriteAsync((index, buffer), cancel.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception) { }
                finally { frames.Writer.TryComplete(); reader.Dispose(); }
            });
        }

        private void Stop()
        {
            _cancel?.Cancel();
            _reader?.Dispose();
            _cancel = null; _reader = null; _frames = null;
        }

        public void Dispose() => Stop();
    }
}
