using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Editing;
using Vibershot.Editing.Video;

namespace Vibershot.UI;

public enum TimelineTool { Select, Razor }

/// <summary>Small pictures along a source, one every <see cref="Interval"/> seconds, drawn inside its clips.</summary>
public sealed record Filmstrip(double Interval, IReadOnlyList<BitmapSource> Frames);

/// <summary>
/// The video editor's timeline: a ruler, one lane per track (video layers top-down, then audio),
/// clips that can be selected, dragged along and between lanes, trimmed at either end and cut
/// with the razor, and the playhead. Every edit goes through <see cref="TimelineOps"/> onto the
/// shared history, so a whole drag is one undo step.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    public const double HeaderWidth = 148, RulerHeight = 24, VisualHeight = 52, AudioHeight = 40, CaptionHeight = 34, EdgeGrip = 7, SnapPixels = 8;
    public const string SourceFormat = "Vibershot.VideoSource";

    private enum Drag { None, Scrub, Move, TrimStart, TrimEnd }

    private readonly Brush _well, _paper, _rule, _hover, _ink, _ink2, _ink3, _accent;
    private static readonly Brush VideoFill = Frozen("#3D4A63"), VideoEdge = Frozen("#2B3447"), AudioFill = Frozen("#1F4D3A"), AudioWave = Frozen("#50FA7B"),
        ImageFill = Frozen("#5B4A7A"), GraphicFill = Frozen("#7A4660"), CaptionFill = Frozen("#6E5A24"), PlayheadBrush = Frozen("#FF79C6"), LabelBack = Frozen("#CC191A21"), LaneAlt = Frozen("#252631");
    private readonly Typeface _face = new("Segoe UI"), _mono = new("Cascadia Mono, Consolas");

    private History<VideoProject>? _history;
    private long _playhead;
    private double _pixelsPerSecond = 60, _scroll;
    private Drag _drag;
    private VideoProject? _dragBase;
    private string _dragKey = "";
    private Guid _dragClip;
    private long _dragClipStart, _dragOffsetFrames;
    private double _dragStartX;
    private bool _dragMoved;
    private long? _snapLine;
    private ModifierKeys _modifiers;

    public TimelineView()
    {
        Focusable = true; ClipToBounds = true; AllowDrop = true;
        // Outside the app (the test program) there are no theme resources; plain brushes stand in.
        Brush Find(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        _well = Find("Well"); _paper = Find("Paper"); _rule = Find("Rule"); _hover = Find("Hover"); _ink = Find("Ink"); _ink2 = Find("Ink2"); _ink3 = Find("Ink3"); _accent = Find("Accent");
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public HashSet<Guid> Selection { get; } = new();
    public TimelineTool Tool { get; set; }
    public bool Snap { get; set; } = true;
    public Func<Guid, Filmstrip?>? FilmstripFor { get; set; }

    /// <summary>The loudness outline of a source's sound, drawn inside its audio clips.</summary>
    public Func<Guid, Vibershot.Media.Waveform?>? WaveformFor { get; set; }

    /// <summary>Raised when the user moves the playhead (ruler click, scrub).</summary>
    public event Action<long>? PlayheadMoved;
    public event Action? SelectionChanged;

    /// <summary>Raised when zoom, scroll or content length changes, so the scroll bar can follow.</summary>
    public event Action? ViewChanged;

    /// <summary>Raised when a media-bin source is dropped: source id, track under the pointer (or null) and frame.</summary>
    public event Action<Guid, Guid?, long>? SourceDropped;

    /// <summary>Raised when an element from the palette is dropped: its key, the track under the pointer (or null) and the frame.</summary>
    public event Action<string, Guid?, long>? ElementDropped;

    /// <summary>Raised when the lane menu asks for a caption: the caption track (or null for the first) and the frame.</summary>
    public event Action<Guid?, long>? CaptionRequested;

    private static double HeightOf(TrackKind kind) => kind == TrackKind.Visual ? VisualHeight : kind == TrackKind.Caption ? CaptionHeight : AudioHeight;

    /// <summary>What a clip is called on its bar and in the properties panel: its file, its words or its shape.</summary>
    public static string NameOf(VideoProject project, Clip clip) => clip switch
    {
        CaptionClip caption => caption.Text.Length > 0 ? caption.Text.Replace("\n", " ") : "Caption",
        GraphicClip { Item: CalloutItem callout } => callout.Text.Length > 0 ? callout.Text.Replace("\n", " ") : "Callout",
        GraphicClip { Item: TextItem text } => text.Text.Length > 0 ? text.Text.Replace("\n", " ") : "Text",
        GraphicClip { Item: RectItem } => "Rectangle",
        GraphicClip { Item: EllipseItem } => "Ellipse",
        GraphicClip { Item: ArrowItem } => "Arrow",
        GraphicClip { Item: LineItem } => "Line",
        GraphicClip { Item: StampItem stamp } => StampLibrary.Find(stamp.StampId)?.Name ?? "Stamp",
        GraphicClip => "Graphic",
        _ => project.Source(clip.SourceId)?.Name ?? "Missing media",
    };

    public History<VideoProject>? History
    {
        get => _history;
        set
        {
            if (_history != null) _history.Changed -= OnHistoryChanged;
            _history = value;
            if (_history != null) _history.Changed += OnHistoryChanged;
            OnHistoryChanged();
        }
    }

    private VideoProject Project => _history?.Current ?? TimelineOps.Empty();

    private void OnHistoryChanged()
    {
        var alive = Project.AllClips.Select(clip => clip.Id).ToHashSet();
        if (Selection.RemoveWhere(id => !alive.Contains(id)) > 0) SelectionChanged?.Invoke();
        InvalidateMeasure();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, DesiredContentHeight);

    public long Playhead
    {
        get => _playhead;
        set { if (_playhead != value) { _playhead = Math.Max(0, value); InvalidateVisual(); } }
    }

    public double PixelsPerSecond => _pixelsPerSecond;
    public double ScrollX
    {
        get => _scroll;
        set { double clamped = Math.Clamp(value, 0, MaxScroll); if (Math.Abs(clamped - _scroll) > 0.01) { _scroll = clamped; InvalidateVisual(); ViewChanged?.Invoke(); } }
    }

    public double LaneWidth => Math.Max(1, ActualWidth - HeaderWidth);

    /// <summary>Width of the whole timeline in pixels at the current zoom, with room to drop past the end.</summary>
    public double ContentWidth => Project.Seconds(Project.Duration) * _pixelsPerSecond + LaneWidth * 0.08;
    public double MaxScroll => Math.Max(0, ContentWidth - LaneWidth);

    public void SetZoom(double pixelsPerSecond, double? anchorX = null)
    {
        double anchor = anchorX ?? HeaderWidth + LaneWidth / 2;
        double seconds = (anchor - HeaderWidth + _scroll) / _pixelsPerSecond;
        _pixelsPerSecond = Math.Clamp(pixelsPerSecond, 2, 1200);
        _scroll = Math.Clamp(seconds * _pixelsPerSecond - (anchor - HeaderWidth), 0, MaxScroll);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Zooms so the whole project fits the lane.</summary>
    public void ZoomToFit()
    {
        double seconds = Math.Max(1, Project.Seconds(Project.Duration));
        _pixelsPerSecond = Math.Clamp(LaneWidth * 0.92 / seconds, 2, 1200);
        _scroll = 0;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Scrolls just enough to keep the playhead in view (called during playback).</summary>
    public void FollowPlayhead()
    {
        double x = FrameToX(_playhead);
        if (x > ActualWidth - 40) ScrollX = _scroll + (x - HeaderWidth) - LaneWidth * 0.1;
        else if (x < HeaderWidth) ScrollX = _scroll - (HeaderWidth - x) - LaneWidth * 0.1;
    }

    // ---- Geometry ----

    private double FrameToX(long frame) => HeaderWidth + Project.Seconds(frame) * _pixelsPerSecond - _scroll;
    private long XToFrame(double x) => Math.Max(0, Project.Frames((x - HeaderWidth + _scroll) / _pixelsPerSecond));

    private readonly record struct Row(Track Track, double Top, double Height);

    /// <summary>Lanes top to bottom: visual tracks with the top layer first, then audio tracks.</summary>
    private List<Row> Rows()
    {
        var rows = new List<Row>();
        double y = RulerHeight;
        // Captions are drawn over everything, so their lanes come first.
        foreach (var track in Project.Tracks.Where(track => track.Kind == TrackKind.Caption).Reverse()) { rows.Add(new Row(track, y, CaptionHeight)); y += CaptionHeight; }
        foreach (var track in Project.Tracks.Where(track => track.Kind == TrackKind.Visual).Reverse()) { rows.Add(new Row(track, y, VisualHeight)); y += VisualHeight; }
        foreach (var track in Project.Tracks.Where(track => track.Kind == TrackKind.Audio)) { rows.Add(new Row(track, y, AudioHeight)); y += AudioHeight; }
        return rows;
    }

    /// <summary>Height this control wants for the current tracks.</summary>
    public double DesiredContentHeight => RulerHeight + Project.Tracks.Sum(track => HeightOf(track.Kind)) + 4;

    private Rect ClipRect(Row row, Clip clip) => new(FrameToX(clip.Start), row.Top + 4, Math.Max(2, Project.Seconds(clip.Duration) * _pixelsPerSecond), row.Height - 8);

    private (Row Row, Clip Clip)? HitClip(Point p)
    {
        if (p.X < HeaderWidth) return null;
        foreach (var row in Rows())
        {
            if (p.Y < row.Top || p.Y >= row.Top + row.Height) continue;
            foreach (var clip in row.Track.Clips)
                if (ClipRect(row, clip).Contains(p)) return (row, clip);
        }
        return null;
    }

    private Row? RowAt(double y)
    {
        foreach (var row in Rows()) if (y >= row.Top && y < row.Top + row.Height) return row;
        return null;
    }

    private static Rect ToggleRect(Row row) => new(HeaderWidth - 34, row.Top + (row.Height - 22) / 2, 24, 22);

    // ---- Drawing ----

    private FormattedText Text(string text, double size, Brush brush, Typeface? face = null, double maxWidth = 0)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face ?? _face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (maxWidth > 0) formatted.MaxTextWidth = maxWidth;
        return formatted;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var project = Project;
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(_well, null, new Rect(0, 0, width, height));
        var rows = Rows();
        var rulePen = new Pen(_rule, 1);

        // Lanes and clips, clipped to the right of the track headers.
        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, LaneWidth, height)));
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i % 2 == 1) dc.DrawRectangle(LaneAlt, null, new Rect(HeaderWidth, row.Top, LaneWidth, row.Height));
            dc.DrawLine(rulePen, new Point(HeaderWidth, row.Top + row.Height - 0.5), new Point(width, row.Top + row.Height - 0.5));
            foreach (var clip in row.Track.Clips)
            {
                var rect = ClipRect(row, clip);
                if (rect.Right < HeaderWidth || rect.Left > width) continue;
                DrawClip(dc, project, row, clip, rect);
            }
        }
        DrawRuler(dc, project, width);
        if (_snapLine is { } snap)
        {
            double sx = Math.Round(FrameToX(snap)) + 0.5;
            dc.DrawLine(new Pen(_accent, 1) { DashStyle = DashStyles.Dash }, new Point(sx, RulerHeight), new Point(sx, height));
        }
        double px = Math.Round(FrameToX(_playhead)) + 0.5;
        dc.DrawLine(new Pen(PlayheadBrush, 2), new Point(px, 0), new Point(px, height));
        dc.DrawRoundedRectangle(PlayheadBrush, null, new Rect(px - 6, 0, 12, 12), 2, 2);
        dc.Pop();

        // Track headers.
        dc.DrawRectangle(_paper, null, new Rect(0, 0, HeaderWidth, height));
        dc.DrawLine(rulePen, new Point(HeaderWidth - 0.5, 0), new Point(HeaderWidth - 0.5, height));
        dc.DrawLine(rulePen, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
        foreach (var row in rows)
        {
            dc.DrawLine(rulePen, new Point(0, row.Top + row.Height - 0.5), new Point(HeaderWidth, row.Top + row.Height - 0.5));
            bool off = row.Track.Kind == TrackKind.Audio ? row.Track.Muted : row.Track.Hidden;
            string kind = row.Track.Kind == TrackKind.Visual ? "Video" : row.Track.Kind == TrackKind.Caption ? "Captions" : "Audio";
            var name = Text($"{row.Track.Name} · {kind}", 12, off ? _ink3 : _ink, maxWidth: HeaderWidth - 50);
            dc.DrawText(name, new Point(12, row.Top + (row.Height - name.Height) / 2));
            DrawToggle(dc, row, off);
        }
    }

    private void DrawRuler(DrawingContext dc, VideoProject project, double width)
    {
        double[] steps = { 1 / 30.0, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800 };
        double step = steps.FirstOrDefault(candidate => candidate * _pixelsPerSecond >= 72, 3600);
        var pen = new Pen(_rule, 1);
        double first = Math.Floor(_scroll / _pixelsPerSecond / step) * step;
        for (double t = first; ; t += step)
        {
            double x = Math.Round(HeaderWidth + t * _pixelsPerSecond - _scroll) + 0.5;
            if (x > width) break;
            if (x < HeaderWidth - 60) continue;
            dc.DrawLine(pen, new Point(x, 6), new Point(x, RulerHeight));
            int minutes = (int)(t / 60); double seconds = t - minutes * 60;
            string label = step < 1 ? $"{minutes}:{seconds.ToString("00.0#", CultureInfo.InvariantCulture)}" : $"{minutes}:{(int)Math.Round(seconds):00}";
            dc.DrawText(Text(label, 10, _ink3, _mono), new Point(x + 4, 5));
        }
    }

    private void DrawClip(DrawingContext dc, VideoProject project, Row row, Clip clip, Rect rect)
    {
        bool selected = Selection.Contains(clip.Id), video = clip is VisualClip;
        var source = project.Source(clip.SourceId);
        bool dim = clip is AudioClip { Muted: true } || (row.Track.Kind == TrackKind.Audio ? row.Track.Muted : row.Track.Hidden);
        dc.PushOpacity(dim ? 0.45 : 1);
        dc.PushClip(new RectangleGeometry(rect, 4, 4));
        dc.DrawRectangle(clip switch { ImageClip => ImageFill, GraphicClip => GraphicFill, CaptionClip => CaptionFill, AudioClip => AudioFill, _ => VideoFill }, null, rect);
        if (video && source != null && FilmstripFor?.Invoke(source.Id) is { Frames.Count: > 0 } strip)
        {
            var sample = strip.Frames[0];
            double thumbHeight = rect.Height, thumbWidth = thumbHeight * sample.PixelWidth / Math.Max(1, sample.PixelHeight);
            double from = Math.Max(rect.Left, HeaderWidth), to = Math.Min(rect.Right, ActualWidth);
            // Tiles are anchored to the clip's left edge so they stay still while scrolling.
            for (double x = rect.Left + Math.Floor((from - rect.Left) / thumbWidth) * thumbWidth; x < to; x += thumbWidth)
            {
                double seconds = clip.SourceIn + (x - rect.Left) / _pixelsPerSecond;
                int index = clip is ImageClip ? 0 : Math.Clamp((int)Math.Round(seconds / strip.Interval), 0, strip.Frames.Count - 1);
                dc.DrawImage(strip.Frames[index], new Rect(x, rect.Top, thumbWidth, thumbHeight));
                dc.DrawRectangle(VideoEdge, null, new Rect(x + thumbWidth - 1, rect.Top, 1, thumbHeight));
            }
        }
        else if (clip is AudioClip)
        {
            double middle = rect.Top + rect.Height / 2;
            dc.DrawRectangle(AudioWave, null, new Rect(rect.Left, middle - 0.5, rect.Width, 1));
            if (source != null && WaveformFor?.Invoke(source.Id) is { } wave) DrawWaveform(dc, clip, wave, rect, middle);
        }
        if (rect.Width > 28)
        {
            var label = Text(NameOf(project, clip), 11, Brushes.White, maxWidth: Math.Max(10, rect.Width - 16));
            double lx = Math.Max(rect.Left, HeaderWidth) + 6;
            dc.DrawRoundedRectangle(LabelBack, null, new Rect(lx - 3, rect.Top + 3, Math.Min(label.Width + 8, rect.Width - 6), label.Height + 2), 3, 3);
            dc.DrawText(label, new Point(lx + 1, rect.Top + 4));
        }
        dc.Pop();
        dc.Pop();
        if (selected)
        {
            dc.DrawRoundedRectangle(null, new Pen(_accent, 2), new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), rect.Height - 2), 4, 4);
            if (rect.Width > 14)
            {
                dc.DrawRoundedRectangle(_accent, null, new Rect(rect.Left, rect.Top, 4, rect.Height), 2, 2);
                dc.DrawRoundedRectangle(_accent, null, new Rect(rect.Right - 4, rect.Top, 4, rect.Height), 2, 2);
            }
        }
    }

    /// <summary>The clip's stretch of the waveform as one mirrored outline, a column every two pixels, scaled by the clip's volume.</summary>
    private void DrawWaveform(DrawingContext dc, Clip clip, Vibershot.Media.Waveform wave, Rect rect, double middle)
    {
        const double step = 2;
        double from = Math.Max(rect.Left, HeaderWidth), to = Math.Min(rect.Right, ActualWidth);
        if (to - from < step) return;
        double gain = clip is AudioClip sound ? Math.Min(sound.Volume, 2) : 1, half = rect.Height / 2 - 3;
        var heights = new List<double>();
        for (double x = from; x < to; x += step)
        {
            double seconds = clip.SourceIn + (x - rect.Left) / _pixelsPerSecond;
            // The square root lifts quiet passages so speech is visible beside loud music.
            heights.Add(Math.Min(half, Math.Sqrt(wave.PeakBetween(seconds, seconds + step / _pixelsPerSecond)) * gain * half));
        }
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(new Point(from, middle), true, true);
            for (int i = 0; i < heights.Count; i++) g.LineTo(new Point(from + i * step, middle - heights[i]), false, false);
            for (int i = heights.Count - 1; i >= 0; i--) g.LineTo(new Point(from + i * step, middle + heights[i]), false, false);
        }
        outline.Freeze();
        dc.DrawGeometry(AudioWave, null, outline);
    }

    /// <summary>An eye for a video layer, a speaker for an audio lane; struck through when off.</summary>
    private void DrawToggle(DrawingContext dc, Row row, bool off)
    {
        var rect = ToggleRect(row);
        var pen = new Pen(off ? _ink3 : _ink2, 1.5);
        var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        if (row.Track.Kind != TrackKind.Audio)
        {
            dc.DrawEllipse(null, pen, c, 8, 5);
            dc.DrawEllipse(off ? _ink3 : _ink2, null, c, 2.2, 2.2);
        }
        else
        {
            var body = new StreamGeometry();
            using (var g = body.Open())
            {
                g.BeginFigure(new Point(c.X - 8, c.Y - 3), true, true);
                g.LineTo(new Point(c.X - 4, c.Y - 3), true, true); g.LineTo(new Point(c.X + 1, c.Y - 7), true, true);
                g.LineTo(new Point(c.X + 1, c.Y + 7), true, true); g.LineTo(new Point(c.X - 4, c.Y + 3), true, true); g.LineTo(new Point(c.X - 8, c.Y + 3), true, true);
            }
            body.Freeze();
            dc.DrawGeometry(off ? _ink3 : _ink2, null, body);
            if (!off) dc.DrawLine(pen, new Point(c.X + 5, c.Y - 4), new Point(c.X + 5, c.Y + 4));
        }
        if (off) dc.DrawLine(new Pen(_ink2, 1.5), new Point(rect.X + 3, rect.Bottom - 3), new Point(rect.Right - 3, rect.Y + 3));
    }

    // ---- Selection ----

    public void Select(IEnumerable<Guid> ids)
    {
        Selection.Clear();
        foreach (var id in ids) Selection.Add(id);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    // ---- Mouse ----

    /// <summary>Snaps a frame to nearby clip edges and the playhead; sets the guide line when it does.</summary>
    private long SnapFrame(long frame, ISet<Guid>? ignore, bool includePlayhead = true)
    {
        _snapLine = null;
        if (!Snap || _modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return frame;
        var points = TimelineOps.SnapPoints(_dragBase ?? Project, ignore);
        if (includePlayhead) points.Add(_playhead);
        long best = frame; double bestDistance = SnapPixels;
        foreach (long point in points)
        {
            double distance = Math.Abs(Project.Seconds(point - frame)) * _pixelsPerSecond;
            if (distance < bestDistance) { best = point; bestDistance = distance; }
        }
        if (best != frame) _snapLine = best;
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (PointerDown(e.GetPosition(this), Keyboard.Modifiers)) CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e) => PointerMove(e.GetPosition(this));

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        PointerUp();
    }

    /// <summary>
    /// A press at a point in this control. The mouse handlers forward here; it is public so the
    /// whole gesture logic can be driven without a real pointer. True when a drag began.
    /// </summary>
    public bool PointerDown(Point p, ModifierKeys modifiers = ModifierKeys.None)
    {
        _modifiers = modifiers;
        if (_history == null) return false;
        if (p.X < HeaderWidth)
        {
            if (RowAt(p.Y) is { } header && ToggleRect(header).Contains(p))
            {
                var track = header.Track;
                _history.Push(TimelineOps.SetTrack(Project, track.Kind == TrackKind.Audio ? track with { Muted = !track.Muted } : track with { Hidden = !track.Hidden }));
            }
            return false;
        }
        if (p.Y < RulerHeight)
        {
            _drag = Drag.Scrub;
            Scrub(p.X);
            return true;
        }
        var hit = HitClip(p);
        if (hit is not var (row, clip))
        {
            if (Selection.Count > 0) Select(Array.Empty<Guid>());
            // Clicking an empty lane parks the playhead there, like the ruler.
            _drag = Drag.Scrub;
            Scrub(p.X);
            return true;
        }
        if (Tool == TimelineTool.Razor)
        {
            if (row.Track.Locked) return false;
            long cut = SnapFrame(XToFrame(p.X), null);
            _snapLine = null;
            _history.Push(TimelineOps.Split(Project, cut, new[] { clip.Id }));
            return false;
        }
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            var ids = TimelineOps.WithLinked(Project, new[] { clip.Id });
            if (Selection.Contains(clip.Id)) Select(Selection.Where(id => !ids.Contains(id)).ToList());
            else Select(Selection.Concat(ids).ToList());
            return false;
        }
        if (!Selection.Contains(clip.Id)) Select(TimelineOps.WithLinked(Project, new[] { clip.Id }));
        if (row.Track.Locked) return false;
        var rect = ClipRect(row, clip);
        _drag = p.X - rect.Left <= EdgeGrip && rect.Width > EdgeGrip * 2 ? Drag.TrimStart : rect.Right - p.X <= EdgeGrip && rect.Width > EdgeGrip * 2 ? Drag.TrimEnd : Drag.Move;
        _dragBase = Project; _dragKey = "drag:" + Guid.NewGuid(); _dragClip = clip.Id; _dragClipStart = clip.Start;
        _dragStartX = p.X; _dragOffsetFrames = XToFrame(p.X) - clip.Start; _dragMoved = false;
        return true;
    }

    private void Scrub(double x)
    {
        long frame = XToFrame(Math.Max(HeaderWidth, x));
        var project = Project;
        // The playhead sticks to clip edges while scrubbing; it never snaps to itself.
        frame = SnapFrame(frame, null, includePlayhead: false);
        _snapLine = null;
        Playhead = Math.Min(frame, Math.Max(0, project.Duration));
        PlayheadMoved?.Invoke(_playhead);
    }

    /// <summary>The pointer moved to a point in this control (during a drag, or hovering).</summary>
    public void PointerMove(Point p)
    {
        if (_drag == Drag.None) { UpdateCursor(p); return; }
        if (_drag == Drag.Scrub)
        {
            Scrub(p.X);
            // Drag past either side scrolls the lane.
            if (p.X > ActualWidth - 24) ScrollX = _scroll + 16; else if (p.X < HeaderWidth + 24) ScrollX = _scroll - 16;
            return;
        }
        if (_history == null || _dragBase == null) return;
        if (!_dragMoved && Math.Abs(p.X - _dragStartX) < 3) return;
        _dragMoved = true;
        var group = TimelineOps.WithLinked(_dragBase, new[] { _dragClip });
        if (TimelineOps.Find(_dragBase, _dragClip) is not var (fromTrack, clip)) return;
        VideoProject? result = null;
        if (_drag == Drag.TrimStart) result = TimelineOps.TrimStart(_dragBase, _dragClip, SnapFrame(XToFrame(p.X), group));
        else if (_drag == Drag.TrimEnd) result = TimelineOps.TrimEnd(_dragBase, _dragClip, SnapFrame(XToFrame(p.X), group));
        else
        {
            long desired = Math.Max(0, XToFrame(p.X) - _dragOffsetFrames);
            // Snap whichever edge of the clip is nearer to a snap point.
            long snappedStart = SnapFrame(desired, group); long? startLine = _snapLine;
            long snappedEnd = SnapFrame(desired + clip.Duration, group) - clip.Duration; long? endLine = _snapLine;
            if (startLine != null) { desired = snappedStart; _snapLine = startLine; }
            else if (endLine != null && snappedEnd >= 0) { desired = snappedEnd; _snapLine = endLine; }
            else _snapLine = null;

            var working = _dragBase;
            Guid target = fromTrack.Id;
            var rows = Rows();
            var sameKind = rows.Where(row => row.Track.Kind == fromTrack.Kind).ToList();
            if (sameKind.Count > 0)
            {
                bool above = fromTrack.Kind == TrackKind.Visual && p.Y < sameKind[0].Top;
                bool below = fromTrack.Kind == TrackKind.Audio && p.Y >= sameKind[^1].Top + sameKind[^1].Height;
                if (above || below)
                {
                    // Dragging past the outermost lane of its kind makes a new layer for the clip.
                    working = TimelineOps.AddTrack(_dragBase, fromTrack.Kind, out target);
                }
                else if (RowAt(p.Y) is { } over && over.Track.Kind == fromTrack.Kind && !over.Track.Locked) target = over.Track.Id;
            }
            long start = TimelineOps.NearestFree(working, _dragClip, target, desired);
            result = TimelineOps.Move(working, _dragClip, target, start) ?? TimelineOps.Move(_dragBase, _dragClip, fromTrack.Id, TimelineOps.NearestFree(_dragBase, _dragClip, fromTrack.Id, desired));
            if (start != desired) _snapLine = null;
        }
        if (result != null) _history.Push(result, _dragKey);
        InvalidateVisual();
    }

    /// <summary>The press ended: finishes the drag as one undo step.</summary>
    public void PointerUp()
    {
        if (_drag == Drag.None) return;
        bool edit = _drag != Drag.Scrub;
        _drag = Drag.None; _dragBase = null; _snapLine = null;
        if (edit) _history?.BreakCoalescing();
        InvalidateVisual();
    }

    private void UpdateCursor(Point p)
    {
        if (p.X < HeaderWidth || p.Y < RulerHeight) { Cursor = Cursors.Arrow; return; }
        if (HitClip(p) is not var (row, clip)) { Cursor = Cursors.Arrow; return; }
        if (Tool == TimelineTool.Razor) { Cursor = Cursors.Cross; return; }
        var rect = ClipRect(row, clip);
        bool edge = rect.Width > EdgeGrip * 2 && (p.X - rect.Left <= EdgeGrip || rect.Right - p.X <= EdgeGrip);
        Cursor = edge ? Cursors.SizeWE : Cursors.Arrow;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SetZoom(_pixelsPerSecond * (e.Delta > 0 ? 1.25 : 0.8), e.GetPosition(this).X);
        else ScrollX = _scroll - e.Delta * 0.6;
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_history == null) return;
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (p.X >= HeaderWidth && HitClip(p) is var (_, clip))
        {
            if (!Selection.Contains(clip.Id)) Select(TimelineOps.WithLinked(Project, new[] { clip.Id }));
            var ids = Selection.ToList();
            long at = _playhead;
            Add("Split at playhead", () => _history.Push(TimelineOps.Split(Project, at, ids)), clip.Start < at && at < clip.End);
            Add("Detach audio", () => _history.Push(TimelineOps.Unlink(Project, clip.Id)), clip.LinkId != null);
            menu.Items.Add(new Separator());
            Add("Delete", () => _history.Push(TimelineOps.Delete(Project, ids)));
            Add("Ripple delete", () => _history.Push(TimelineOps.Delete(Project, ids, ripple: true)));
        }
        else
        {
            long here = XToFrame(Math.Max(HeaderWidth, p.X));
            var under = RowAt(p.Y);
            Add("Add caption here", () => CaptionRequested?.Invoke(under is { Track.Kind: TrackKind.Caption } lane ? lane.Track.Id : null, here));
            menu.Items.Add(new Separator());
            Add("Add video track", () => _history.Push(TimelineOps.AddTrack(Project, TrackKind.Visual, out _)));
            Add("Add audio track", () => _history.Push(TimelineOps.AddTrack(Project, TrackKind.Audio, out _)));
            Add("Add caption track", () => _history.Push(TimelineOps.AddTrack(Project, TrackKind.Caption, out _)));
            if (under is { } row)
            {
                bool last = row.Track.Kind != TrackKind.Caption && Project.Tracks.Count(track => track.Kind == row.Track.Kind) <= 1;
                menu.Items.Add(new Separator());
                Add($"Remove {row.Track.Name}", () => _history.Push(TimelineOps.RemoveTrack(Project, row.Track.Id)), !last);
            }
        }
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---- Drops from the media bin ----

    protected override void OnDragOver(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SourceFormat) && !e.Data.GetDataPresent(VideoPreview.ElementFormat)) return;
        e.Effects = DragDropEffects.Copy;
        var p = e.GetPosition(this);
        _snapLine = Math.Max(0, XToFrame(p.X));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e) { _snapLine = null; InvalidateVisual(); }

    protected override void OnDrop(DragEventArgs e)
    {
        _snapLine = null;
        var p = e.GetPosition(this);
        if (e.Data.GetData(VideoPreview.ElementFormat) is string element)
        {
            ElementDropped?.Invoke(element, RowAt(p.Y)?.Track.Id, Math.Max(0, XToFrame(p.X)));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.Data.GetData(SourceFormat) is not string text || !Guid.TryParse(text, out var sourceId)) return;
        SourceDropped?.Invoke(sourceId, RowAt(p.Y)?.Track.Id, Math.Max(0, XToFrame(p.X)));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ViewChanged?.Invoke();
    }
}
