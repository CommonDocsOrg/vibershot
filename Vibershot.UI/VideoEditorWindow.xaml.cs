using System.Collections.Generic;
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
        PlaceX.Committed += value => Place(p => p with { X = value });
        PlaceY.Committed += value => Place(p => p with { Y = value });
        PlaceW.Committed += value => Place(p => p with { Width = value });
        PlaceH.Committed += value => Place(p => p with { Height = value });
        PlaceRotation.Committed += value => Place(p => p with { Rotation = value });
        foreach (var box in new[] { PlaceX, PlaceY, PlaceW, PlaceH, PlaceRotation }) box.ReturnFocusTo = Timeline;
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
        BinLabel.Text = _bin.Count == 0 ? "PROJECT MEDIA" : $"PROJECT MEDIA · {_bin.Count}";
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
        string key = plan.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "|" + string.Join("|", audible.Select(clip => $"{clip.Path}:{clip.SourceIn:0.###}:{clip.Duration:0.###}:{clip.Start:0.###}:{clip.Volume:0.##}"));
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
            Preview.Selected = selected.Select(pair => pair.Clip).OfType<VisualClip>().FirstOrDefault()?.Id;
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
            var primary = selected.FirstOrDefault(pair => pair.Clip is VisualClip, selected[0]);
            var sound = selected.Select(pair => pair.Clip).OfType<AudioClip>().FirstOrDefault();
            PropTitle.Text = project.Source(primary.Clip.SourceId)?.Name ?? "Missing media";
            string kind = primary.Clip is VideoClip ? "Video clip" : primary.Clip is ImageClip ? "Picture" : "Audio clip";
            string linked = primary.Clip.LinkId == null ? "" : primary.Clip is VideoClip ? " · linked audio" : " · linked video";
            PropSubtitle.Text = selected.Count > 2 ? $"{selected.Count} clips selected" : $"{kind} on {primary.Track.Name}{linked}";
            ClipStartValue.Text = Timecode(primary.Clip.Start);
            ClipLengthValue.Text = Timecode(primary.Clip.Duration);
            ClipSourceValue.Text = Timecode(project.Frames(primary.Clip.SourceIn));
            PicturePanel.Visibility = primary.Clip is VisualClip ? Visibility.Visible : Visibility.Collapsed;
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
        if (SelectedPicture is not { } clip) return;
        _history.Push(TimelineOps.SetPlacement(Project, clip.Id, change(TimelineOps.PlacementOf(Project, clip))), key);
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPanel || SelectedPicture is not { } clip) return;
        double opacity = Math.Round(e.NewValue) / 100;
        Place(p => p with { Opacity = opacity }, "opacity:" + clip.Id);
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
        "Drag a picture in the preview\tMove it (Shift: one axis, Alt: no snapping)\nDouble-click a picture\tCrop it (Enter or Esc to finish)\n\n+ / -\tZoom the timeline\n\\\tFit the timeline\nCtrl + wheel\tZoom at the pointer\n\nCtrl+Z / Ctrl+Y\tUndo / redo\nCtrl+S\tSave project\nCtrl+I\tImport media\nCtrl+E\tExport MP4",
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
