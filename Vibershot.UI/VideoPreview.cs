using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Core;
using Vibershot.Editing.Video;
using Vibershot.Media;

namespace Vibershot.UI;

/// <summary>
/// The video editor's picture: every visible layer under the playhead, composited bottom to top
/// over the project canvas. Each layer is fed by its own ffmpeg <see cref="FrameReader"/>, read
/// on a worker and shown on the UI thread; frames that arrive late are dropped, never queued.
/// </summary>
public sealed class VideoPreview : FrameworkElement
{
    private readonly Dictionary<Guid, LayerPlayer> _players = new();
    private VideoProject _project = TimelineOps.Empty();
    private long _frame;
    private bool _playing;
    private int _generation;

    public FfmpegRunner? Runner { get; set; }
    public VideoEditorSettings Settings { get; set; } = new();

    /// <summary>Returns the finished proxy for a source path, or null to decode the original.</summary>
    public Func<string, string?>? ProxyFor { get; set; }

    /// <summary>The preview scale last used (1, 0.5 or 0.25), for the status line.</summary>
    public double CurrentScale { get; private set; } = 1;

    public VideoPreview()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (Settings.PreviewQuality == PreviewQuality.Auto && Math.Abs(ScaleNow() - CurrentScale) > 1e-9) Reset(); };
        Unloaded += (_, _) => Reset();
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
        _generation++;
        foreach (var player in _players.Values) player.Dispose();
        _players.Clear();
        InvalidateVisual();
    }

    private Rect CanvasRect()
    {
        double availableW = Math.Max(1, ActualWidth - 32), availableH = Math.Max(1, ActualHeight - 32);
        double scale = Math.Min(availableW / _project.Width, availableH / _project.Height);
        double w = _project.Width * scale, h = _project.Height * scale;
        return new Rect(Math.Round((ActualWidth - w) / 2), Math.Round((ActualHeight - h) / 2), Math.Round(w), Math.Round(h));
    }

    private double ScaleNow()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return VideoEditorSettings.Scale(Settings.PreviewQuality, CanvasRect().Width * dpi / Math.Max(1, _project.Width));
    }

    private void Sync()
    {
        if (Runner == null || ActualWidth < 1) { InvalidateVisual(); return; }
        CurrentScale = ScaleNow();
        var visible = TimelineOps.VisibleAt(_project, _frame);
        // A layer that fills the canvas hides everything under it: do not decode what cannot be seen.
        int firstNeeded = 0;
        for (int i = visible.Count - 1; i > 0; i--)
        {
            if (_project.Source(visible[i].SourceId) is { } source && Covers(source)) { firstNeeded = i; break; }
        }
        var wanted = visible.Skip(firstNeeded).ToList();
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
        foreach (var clip in wanted)
        {
            if (_project.Source(clip.SourceId) is not { } source) continue;
            if (_players.TryGetValue(clip.Id, out var player) && !player.Matches(clip)) { player.Dispose(); _players.Remove(clip.Id); player = null; }
            if (player == null)
            {
                if (!File.Exists(source.Path)) continue;
                string path = Settings.PreviewQuality != PreviewQuality.Full && Settings.UseProxies ? ProxyFor?.Invoke(source.Path) ?? source.Path : source.Path;
                var (w, h) = FrameReader.Fit(source.Width, source.Height, Math.Max(2, (int)(_project.Width * CurrentScale)), Math.Max(2, (int)(_project.Height * CurrentScale)));
                player = new LayerPlayer(Runner, _project, clip, path, w, h, Settings.HardwareDecode);
                _players[clip.Id] = player;
            }
            // The bitmap repaints itself when its pixels change; only a layer's first frame needs a redraw.
            bool had = player.HasFrame;
            player.Advance(Math.Max(_frame, clip.Start));
            redraw |= !had && player.HasFrame;
        }
        var order = visible.Skip(firstNeeded).Select(clip => clip.Id).ToList();
        if (redraw || !order.SequenceEqual(_order) || !ReferenceEquals(_drawn, _project)) InvalidateVisual();
        _order = order; _drawn = _project;
    }

    private VideoProject? _drawn;

    private List<Guid> _order = new();

    private bool Covers(MediaSource source) =>
        source.Width > 0 && source.Height > 0 && Math.Abs((double)source.Width / source.Height - (double)_project.Width / _project.Height) < 0.01;

    protected override void OnRender(DrawingContext dc)
    {
        var well = (Brush)Application.Current.FindResource("Well");
        dc.DrawRectangle(well, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 40 || ActualHeight < 40) return;
        var canvas = CanvasRect();
        var background = new SolidColorBrush(_project.Background); background.Freeze();
        dc.DrawRectangle(background, new Pen((Brush)Application.Current.FindResource("Rule"), 1), canvas);
        dc.PushClip(new RectangleGeometry(canvas));
        foreach (var id in _order)
        {
            if (!_players.TryGetValue(id, out var player) || player.Bitmap == null || !player.HasFrame) continue;
            // Fit the source inside the canvas, centred, the same way the export does.
            double scale = Math.Min(canvas.Width / player.Width, canvas.Height / player.Height);
            double w = player.Width * scale, h = player.Height * scale;
            dc.DrawImage(player.Bitmap, new Rect(canvas.X + (canvas.Width - w) / 2, canvas.Y + (canvas.Height - h) / 2, w, h));
        }
        dc.Pop();
        if (_project.Duration == 0)
        {
            var text = new FormattedText("Import an MP4 to start", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, (Brush)Application.Current.FindResource("Ink3"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(canvas.X + (canvas.Width - text.Width) / 2, canvas.Y + (canvas.Height - text.Height) / 2));
        }
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
