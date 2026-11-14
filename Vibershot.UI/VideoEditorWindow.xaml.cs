using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Vibershot.Core;
using Vibershot.Editing;
using Vibershot.Editing.Video;
using Vibershot.Media;

namespace Vibershot.UI;

/// <summary>
/// The video editor: project media on the left, the composited preview in the middle, the
/// selected clip's properties on the right and the layered timeline along the bottom. The
/// project is an immutable <see cref="VideoProject"/> on a <see cref="History{T}"/>; everything
/// on screen is redrawn from it after each change.
/// </summary>
public partial class VideoEditorWindow : Window
{
    private readonly AppConfig _config;
    private readonly History<VideoProject> _history;
    private readonly FfmpegRunner? _runner;
    private readonly ProxyCache? _proxies;
    private readonly ObservableCollection<BinItem> _bin = new();
    private readonly Dictionary<Guid, Filmstrip> _filmstrips = new();
    private readonly Dictionary<Guid, Waveform> _waveforms = new();
    private readonly WaveformCache? _waves;

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
    private const string MediaFilter = "Video and pictures (*.mp4;*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.mp4;*.png;*.jpg;*.jpeg;*.bmp;*.webp|MP4 video (*.mp4)|*.mp4|Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp";
    private readonly Stopwatch _clock = new();
    private readonly MediaPlayer _audio = new();
    private readonly DispatcherTimer _mixTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(30) };
    private string? _path, _mixKey, _pendingImport;
    private long _playhead, _playStart;
    private bool _playing, _updatingPanel, _mixOpen, _syncingScroll;
    private Point _binDown;

    public VideoEditorWindow(AppConfig config, VideoProject? project = null, string? path = null)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _config = config;
        _path = path;
        _history = new History<VideoProject>(project ?? TimelineOps.Empty());
        try { _runner = new FfmpegRunner(); _proxies = new ProxyCache(_runner); _waves = new WaveformCache(_runner); }
        catch (FfmpegNotFoundException) { }

        Bin.ItemsSource = _bin;
        Preview.Runner = _runner;
        Preview.Settings = _config.VideoEditor;
        Preview.ProxyFor = source => _proxies?.TryGet(source);
        Preview.History = _history;
        Preview.ClipPicked += id => Timeline.Select(id is Guid picked ? TimelineOps.WithLinked(Project, new[] { picked }) : new HashSet<Guid>());
        Preview.SourceDropped += OnSourceDroppedOnPicture;
        Preview.CropModeChanged += () => CropButton.IsChecked = Preview.CropMode;
        Preview.ElementDropped += (key, at) => AddElement(key, at, null, _playhead);
        Preview.TextEditRequested += BeginInlineEdit;
        Timeline.ElementDropped += (key, trackId, frame) => AddElement(key, new Point(Project.Width / 2.0, Project.Height / 2.0), trackId, frame);
        Timeline.CaptionRequested += AddCaptionAt;
        FontSizeBox.Committed += value => StyleText(text => text with { FontSize = value }, style => style with { FontSize = value });
        StrokeWidthBox.Committed += value => StyleGraphic(item => item with { StrokeWidth = value });
        TextColour.ValueChanged += colour => { if (!_updatingPanel && colour is { } c) StyleText(text => text with { Color = c }, style => style with { Color = c }); };
        // Captions left without a box get an outline, so the words still read over a busy picture.
        BackColour.ValueChanged += colour => { if (!_updatingPanel) StyleText(text => text with { Background = colour }, style => style with { Background = colour, OutlineWidth = colour == null && style.OutlineWidth <= 0 ? TimelineOps.DefaultOutlineWidth(style.FontSize) : style.OutlineWidth }); };
        // No colour switches the outline off; picking one switches it on at a width that suits the letters.
        OutlineColour.ValueChanged += colour =>
        {
            if (_updatingPanel) return;
            if (colour is not { } c) StyleText(text => text with { OutlineWidth = 0 }, style => style with { OutlineWidth = 0 });
            else StyleText(text => text with { OutlineColor = c, OutlineWidth = text.OutlineWidth > 0 ? text.OutlineWidth : TimelineOps.DefaultOutlineWidth(text.FontSize) },
                style => style with { OutlineColor = c, OutlineWidth = style.OutlineWidth > 0 ? style.OutlineWidth : TimelineOps.DefaultOutlineWidth(style.FontSize) });
        };
        OutlineWidthBox.Committed += value => StyleText(text => text with { OutlineWidth = value }, style => style with { OutlineWidth = value });
        StrokeColour.ValueChanged += colour => { if (!_updatingPanel) StyleGraphic(item => item with { Stroke = colour, StrokeWidth = colour != null && item.StrokeWidth <= 0 ? Math.Max(2, Math.Round(Project.Height / 180.0)) : item.StrokeWidth }); };
        FillColour.ValueChanged += colour => { if (!_updatingPanel) StyleGraphic(item => item is TextItem ? item : item with { Fill = colour }); };
        foreach (var box in new[] { FontSizeBox, StrokeWidthBox, OutlineWidthBox }) box.ReturnFocusTo = Timeline;
        BuildElements();
        PlaceX.Committed += value => Place(p => p with { X = value });
        PlaceY.Committed += value => Place(p => p with { Y = value });
        PlaceW.Committed += value => Place(p => p with { Width = value });
        PlaceH.Committed += value => Place(p => p with { Height = value });
        PlaceRotation.Committed += value => Place(p => p with { Rotation = value });
        foreach (var box in new[] { PlaceX, PlaceY, PlaceW, PlaceH, PlaceRotation }) box.ReturnFocusTo = Timeline;
        FadeInBox.Committed += value => SetFade(fadeIn: (long)Math.Round(value));
        FadeOutBox.Committed += value => SetFade(fadeOut: (long)Math.Round(value));
        foreach (var box in new[] { FadeInBox, FadeOutBox }) box.ReturnFocusTo = Timeline;
        Timeline.FilmstripFor = id => _filmstrips.GetValueOrDefault(id);
        Timeline.WaveformFor = id => _waveforms.GetValueOrDefault(id);
        Timeline.History = _history;
        Timeline.PlayheadMoved += frame => Seek(frame);
        Timeline.SelectionChanged += UpdatePanel;
        Timeline.ViewChanged += SyncScrollBar;
        Timeline.SourceDropped += OnSourceDropped;
        TimelineHost.SizeChanged += (_, _) => Timeline.MinHeight = Math.Max(0, TimelineHost.ActualHeight);
        _history.Changed += OnProjectChanged;
        _mixTimer.Tick += (_, _) => { _mixTimer.Stop(); RebuildMix(); };
        _autosave.Tick += (_, _) => Autosave();
        _audio.MediaOpened += (_, _) => { _mixOpen = true; if (_playing) StartAudio(); };
        _audio.MediaFailed += (_, _) => _mixOpen = false;
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnFilesDropped;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        CompositionTarget.Rendering += OnRendering;
        OnProjectChanged();
        UpdateQualityLabel();
    }

    /// <summary>Opens a <c>.vibevideo</c> project, or starts a new project from an MP4.</summary>
    public static VideoEditorWindow Open(AppConfig config, string path)
    {
        if (string.Equals(Path.GetExtension(path), VideoProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            return new VideoEditorWindow(config, VideoProjectFile.Load(path), path);
        return new VideoEditorWindow(config) { _pendingImport = path };
    }

    private VideoProject Project => _history.Current;
    private string DocumentName => _path != null ? Path.GetFileNameWithoutExtension(_path) : Project.Sources.Count > 0 ? Path.GetFileNameWithoutExtension(Project.Sources[0].Path) : "Untitled";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _autosave.Start();
        Timeline.Focus();
        if (_runner == null) ShowStatus("FFmpeg was not found, so video cannot be previewed or exported. Run tools\\get-ffmpeg.ps1.", failed: true);
        RelinkMissing();
        if (_pendingImport is { } path) { _pendingImport = null; await ImportAsync(new[] { path }); _history.MarkSaved(); UpdateTitle(); }
        Timeline.ZoomToFit();
    }

    // ---- Project changes ----

    private void OnProjectChanged()
    {
        var project = Project;
        foreach (var source in project.Sources)
        {
            if (_bin.Any(item => item.Source.Id == source.Id)) continue;
            var item = new BinItem(source);
            _bin.Add(item);
            LoadAssets(item);
        }
        BinLabel.Text = _bin.Count == 0 ? "Media" : $"Media · {_bin.Count}";
        if (InlineWords.Visibility == Visibility.Visible) PositionInlineEdit();
        if (_playhead > project.Duration) _playhead = project.Duration;
        Timeline.Playhead = _playhead;
        UndoButton.IsEnabled = _history.CanUndo; RedoButton.IsEnabled = _history.CanRedo;
        UpdateTitle();
        UpdateTime();
        UpdatePanel();
        Preview.Show(project, _playhead, _playing);
        _mixTimer.Stop(); _mixTimer.Start();
    }

    private void UpdateTitle()
    {
        var project = Project;
        DocNameLabel.Text = DocumentName + (_history.IsDirty ? " •" : "");
        DocInfoLabel.Text = $"{project.Width} × {project.Height} · {project.Rate} fps";
        Title = $"Vibershot Video — {DocumentName}";
    }

    private string Timecode(long frame)
    {
        int rate = Math.Max(1, Project.Rate.Rounded);
        long seconds = frame / rate;
        return $"{seconds / 60}:{seconds % 60:00}:{frame % rate:00}";
    }

    private void UpdateTime()
    {
        TimeLabel.Text = Timecode(_playhead);
        DurationLabel.Text = "/ " + Timecode(Project.Duration);
    }

    private void ShowStatus(string text, bool failed = false, bool ok = false)
    {
        StatusLabel.Text = text;
        StatusDot.Fill = (Brush)FindResource(failed ? "Danger" : ok ? "Ok" : "Ink3");
    }

    // ---- Media bin, proxies and filmstrips ----

    private async void LoadAssets(BinItem item)
    {
        var source = item.Source;
        if (!File.Exists(source.Path)) { item.Badge = "Missing"; return; }
        if (source.IsImage)
        {
            // A still is its own thumbnail, in the bin and repeated along its clips.
            if (await Task.Run(() => LoadPicture(source.Path, 88)) is { } still)
            {
                item.Thumb = still;
                _filmstrips[source.Id] = new Filmstrip(1, new[] { still });
                Timeline.InvalidateVisual();
            }
            return;
        }
        if (_runner == null) return;
        if (source.HasAudio && _waves != null) LoadWaveform(source);
        if (_proxies == null || !source.HasVideo) return;
        try
        {
            string? proxy = _proxies.TryGet(source.Path);
            if (proxy == null)
            {
                var info = await Ffprobe.ProbeAsync(source.Path);
                item.ProxyBusy = true;
                UpdateQualityLabel();
                ShowStatus($"Building a preview proxy for {source.Name}…");
                proxy = await _proxies.GetOrCreateAsync(info, new Progress<double>(value => item.ProxyProgress = value));
                item.ProxyBusy = false;
                ShowStatus(proxy != null ? $"Proxy ready for {source.Name}." : $"No proxy for {source.Name}; previewing from the original.", ok: proxy != null);
                // Decoders that started on the original switch over to the proxy.
                Preview.Reset();
                Preview.Show(Project, _playhead, _playing);
            }
            UpdateQualityLabel();
            var strip = await BuildFilmstripAsync(_runner, proxy ?? source.Path, source);
            if (strip.Frames.Count > 0)
            {
                _filmstrips[source.Id] = strip;
                item.Thumb = strip.Frames[Math.Min(strip.Frames.Count - 1, strip.Frames.Count / 8)];
                Timeline.InvalidateVisual();
            }
        }
        catch (Exception ex)
        {
            item.ProxyBusy = false;
            ShowStatus($"Could not prepare {source.Name}: {ex.Message}", failed: true);
        }
    }

    private async void LoadWaveform(MediaSource source)
    {
        if (await _waves!.GetOrCreateAsync(source.Path) is not { } wave) return;
        _waveforms[source.Id] = wave;
        Timeline.InvalidateVisual();
    }

    /// <summary>Decodes a picture file, scaled down to a height when one is given. Null when Windows cannot read it.</summary>
    private static BitmapSource? LoadPicture(string path, int height = 0)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (height > 0) bitmap.DecodePixelHeight = height;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception) { return null; }
    }

    private static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Decodes one small frame every few seconds for the timeline strip and the bin thumbnail.</summary>
    private static Task<Filmstrip> BuildFilmstripAsync(FfmpegRunner runner, string path, MediaSource source) => Task.Run(async () =>
    {
        int interval = Math.Max(1, (int)Math.Ceiling(source.Duration / 160));
        var (width, height) = FrameReader.Fit(Math.Max(2, source.Width), Math.Max(2, source.Height), 400, 44);
        var frames = new List<BitmapSource>();
        using var reader = new FrameReader(runner, new FrameReaderOptions { Path = path, Width = width, Height = height, FrameRate = new FrameRate(1, interval) });
        var buffer = new byte[reader.FrameBytes];
        while (frames.Count < 400 && await reader.ReadFrameAsync(buffer).ConfigureAwait(false))
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, (byte[])buffer.Clone(), width * 4);
            bitmap.Freeze();
            frames.Add(bitmap);
        }
        return new Filmstrip(interval, frames);
    });

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import media", Filter = MediaFilter, Multiselect = true };
        try { if (Directory.Exists(_config.Gallery.RecordingsFolder)) dialog.InitialDirectory = _config.Gallery.RecordingsFolder; } catch (Exception) { }
        if (dialog.ShowDialog(this) == true) _ = ImportAsync(dialog.FileNames);
    }

    /// <summary>Probes each file and places it after the last clip. The first clip of an empty project sets the canvas size and frame rate.</summary>
    private async Task ImportAsync(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            bool image = IsImagePath(path);
            if (!image && !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)) { ShowStatus($"{Path.GetFileName(path)} cannot be imported. Videos must be MP4; pictures PNG, JPEG, BMP or WebP.", failed: true); continue; }
            try
            {
                var existing = Project.Sources.FirstOrDefault(source => string.Equals(source.Path, path, StringComparison.OrdinalIgnoreCase))
                    ?? _bin.Select(item => item.Source).FirstOrDefault(source => string.Equals(source.Path, path, StringComparison.OrdinalIgnoreCase));
                MediaSource source;
                if (existing != null) source = existing;
                else if (image)
                {
                    if (await Task.Run(() => LoadPicture(path, 16)) is not { } probe) { ShowStatus($"{Path.GetFileName(path)} could not be read as a picture.", failed: true); continue; }
                    // The tiny decode keeps the aspect; the real size comes from the file's header.
                    var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    source = MediaSource.ForImage(path, frame.PixelWidth, frame.PixelHeight);
                }
                else source = MediaSource.From(await Ffprobe.ProbeAsync(path));
                if (!source.HasPicture && !source.HasAudio) { ShowStatus($"{source.Name} has no video or audio.", failed: true); continue; }
                bool first = Project.Sources.Count == 0 && Project.Duration == 0;
                // A video joins the end of the cut; a still goes over whatever is at the playhead.
                _history.Push(first ? TimelineOps.ForSource(source) : AddFromBin(source));
                if (first) Timeline.ZoomToFit();
                ShowStatus($"Imported {source.Name}.", ok: true);
            }
            catch (Exception ex) { ShowStatus($"Could not import {Path.GetFileName(path)}: {ex.Message}", failed: true); }
        }
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { _ = ImportAsync(files); e.Handled = true; }
    }

    private void Bin_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Bin.SelectedItem is BinItem item) _history.Push(AddFromBin(item.Source));
    }

    /// <summary>The default way a source joins the timeline: a video after the last clip, a still on top at the playhead.</summary>
    private VideoProject AddFromBin(MediaSource source) =>
        source.IsImage ? TimelineOps.InsertOnTop(Project, source, _playhead, out _) : TimelineOps.Append(Project, source);

    private void BinMenu_Opened(object sender, RoutedEventArgs e)
    {
        var source = (Bin.SelectedItem as BinItem)?.Source;
        BinAddEnd.IsEnabled = BinAddTop.IsEnabled = source != null;
        BinAddTop.Visibility = source is { HasPicture: true } ? Visibility.Visible : Visibility.Collapsed;
        BinAddAudio.Visibility = source is { HasAudio: true, HasVideo: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BinAddEnd_Click(object sender, RoutedEventArgs e)
    {
        if (Bin.SelectedItem is BinItem item) _history.Push(TimelineOps.Append(Project, item.Source));
    }

    private void BinAddTop_Click(object sender, RoutedEventArgs e)
    {
        if (Bin.SelectedItem is not BinItem item) return;
        _history.Push(TimelineOps.InsertOnTop(Project, item.Source, _playhead, out var clipId));
        Timeline.Select(TimelineOps.WithLinked(Project, new[] { clipId }));
    }

    private void BinAddAudio_Click(object sender, RoutedEventArgs e)
    {
        if (Bin.SelectedItem is not BinItem item) return;
        _history.Push(TimelineOps.InsertAudioOnly(Project, item.Source, _playhead, out var clipId));
        Timeline.Select(new[] { clipId });
    }

    /// <summary>
    /// A source dropped on the picture lands on top at the playhead, centred where it was dropped:
    /// a still at its own size, a video at two fifths of the canvas. Onto an empty frame a video
    /// simply fills the canvas.
    /// </summary>
    private void OnSourceDroppedOnPicture(Guid sourceId, Point canvas)
    {
        if (_bin.FirstOrDefault(item => item.Source.Id == sourceId) is not { } item || !item.Source.HasPicture) return;
        var project = Project; var source = item.Source;
        Placement? placement = null;
        if (source.IsImage || TimelineOps.VisibleAt(project, _playhead).Count > 0)
        {
            var size = TimelineOps.Natural(project, source.Width, source.Height);
            double w = size.Width, h = size.Height;
            if (!source.IsImage) { w = Math.Round(project.Width * 0.4); h = Math.Round(w * Math.Max(1, source.Height) / Math.Max(1, source.Width)); }
            placement = new Placement { X = Math.Round(canvas.X - w / 2), Y = Math.Round(canvas.Y - h / 2), Width = w, Height = h };
        }
        _history.Push(TimelineOps.InsertOnTop(project, source, _playhead, out var clipId, placement));
        Timeline.Select(TimelineOps.WithLinked(Project, new[] { clipId }));
    }

    private void Bin_MouseDown(object sender, MouseButtonEventArgs e) => _binDown = e.GetPosition(Bin);

    private void Bin_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Bin.SelectedItem is not BinItem item) return;
        var p = e.GetPosition(Bin);
        if (Math.Abs(p.X - _binDown.X) < 5 && Math.Abs(p.Y - _binDown.Y) < 5) return;
        DragDrop.DoDragDrop(Bin, new DataObject(TimelineView.SourceFormat, item.Source.Id.ToString()), DragDropEffects.Copy);
    }

    private void OnSourceDropped(Guid sourceId, Guid? trackId, long frame)
    {
        if (_bin.FirstOrDefault(item => item.Source.Id == sourceId) is not { } item) return;
        _history.Push(TimelineOps.Insert(Project, item.Source, trackId, frame, out var clipId));
        Timeline.Select(TimelineOps.WithLinked(Project, new[] { clipId }));
    }

    /// <summary>Asks for the new place of each linked file that has gone missing.</summary>
    private void RelinkMissing()
    {
        var project = Project;
        var missing = VideoProjectFile.Missing(project);
        if (missing.Count == 0) return;
        foreach (var source in missing)
        {
            var dialog = new OpenFileDialog { Title = $"Locate {source.Name}", Filter = MediaFilter, FileName = source.Name };
            if (dialog.ShowDialog(this) != true) continue;
            project = VideoProjectFile.Relink(project, source.Id, dialog.FileName);
        }
        if (!ReferenceEquals(project, Project))
        {
            _bin.Clear();
            _history.Reset(project);
        }
        int still = VideoProjectFile.Missing(Project).Count;
        if (still > 0) ShowStatus($"{still} media file{(still == 1 ? " is" : "s are")} missing. Those clips show nothing until the files are back.", failed: true);
    }

    // ---- Playback ----

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_playing)
        {
            long frame = _playStart + (long)(_clock.Elapsed.TotalSeconds * Project.Rate.Value);
            if (frame >= Project.Duration) { SetPlayhead(Project.Duration); Pause(); return; }
            SetPlayhead(frame);
            Timeline.FollowPlayhead();
        }
        // Decoded frames arrive between edits, so the preview is polled every screen refresh.
        Preview.Show(Project, _playhead, _playing);
        string source = Preview.Settings.PreviewQuality != PreviewQuality.Full && Preview.Settings.UseProxies ? "proxy" : "original";
        string info = $"{(int)(Project.Width * Preview.CurrentScale)} × {(int)(Project.Height * Preview.CurrentScale)} · {source}";
        if (PreviewInfoLabel.Text != info) PreviewInfoLabel.Text = info;
    }

    private void SetPlayhead(long frame)
    {
        frame = Math.Clamp(frame, 0, Math.Max(0, Project.Duration));
        if (frame == _playhead) return;
        _playhead = frame;
        Timeline.Playhead = frame;
        UpdateTime();
    }

    private void Seek(long frame)
    {
        SetPlayhead(frame);
        if (_playing) { _playStart = _playhead; _clock.Restart(); StartAudio(); }
        Preview.Show(Project, _playhead, _playing);
    }

    private void Play()
    {
        if (Project.Duration == 0) return;
        if (_playhead >= Project.Duration) SetPlayhead(0);
        _playStart = _playhead; _clock.Restart(); _playing = true;
        PlayButton.Content = "";
        StartAudio();
    }

    private void Pause()
    {
        _playing = false; _clock.Stop();
        PlayButton.Content = "";
        _audio.Pause();
    }

    private void StartAudio()
    {
        if (!_mixOpen) return;
        _audio.Position = TimeSpan.FromSeconds(Project.Seconds(_playhead));
        _audio.Play();
    }

    /// <summary>
    /// Renders the timeline's sound to one WAV for playback. Runs a moment after the last edit
    /// and only when the audio arrangement really changed.
    /// </summary>
    private async void RebuildMix()
    {
        if (_runner == null) return;
        var plan = TimelineOps.ToPlan(Project);
        var audible = plan.Audio.Where(clip => File.Exists(clip.Path)).ToList();
        plan = plan with { Audio = audible };
        string key = plan.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "|" + string.Join("|", audible.Select(clip => $"{clip.Path}:{clip.SourceIn:0.###}:{clip.Duration:0.###}:{clip.Start:0.###}:{clip.Volume:0.##}" + (clip.FadeIn > 0 || clip.FadeOut > 0 ? $":{clip.FadeIn:0.###}:{clip.FadeOut:0.###}" : "")));
        if (key == _mixKey) return;
        _mixKey = key;
        if (audible.Count == 0) { _audio.Close(); _mixOpen = false; return; }
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Preview");
            Directory.CreateDirectory(folder);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
            string wav = Path.Combine(folder, $"mix_{hash}.wav");
            if (!File.Exists(wav))
            {
                string partial = wav + ".part";
                var result = await _runner.RunAsync(ExportArguments.BuildAudioMix(plan, partial));
                if (!result.Succeeded) { ShowStatus("Preview sound could not be mixed: " + result.ErrorText(2), failed: true); return; }
                File.Move(partial, wav, overwrite: true);
            }
            if (key != _mixKey) return;
            _mixOpen = false;
            _audio.Open(new Uri(wav));
        }
        catch (Exception ex) { ShowStatus("Preview sound could not be mixed: " + ex.Message, failed: true); }
    }

    private void Play_Click(object sender, RoutedEventArgs e) { if (_playing) Pause(); else Play(); Timeline.Focus(); }
    private void GoStart_Click(object sender, RoutedEventArgs e) => Seek(0);
    private void GoEnd_Click(object sender, RoutedEventArgs e) => Seek(Project.Duration);
    private void StepBack_Click(object sender, RoutedEventArgs e) { Pause(); Seek(_playhead - 1); }
    private void StepForward_Click(object sender, RoutedEventArgs e) { Pause(); Seek(_playhead + 1); }

    // ---- Editing commands ----

    private void Undo_Click(object sender, RoutedEventArgs e) => _history.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _history.Redo();

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        var project = Project;
        bool selectedUnder = project.AllClips.Any(clip => Timeline.Selection.Contains(clip.Id) && clip.Start < _playhead && _playhead < clip.End);
        var result = TimelineOps.Split(project, _playhead, selectedUnder ? Timeline.Selection.ToList() : null);
        if (ReferenceEquals(result, project)) { ShowStatus("Nothing to split: move the playhead inside a clip."); return; }
        _history.Push(result);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection(false);
    private void RippleDelete_Click(object sender, RoutedEventArgs e) => DeleteSelection(true);

    private void DeleteSelection(bool ripple)
    {
        if (Timeline.Selection.Count == 0) { ShowStatus("Select a clip first."); return; }
        _history.Push(TimelineOps.Delete(Project, Timeline.Selection.ToList(), ripple));
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.Selection.Count == 0) return;
        var project = Project;
        foreach (var id in Timeline.Selection.ToList()) project = TimelineOps.Unlink(project, id);
        _history.Push(project);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Timeline.Select(Project.AllClips.Select(clip => clip.Id).ToList());
    private void AddVideoTrack_Click(object sender, RoutedEventArgs e) => _history.Push(TimelineOps.AddTrack(Project, TrackKind.Visual, out _));
    private void AddAudioTrack_Click(object sender, RoutedEventArgs e) => _history.Push(TimelineOps.AddTrack(Project, TrackKind.Audio, out _));

    private void EditMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool any = Timeline.Selection.Count > 0;
        UndoMenu.IsEnabled = _history.CanUndo; RedoMenu.IsEnabled = _history.CanRedo;
        DeleteMenu.IsEnabled = RippleMenu.IsEnabled = any;
        DetachMenu.IsEnabled = Project.AllClips.Any(clip => Timeline.Selection.Contains(clip.Id) && clip.LinkId != null);
        SplitMenu.IsEnabled = Project.AllClips.Any(clip => clip.Start < _playhead && _playhead < clip.End);
    }

    private void SetTool(TimelineTool tool)
    {
        Timeline.Tool = tool;
        SelectToolButton.IsChecked = tool == TimelineTool.Select;
        RazorToolButton.IsChecked = tool == TimelineTool.Razor;
        Timeline.Focus();
    }

    private void SelectTool_Click(object sender, RoutedEventArgs e) => SetTool(TimelineTool.Select);
    private void RazorTool_Click(object sender, RoutedEventArgs e) => SetTool(TimelineTool.Razor);
    private void Snap_Click(object sender, RoutedEventArgs e) => Timeline.Snap = Preview.Snap = SnapButton.IsChecked == true;
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Timeline.SetZoom(Timeline.PixelsPerSecond * 1.4);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Timeline.SetZoom(Timeline.PixelsPerSecond / 1.4);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Timeline.ZoomToFit();

    private void SyncScrollBar()
    {
        _syncingScroll = true;
        TimelineScroll.Maximum = Timeline.MaxScroll;
        TimelineScroll.ViewportSize = Timeline.LaneWidth;
        TimelineScroll.LargeChange = Timeline.LaneWidth * 0.8; TimelineScroll.SmallChange = 40;
        TimelineScroll.Value = Timeline.ScrollX;
        TimelineScroll.Visibility = Timeline.MaxScroll > 0 ? Visibility.Visible : Visibility.Hidden;
        _syncingScroll = false;
        ZoomLabel.Text = $"{Timeline.PixelsPerSecond:0.#} px/s";
    }

    private void TimelineScroll_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingScroll) Timeline.ScrollX = e.NewValue;
    }

    // ---- Properties panel ----

    private void UpdatePanel()
    {
        var project = Project;
        var selected = project.Tracks.SelectMany(track => track.Clips.Select(clip => (Track: track, Clip: clip))).Where(pair => Timeline.Selection.Contains(pair.Clip.Id)).ToList();
        _updatingPanel = true;
        try
        {
            Preview.Selected = selected.Select(pair => pair.Clip).FirstOrDefault(clip => clip is GraphicClip || clip is ImageClip || clip is VideoClip || clip is CaptionClip)?.Id;
            if (InlineWords.Visibility == Visibility.Visible && InlineText == null) EndInlineEdit();
            if (selected.Count == 0)
            {
                ClipPanel.Visibility = Visibility.Collapsed; ProjectPanel.Visibility = Visibility.Visible;
                PropTitle.Text = DocumentName;
                PropSubtitle.Text = project.Duration == 0 ? "Import an MP4 to begin. The first video sets the canvas size and frame rate." : "Select a clip on the timeline to see its settings.";
                ProjectSizeValue.Text = $"{project.Width} × {project.Height}";
                ProjectRateValue.Text = $"{project.Rate} fps";
                ProjectLengthValue.Text = Timecode(project.Duration);
                ProjectTracksValue.Text = $"{project.Tracks.Count(track => track.Kind == TrackKind.Visual)} video · {project.Tracks.Count(track => track.Kind == TrackKind.Audio)} audio";
                return;
            }
            ClipPanel.Visibility = Visibility.Visible; ProjectPanel.Visibility = Visibility.Collapsed;
            var primary = selected.FirstOrDefault(pair => pair.Clip is not AudioClip, selected[0]);
            var sound = selected.Select(pair => pair.Clip).OfType<AudioClip>().FirstOrDefault();
            PropTitle.Text = TimelineView.NameOf(project, primary.Clip);
            string kind = primary.Clip switch { VideoClip => "Video clip", ImageClip => "Picture", GraphicClip { Item: CalloutItem } => "Callout", GraphicClip { Item: TextItem } => "Text", GraphicClip => "Graphic", CaptionClip => "Caption", _ => "Audio clip" };
            string linked = primary.Clip.LinkId == null ? "" : primary.Clip is VideoClip ? " · linked audio" : " · linked video";
            PropSubtitle.Text = selected.Count > 2 ? $"{selected.Count} clips selected" : $"{kind} on {primary.Track.Name}{linked}";
            ClipStartValue.Text = Timecode(primary.Clip.Start);
            ClipLengthValue.Text = Timecode(primary.Clip.Duration);
            ClipSourceValue.Text = Timecode(project.Frames(primary.Clip.SourceIn));
            // Fades: a picture, a graphic or a sound; a linked pair shares them.
            FadePanel.Visibility = TimelineOps.CanFade(primary.Clip) ? Visibility.Visible : Visibility.Collapsed;
            FadeInBox.Show(primary.Clip.FadeIn); FadeOutBox.Show(primary.Clip.FadeOut);
            var graphicItem = (primary.Clip as GraphicClip)?.Item;
            var captionItem = primary.Clip as CaptionClip;
            var words = graphicItem as TextItem;
            // Words: a text or callout graphic shows its own style, a caption its track's.
            WordsPanel.Visibility = words != null || captionItem != null ? Visibility.Visible : Visibility.Collapsed;
            if (words != null || captionItem != null)
            {
                var style = captionItem != null ? TimelineOps.CaptionStyleOf(project, primary.Track) : null;
                string text = words?.Text ?? captionItem!.Text;
                if (WordsBox.Text.Replace("\r\n", "\n") != text) WordsBox.Text = text;
                WordsLabel.Text = captionItem != null ? "CAPTION" : "TEXT";
                FontSizeBox.Show(words?.FontSize ?? style!.FontSize);
                BoldButton.IsChecked = words?.Bold ?? style!.Bold;
                ItalicButton.IsChecked = words?.Italic ?? false;
                ItalicButton.Visibility = AlignBox.Visibility = words != null ? Visibility.Visible : Visibility.Collapsed;
                AlignBox.SelectedIndex = words == null ? 1 : words.Alignment == TextAlign.Left ? 0 : words.Alignment == TextAlign.Right ? 2 : 1;
                TextColour.Value = words?.Color ?? style!.Color;
                BackColour.Value = words != null ? words.Background : style!.Background;
                CaptionTopCheck.Visibility = CaptionNote.Visibility = captionItem != null ? Visibility.Visible : Visibility.Collapsed;
                CaptionTopCheck.IsChecked = style?.AtTop ?? false;
                double outline = words?.OutlineWidth ?? style!.OutlineWidth;
                OutlineColour.Value = outline > 0 ? words?.OutlineColor ?? style!.OutlineColor : null;
                OutlineWidthBox.Show(Math.Max(0, outline));
                // Captions take only the outline from their track; the shadow is for text graphics.
                TextShadowCheck.Visibility = words != null ? Visibility.Visible : Visibility.Collapsed;
                TextShadowCheck.IsChecked = words?.TextShadow ?? false;
            }
            // Line and fill: shapes, arrows, lines and a callout's outline. Stamps keep their own colours unless given one.
            ShapePanel.Visibility = graphicItem != null && (graphicItem is not TextItem || graphicItem is CalloutItem) ? Visibility.Visible : Visibility.Collapsed;
            if (graphicItem != null)
            {
                StrokeColour.Value = graphicItem.Stroke;
                StrokeWidthBox.Show(graphicItem.StrokeWidth);
                FillColour.Value = graphicItem.Fill;
                FillLabel.Visibility = FillColour.Visibility = graphicItem is RectItem || graphicItem is EllipseItem || graphicItem is StampItem ? Visibility.Visible : Visibility.Collapsed;
            }
            bool isPicture = primary.Clip is VideoClip || primary.Clip is ImageClip;
            PicturePanel.Visibility = isPicture || graphicItem != null ? Visibility.Visible : Visibility.Collapsed;
            PictureButtons.Visibility = isPicture ? Visibility.Visible : Visibility.Collapsed;
            PictureLabel.Text = isPicture ? "PICTURE" : "POSITION";
            PictureHint.Text = isPicture
                ? "Drag the picture in the preview to move it, its handles to resize or turn it. Shift frees the shape on a corner and snaps the turn to 15°."
                : graphicItem is TextItem ? "Drag it in the preview to move it; a corner scales the letters, a side changes where lines wrap. Double-click it to type in place."
                : "Drag it in the preview to move it, its handles to resize or turn it.";
            if (graphicItem != null)
            {
                PlaceX.Show(graphicItem.Bounds.X); PlaceY.Show(graphicItem.Bounds.Y); PlaceW.Show(graphicItem.Bounds.Width); PlaceH.Show(graphicItem.Bounds.Height); PlaceRotation.Show(graphicItem.Rotation);
                OpacitySlider.Value = Math.Round(graphicItem.Opacity * 100);
                OpacityValue.Text = $"{Math.Round(graphicItem.Opacity * 100)}%";
            }
            if (primary.Clip is VisualClip picture)
            {
                var p = TimelineOps.PlacementOf(project, picture);
                PlaceX.Show(p.X); PlaceY.Show(p.Y); PlaceW.Show(p.Width); PlaceH.Show(p.Height); PlaceRotation.Show(p.Rotation);
                OpacitySlider.Value = Math.Round(p.Opacity * 100);
                OpacityValue.Text = $"{Math.Round(p.Opacity * 100)}%";
            }
            AudioPanel.Visibility = sound != null ? Visibility.Visible : Visibility.Collapsed;
            if (sound != null)
            {
                VolumeSlider.Value = Math.Round(sound.Volume * 100);
                VolumeValue.Text = $"{Math.Round(sound.Volume * 100)}%";
                MuteCheck.IsChecked = sound.Muted;
            }
            DetachButton.Visibility = selected.Any(pair => pair.Clip.LinkId != null) ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingPanel = false; }
    }

    private VisualClip? SelectedPicture => Project.AllClips.OfType<VisualClip>().FirstOrDefault(clip => Timeline.Selection.Contains(clip.Id));

    /// <summary>Changes the selected picture's placement, starting from the one it shows now.</summary>
    private void Place(Func<Placement, Placement> change, string? key = null)
    {
        if (SelectedPicture is { } clip) { _history.Push(TimelineOps.SetPlacement(Project, clip.Id, change(TimelineOps.PlacementOf(Project, clip))), key); return; }
        if (SelectedGraphic is not { } graphic) return;
        // A graphic keeps its box in its item; the same numbers are read and written through a placement.
        var was = graphic.Item;
        var box = change(new Placement { X = was.Bounds.X, Y = was.Bounds.Y, Width = was.Bounds.Width, Height = was.Bounds.Height, Rotation = was.Rotation, Opacity = was.Opacity });
        _history.Push(TimelineOps.UpdateItem(Project, graphic.Id, item =>
        {
            var shift = new Vector(box.X - was.Bounds.X, box.Y - was.Bounds.Y);
            Item moved = item with { Bounds = new Rect(box.X, box.Y, Math.Max(0, box.Width), Math.Max(0, box.Height)), Rotation = box.Rotation, Opacity = Math.Clamp(box.Opacity, 0, 1) };
            return moved is CalloutItem callout ? callout with { Targets = callout.Targets.Select(target => target + shift).ToImmutableList() } : moved;
        }), key);
    }

    /// <summary>Sets a fade, in frames, on the clip the panel is showing (and whatever is linked to it).</summary>
    private void SetFade(long? fadeIn = null, long? fadeOut = null)
    {
        var selected = Project.Tracks.SelectMany(track => track.Clips).Where(clip => Timeline.Selection.Contains(clip.Id)).ToList();
        if (selected.Count == 0) return;
        var primary = selected.FirstOrDefault(clip => clip is not AudioClip, selected[0]);
        var result = TimelineOps.SetFade(Project, primary.Id, fadeIn, fadeOut);
        // A value that was cut short to fit the clip is shown as it ended up.
        if (ReferenceEquals(result, Project)) UpdatePanel(); else _history.Push(result);
    }

    private GraphicClip? SelectedGraphic => Project.AllClips.OfType<GraphicClip>().FirstOrDefault(clip => Timeline.Selection.Contains(clip.Id));

    private (Vibershot.Editing.Video.Track Track, CaptionClip Clip)? SelectedCaption
    {
        get
        {
            foreach (var track in Project.Tracks)
                if (track.Clips.OfType<CaptionClip>().FirstOrDefault(clip => Timeline.Selection.Contains(clip.Id)) is { } clip) return (track, clip);
            return null;
        }
    }

    // ---- Elements, text and captions ----

    private void LeftTab_Changed(object sender, RoutedEventArgs e)
    {
        if (ElementsScroll == null || LeftHint == null) return;
        bool elements = ElementsTab.IsChecked == true;
        Bin.Visibility = elements ? Visibility.Collapsed : Visibility.Visible;
        ElementsScroll.Visibility = elements ? Visibility.Visible : Visibility.Collapsed;
        LeftHint.Text = elements
            ? "Drag an element onto the picture or the timeline, or double-click it to put it in the middle at the playhead."
            : "Drag a file onto the timeline, or onto the picture to lay it over what is there. Double-click adds a video after the last clip and a still on top at the playhead.";
    }

    /// <summary>Fills the Elements tab: one tile per palette entry, grouped, each draggable and double-clickable.</summary>
    private void BuildElements()
    {
        foreach (var group in VideoElements.All.GroupBy(element => element.Group))
        {
            ElementsHost.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Style = (Style)FindResource("PanelLabel"), Margin = new Thickness(0, ElementsHost.Children.Count == 0 ? 0 : 10, 0, 8) });
            var wrap = new WrapPanel();
            foreach (var element in group)
            {
                var tile = new Border
                {
                    Width = 74, Height = 54, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(7), Background = (Brush)FindResource("Well"), BorderBrush = (Brush)FindResource("Rule"), BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand, ToolTip = element.Name, Tag = element.Key, Padding = new Thickness(3),
                    Child = new Image { Source = VideoElements.Thumbnail(element), Stretch = Stretch.Uniform },
                };
                System.Windows.Automation.AutomationProperties.SetName(tile, element.Name);
                Point down = default;
                tile.MouseLeftButtonDown += (_, e) =>
                {
                    down = e.GetPosition(tile);
                    if (e.ClickCount == 2) AddElement(element.Key, new Point(Project.Width / 2.0, Project.Height / 2.0), null, _playhead);
                };
                tile.MouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed) return;
                    var p = e.GetPosition(tile);
                    if (Math.Abs(p.X - down.X) < 5 && Math.Abs(p.Y - down.Y) < 5) return;
                    DragDrop.DoDragDrop(tile, new DataObject(VideoPreview.ElementFormat, element.Key), DragDropEffects.Copy);
                };
                wrap.Children.Add(tile);
            }
            ElementsHost.Children.Add(wrap);
        }
    }

    /// <summary>Puts a palette element on the canvas at a point, for five seconds from a frame, and selects it. Text is ready to type over.</summary>
    public void AddElement(string key, Point canvas, Guid? trackId, long frame)
    {
        if (VideoElements.Create(Project, key, canvas) is not { } item) return;
        _history.Push(TimelineOps.InsertGraphic(Project, item, trackId, frame, out var clipId));
        Timeline.Select(new[] { clipId });
        if (frame != _playhead && (_playhead < frame || _playhead >= frame + Project.Frames(TimelineOps.ImageSeconds))) Seek(frame);
        if (item is TextItem) FocusWords();
    }

    private void FocusWords()
    {
        // After the panel has been rebuilt for the new selection.
        Dispatcher.BeginInvoke(() => { if (WordsPanel.Visibility == Visibility.Visible) { WordsBox.Focus(); WordsBox.SelectAll(); } }, DispatcherPriority.Input);
    }

    private void AddCaption_Click(object sender, RoutedEventArgs e) => AddCaptionAt(null, _playhead);
    private void AddCaptionTrack_Click(object sender, RoutedEventArgs e) => _history.Push(TimelineOps.AddTrack(Project, TrackKind.Caption, out _));

    private void AddCaptionAt(Guid? trackId, long frame)
    {
        var project = TimelineOps.AddCaption(Project, trackId, frame, "Caption", out var clipId);
        if (clipId == Guid.Empty)
        {
            // A caption is already here: select it so typing changes it.
            var existing = Project.Tracks.Where(track => track.Kind == TrackKind.Caption && (trackId == null || track.Id == trackId))
                .SelectMany(track => track.Clips).FirstOrDefault(clip => clip.Start <= frame && frame < clip.End);
            if (existing == null) { ShowStatus("That caption track is locked."); return; }
            Timeline.Select(new[] { existing.Id });
        }
        else
        {
            _history.Push(project);
            Timeline.Select(new[] { clipId });
        }
        if (frame != _playhead) Seek(frame);
        FocusWords();
    }

    /// <summary>Typing in the words box (or in place over the picture) rewrites the selected text graphic or caption.</summary>
    private void Words_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingPanel) return;
        string words = ((TextBox)sender).Text.Replace("\r\n", "\n");
        if (SelectedGraphic is { Item: TextItem } graphic)
        {
            _history.Push(TimelineOps.UpdateItem(Project, graphic.Id, item => item is TextItem text ? Refit(text with { Text = words }) : item), "words:" + graphic.Id);
        }
        else if (SelectedCaption is var (_, caption))
        {
            _history.Push(TimelineOps.Update(Project, caption.Id, clip => clip is CaptionClip c ? c with { Text = words } : clip), "words:" + caption.Id);
        }
    }

    /// <summary>
    /// Resizes a text box for its words: it widens about its centre until a line fits (up to nine
    /// tenths of the canvas) and is exactly as tall as the wrapped text needs.
    /// </summary>
    private Item Refit(TextItem text)
    {
        double natural = DocumentRenderer.MeasureText(text with { Bounds = default }).Width;
        double width = Math.Max(text.Bounds.Width, Math.Min(natural, Project.Width * 0.9));
        double left = text.Bounds.X - (width - text.Bounds.Width) / 2;
        var sized = text with { Bounds = new Rect(Math.Round(left), text.Bounds.Y, Math.Round(width), text.Bounds.Height) };
        return sized with { Bounds = new Rect(sized.Bounds.X, sized.Bounds.Y, sized.Bounds.Width, DocumentRenderer.MeasureText(sized).Height) };
    }

    private void Words_KeyDown(object sender, KeyEventArgs e)
    {
        // Esc, or Ctrl+Enter, hands the keyboard back to the timeline; plain Enter is a new line.
        if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { EndInlineEdit(); Timeline.Focus(); e.Handled = true; }
    }

    /// <summary>Changes the selected text graphic, or the style of the selected caption's whole track.</summary>
    private void StyleText(Func<TextItem, TextItem> graphic, Func<CaptionStyle, CaptionStyle> captions)
    {
        if (_updatingPanel) return;
        if (SelectedGraphic is { Item: TextItem } clip) _history.Push(TimelineOps.UpdateItem(Project, clip.Id, item => item is TextItem text ? Refit(graphic(text)) : item));
        else if (SelectedCaption is var (track, _)) _history.Push(TimelineOps.SetTrack(Project, track with { CaptionStyle = captions(TimelineOps.CaptionStyleOf(Project, track)) }));
    }

    private void StyleGraphic(Func<Item, Item> change)
    {
        if (_updatingPanel || SelectedGraphic is not { } clip) return;
        _history.Push(TimelineOps.UpdateItem(Project, clip.Id, change));
    }

    private void TextStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingPanel || BoldButton == null || AlignBox == null) return;
        bool bold = BoldButton.IsChecked == true, italic = ItalicButton.IsChecked == true;
        var align = AlignBox.SelectedIndex switch { 0 => TextAlign.Left, 2 => TextAlign.Right, _ => TextAlign.Center };
        StyleText(text => text with { Bold = bold, Italic = italic, Alignment = align }, style => style with { Bold = bold });
    }

    private void TextShadow_Click(object sender, RoutedEventArgs e)
    {
        bool on = TextShadowCheck.IsChecked == true;
        StyleText(text => text with { TextShadow = on }, style => style);
    }

    private void CaptionTop_Click(object sender, RoutedEventArgs e)
    {
        bool top = CaptionTopCheck.IsChecked == true;
        StyleText(text => text, style => style with { AtTop = top });
    }

    /// <summary>
    /// The words being typed in place, as the box they are drawn in: the selected text graphic's
    /// item, or the selected caption laid out by its track's style.
    /// </summary>
    private TextItem? InlineText =>
        SelectedGraphic is { Item: TextItem words } ? words
        : SelectedCaption is var (track, caption) ? TimelineOps.CaptionItem(Project, track, caption) : null;

    /// <summary>Shows a text box exactly over a text graphic or a caption so its words can be typed where they appear.</summary>
    private void BeginInlineEdit(Guid clipId)
    {
        if (TimelineOps.Find(Project, clipId) is not var (_, clip)) return;
        string? was = clip switch { GraphicClip { Item: TextItem item } => item.Text, CaptionClip caption => caption.Text, _ => null };
        if (was == null) return;
        Timeline.Select(new[] { clipId });
        if (InlineText is not { } text) return;
        _updatingPanel = true;
        InlineWords.Text = was;
        _updatingPanel = false;
        InlineWords.Visibility = Visibility.Visible;
        PositionInlineEdit();
        InlineWords.Focus();
        InlineWords.SelectAll();
    }

    /// <summary>Keeps the in-place box on its graphic or caption, matching its letters, as the text or the window changes.</summary>
    private void PositionInlineEdit()
    {
        if (InlineText is not { } text) { EndInlineEdit(); return; }
        var screen = Preview.ScreenRect(text.Bounds);
        double zoom = screen.Width / Math.Max(1, text.Bounds.Width);
        // A few pixels wider than the graphic: a text box measures a hair wider than the renderer,
        // and without the slack the last word of a full line drops to the next one while typing.
        InlineWords.Margin = new Thickness(screen.X - 8, screen.Y, 0, 0);
        InlineWords.Width = Math.Max(40, screen.Width + 16);
        InlineWords.MinHeight = Math.Max(20, screen.Height);
        InlineWords.FontFamily = new FontFamily(text.FontFamily);
        InlineWords.FontSize = Math.Max(6, text.FontSize * zoom);
        InlineWords.FontWeight = text.Bold ? FontWeights.Bold : FontWeights.Normal;
        InlineWords.FontStyle = text.Italic ? FontStyles.Italic : FontStyles.Normal;
        // The box overhangs the graphic by 8 px each side; the padding takes that back, less the
        // border, the two pixels a text box keeps beside its words, and four of slack per side.
        double pad = text.Padding * zoom;
        // The side the words start from keeps its exact place; the spare width goes to the other
        // side (to both, for centred words), so a full line never scrolls out of the box.
        double near = Math.Max(0, pad + 1), far = Math.Max(0, pad - 12), top = Math.Max(0, pad - 1);
        InlineWords.Padding = text.Alignment switch
        {
            TextAlign.Left => new Thickness(near, top, far, top),
            TextAlign.Right => new Thickness(far, top, near, top),
            _ => new Thickness(Math.Max(0, pad - 5), top, Math.Max(0, pad - 5), top),
        };
        // Wrap only where the graphic itself wraps: a text box measures a touch wider than the
        // renderer, and would otherwise drop the last word of a line that just fits.
        InlineWords.TextWrapping = DocumentRenderer.MeasureText(text with { Bounds = default }).Width > text.Bounds.Width + 0.5 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        InlineWords.TextAlignment = text.Alignment switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
        // Opaque, so the drawn words underneath do not show through as a double image.
        var back = text.Background is { A: > 0 } b ? Color.FromRgb(b.R, b.G, b.B) : Color.FromRgb(0x20, 0x22, 0x2B);
        InlineWords.Background = new SolidColorBrush(back);
        InlineWords.Foreground = new SolidColorBrush(Color.FromRgb(text.Color.R, text.Color.G, text.Color.B));
        InlineWords.CaretBrush = InlineWords.Foreground;
    }

    private void EndInlineEdit()
    {
        if (InlineWords.Visibility != Visibility.Visible) return;
        InlineWords.Visibility = Visibility.Collapsed;
        _history.BreakCoalescing();
    }

    private void InlineWords_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndInlineEdit();

    private void ImportCaptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import captions", Filter = "SubRip subtitles (*.srt)|*.srt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var cues = Srt.Parse(File.ReadAllText(dialog.FileName));
            if (cues.Count == 0) { ShowStatus($"{Path.GetFileName(dialog.FileName)} has no captions that could be read.", failed: true); return; }
            _history.Push(TimelineOps.ImportCaptions(Project, cues, out var trackId));
            int placed = Project.Tracks.First(track => track.Id == trackId).Clips.Count;
            ShowStatus($"Imported {placed} caption{(placed == 1 ? "" : "s")} onto a new caption track.", ok: true);
        }
        catch (Exception ex) { ShowStatus("Could not import the captions: " + ex.Message, failed: true); }
    }

    private void ExportCaptions_Click(object sender, RoutedEventArgs e)
    {
        var cues = TimelineOps.CaptionCues(Project);
        if (cues.Count == 0) { ShowStatus("There are no captions to export."); return; }
        var dialog = new SaveFileDialog { Title = "Export captions", Filter = "SubRip subtitles (*.srt)|*.srt", FileName = DocumentName + ".srt", AddExtension = true };
        try { if (_path != null) dialog.InitialDirectory = Path.GetDirectoryName(_path); } catch (Exception) { }
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, Srt.Write(cues), new System.Text.UTF8Encoding(false));
            ShowStatus($"Exported {cues.Count} caption{(cues.Count == 1 ? "" : "s")} to {dialog.FileName}", ok: true);
        }
        catch (Exception ex) { ShowStatus("Could not export the captions: " + ex.Message, failed: true); }
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPanel) return;
        Guid? id = SelectedPicture?.Id ?? SelectedGraphic?.Id;
        if (id == null) return;
        double opacity = Math.Round(e.NewValue) / 100;
        Place(p => p with { Opacity = opacity }, "opacity:" + id);
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPicture is not { } clip || Project.Source(clip.SourceId) is not { } source) return;
        Place(p => TimelineOps.Fit(Project, source.Width, source.Height) with { Opacity = p.Opacity });
    }

    private void Fill_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPicture is not { } clip || Project.Source(clip.SourceId) is not { } source) return;
        Place(p => TimelineOps.Fill(Project, source.Width, source.Height) with { Opacity = p.Opacity });
    }

    private void ResetPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPicture is not { } clip) return;
        Preview.CropMode = false;
        _history.Push(TimelineOps.SetPlacement(Project, clip.Id, null));
    }

    private void Crop_Click(object sender, RoutedEventArgs e) => Preview.CropMode = CropButton.IsChecked == true;

    private AudioClip? SelectedSound => Project.AllClips.OfType<AudioClip>().FirstOrDefault(clip => Timeline.Selection.Contains(clip.Id));

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPanel || SelectedSound is not { } sound) return;
        double volume = Math.Round(e.NewValue) / 100;
        _history.Push(TimelineOps.Update(Project, sound.Id, clip => ((AudioClip)clip) with { Volume = volume }), "volume:" + sound.Id);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSound is not { } sound) return;
        bool muted = MuteCheck.IsChecked == true;
        _history.Push(TimelineOps.Update(Project, sound.Id, clip => ((AudioClip)clip) with { Muted = muted }));
    }

    // ---- Preview quality ----

    private void Quality_Click(object sender, RoutedEventArgs e)
    {
        var settings = _config.VideoEditor;
        var menu = new ContextMenu { PlacementTarget = QualityButton, Placement = PlacementMode.Bottom };
        void Choice(string header, PreviewQuality quality)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = settings.PreviewQuality == quality };
            item.Click += (_, _) => { settings.PreviewQuality = quality; ApplyPreviewSettings(); };
            menu.Items.Add(item);
        }
        var project = Project;
        Choice("Auto (match the preview size)", PreviewQuality.Auto);
        Choice($"Full  ·  {project.Width} × {project.Height}", PreviewQuality.Full);
        Choice($"Half  ·  {project.Width / 2} × {project.Height / 2}", PreviewQuality.Half);
        Choice($"Quarter  ·  {project.Width / 4} × {project.Height / 4}", PreviewQuality.Quarter);
        menu.Items.Add(new Separator());
        var proxies = new MenuItem { Header = "Use low-quality proxies (faster scrubbing, lighter on the GPU)", IsCheckable = true, IsChecked = settings.UseProxies };
        proxies.Click += (_, _) => { settings.UseProxies = proxies.IsChecked; ApplyPreviewSettings(); };
        menu.Items.Add(proxies);
        var gpu = new MenuItem { Header = "Decode on the GPU", IsCheckable = true, IsChecked = settings.HardwareDecode };
        gpu.Click += (_, _) => { settings.HardwareDecode = gpu.IsChecked; ApplyPreviewSettings(); };
        menu.Items.Add(gpu);
        menu.IsOpen = true;
    }

    private void ApplyPreviewSettings()
    {
        try { _config.Save(); } catch (Exception) { }
        Preview.Reset();
        Preview.Show(Project, _playhead, _playing);
        UpdateQualityLabel();
    }

    private void UpdateQualityLabel()
    {
        var settings = _config.VideoEditor;
        bool proxies = settings.UseProxies && settings.PreviewQuality != PreviewQuality.Full;
        QualityLabel.Text = $"Preview: {settings.PreviewQuality}{(proxies ? " · Proxy" : "")}";
        bool building = _bin.Any(item => item.ProxyBusy);
        ProxyDot.Fill = (Brush)FindResource(!proxies ? "Ink3" : building ? "Accent" : "Ok");
    }

    // ---- Files ----

    private void New_Click(object sender, RoutedEventArgs e) => new VideoEditorWindow(_config).Show();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open video project", Filter = "Vibershot video project (*.vibevideo)|*.vibevideo|MP4 video (*.mp4)|*.mp4" };
        try { if (Directory.Exists(_config.Gallery.ProjectsFolder)) dialog.InitialDirectory = _config.Gallery.ProjectsFolder; } catch (Exception) { }
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            bool pristine = Project.Duration == 0 && !_history.IsDirty && _path == null;
            Open(_config, dialog.FileName).Show();
            if (pristine) Close();
        }
        catch (Exception ex) { ShowStatus("Could not open the project: " + ex.Message, failed: true); }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Save(true);

    private bool Save(bool saveAs)
    {
        string? path = _path;
        if (path == null || saveAs)
        {
            var dialog = new SaveFileDialog { Title = "Save video project", Filter = "Vibershot video project (*.vibevideo)|*.vibevideo", FileName = DocumentName + VideoProjectFile.Extension, AddExtension = true };
            try { Directory.CreateDirectory(_config.Gallery.ProjectsFolder); dialog.InitialDirectory = _config.Gallery.ProjectsFolder; } catch (Exception) { }
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            VideoProjectFile.Save(path, Project);
            _path = path;
            _history.MarkSaved();
            DeleteAutosave();
            UpdateTitle();
            ShowStatus("Saved " + path, ok: true);
            return true;
        }
        catch (Exception ex) { ShowStatus("Save failed: " + ex.Message, failed: true); return false; }
    }

    private string AutosavePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Recovery", DocumentName + ".autosave" + VideoProjectFile.Extension);

    private void Autosave()
    {
        if (!_history.IsDirty || Project.Duration == 0) return;
        try { VideoProjectFile.Save(AutosavePath, Project); } catch (Exception) { }
    }

    private void DeleteAutosave()
    {
        try { File.Delete(AutosavePath); } catch (Exception) { }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        if (Project.Duration == 0) { ShowStatus("There is nothing to export yet."); return; }
        if (_runner == null) { ShowStatus("FFmpeg was not found, so the video cannot be exported.", failed: true); return; }
        if (VideoProjectFile.Missing(Project).Count > 0) { ShowStatus("Some media files are missing. Reopen the project to locate them before exporting.", failed: true); return; }
        var dialog = new ExportVideoWindow(_config, _runner, Project, DocumentName) { Owner = this };
        dialog.ShowDialog();
        if (dialog.ExportedPath != null) ShowStatus("Exported " + dialog.ExportedPath, ok: true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Space\tPlay / pause\nLeft / Right or , / .\tStep one frame (Shift: one second)\nHome / End\tStart / end\nJ / K / L\tBack one second / pause / play\n\n" +
        "V / C\tSelect tool / Razor tool\nS or Ctrl+K\tSplit at the playhead\nDelete\tDelete the selection\nShift+Delete\tRipple delete\nN\tSnap on / off (hold Alt while dragging to snap off)\n\n" +
        "T\tAdd a caption at the playhead\nDouble-click text in the preview\tType in place (Esc to finish)\nDrag a picture in the preview\tMove it (Shift: one axis, Alt: no snapping)\nDouble-click a picture\tCrop it (Enter or Esc to finish)\n\n+ / -\tZoom the timeline\n\\\tFit the timeline\nCtrl + wheel\tZoom at the pointer\n\nCtrl+Z / Ctrl+Y\tUndo / redo\nCtrl+S\tSave project\nCtrl+I\tImport media\nCtrl+E\tExport MP4",
        "Keyboard shortcuts");

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool slider = Keyboard.FocusedElement is Slider;
        long second = Math.Max(1, Project.Rate.Rounded);
        bool handled = true;
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z: if (shift) _history.Redo(); else _history.Undo(); break;
                case Key.Y: _history.Redo(); break;
                case Key.S: Save(shift); break;
                case Key.O: Open_Click(this, e); break;
                case Key.N: New_Click(this, e); break;
                case Key.I: Import_Click(this, e); break;
                case Key.E: Export_Click(this, e); break;
                case Key.W: Close(); break;
                case Key.A: SelectAll_Click(this, e); break;
                case Key.K: Split_Click(this, e); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Space: if (_playing) Pause(); else Play(); break;
                case Key.V: SetTool(TimelineTool.Select); break;
                case Key.C: SetTool(TimelineTool.Razor); break;
                case Key.Escape: if (Preview.CropMode) Preview.CropMode = false; else SetTool(TimelineTool.Select); break;
                case Key.Enter when Preview.CropMode: Preview.CropMode = false; break;
                case Key.S: Split_Click(this, e); break;
                case Key.T: AddCaptionAt(null, _playhead); break;
                case Key.N: SnapButton.IsChecked = !(SnapButton.IsChecked == true); Timeline.Snap = Preview.Snap = SnapButton.IsChecked == true; break;
                case Key.Delete: DeleteSelection(shift); break;
                case Key.Left when !slider: case Key.OemComma: Pause(); Seek(_playhead - (shift ? second : 1)); break;
                case Key.Right when !slider: case Key.OemPeriod: Pause(); Seek(_playhead + (shift ? second : 1)); break;
                case Key.Home: Seek(0); break;
                case Key.End: Seek(Project.Duration); break;
                case Key.J: Seek(_playhead - second); break;
                case Key.K: Pause(); break;
                case Key.L: Play(); break;
                case Key.OemPlus: case Key.Add: ZoomIn_Click(this, e); break;
                case Key.OemMinus: case Key.Subtract: ZoomOut_Click(this, e); break;
                case Key.Oem5: ZoomFit_Click(this, e); break;
                default: handled = false; break;
            }
        }
        e.Handled = handled;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_history.IsDirty || Project.Duration == 0) return;
        Pause();
        switch (SaveChangesDialog.Show(this, DocumentName))
        {
            case SaveChangesDialog.Choice.Cancel: e.Cancel = true; break;
            case SaveChangesDialog.Choice.Save: if (!Save(false)) e.Cancel = true; break;
            case SaveChangesDialog.Choice.Discard: DeleteAutosave(); break;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _mixTimer.Stop(); _autosave.Stop();
        _audio.Close();
        Preview.Reset();
    }

    /// <summary>One file in the media bin.</summary>
    public sealed class BinItem : INotifyPropertyChanged
    {
        private BitmapSource? _thumb;
        private double _proxyProgress;
        private bool _proxyBusy;
        private string _badge;

        public BinItem(MediaSource source)
        {
            Source = source;
            int seconds = (int)Math.Round(source.Duration);
            Length = source.IsImage ? "Still" : $"{seconds / 60}:{seconds % 60:00}";
            string size = source.Height >= 2160 ? "4K" : source.Height >= 1440 ? "1440p" : source.Height >= 1080 ? "1080p" : source.Height > 0 ? source.Height + "p" : "Audio";
            string rate = FrameRate.TryParse(source.FrameRate, out var parsed) ? " " + parsed.Rounded : "";
            _badge = source.IsImage ? $"{source.Width} × {source.Height}" : size + rate;
        }

        public MediaSource Source { get; }
        public string Name => Source.Name;
        public string Path => Source.Path;
        public string Length { get; }
        public string Badge { get => _badge; set { _badge = value; Raise(nameof(Badge)); } }
        public BitmapSource? Thumb { get => _thumb; set { _thumb = value; Raise(nameof(Thumb)); } }
        public double ProxyProgress { get => _proxyProgress; set { _proxyProgress = value; Raise(nameof(ProxyProgress)); } }
        public bool ProxyBusy { get => _proxyBusy; set { _proxyBusy = value; Raise(nameof(ProxyBusy)); Raise(nameof(ProxyVisibility)); } }
        public Visibility ProxyVisibility => _proxyBusy ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
