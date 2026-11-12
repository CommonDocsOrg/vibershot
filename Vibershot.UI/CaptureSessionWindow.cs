using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media;
using Vibershot.Core;
using Vibershot.Media;

namespace Vibershot.UI;

public sealed class CaptureSessionWindow : Window
{
    private readonly Int32Rect _region;
    private readonly bool _video;
    private readonly VideoSettings _videoSettings;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
    private readonly Button _add = new() { Content = "Add shot (2 second delay)", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _finish = new() { Content = "Finish", Margin = new Thickness(0, 0, 8, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private VideoWriter? _writer;
    private RecorderSession? _recorder;
    private RecorderStatus? _recorderStatus;
    private RecordingFrameWindow? _frame;
    private string? _downgradeNote;
    private BitmapSource _previous;
    private bool _busy, _finished, _paused;
    private readonly Button _pause = new() { Content = "Pause", Margin = new Thickness(0, 0, 8, 0) };
    private int _frames, _shots = 1;
    private string? _temporaryVideo;
    public BitmapSource? Image { get; private set; }
    public string? VideoPath { get; private set; }
    public bool Completed { get; private set; }
    private readonly string? _sourceProcess, _sourceTitle;
    private readonly string? _recordingsFolder;
    /// <param name="recordingsFolder">Where the Save As for a finished recording starts (the library's Recordings folder).</param>
    public CaptureSessionWindow(Int32Rect region, BitmapSource first, bool video, string? videoPath, VideoSettings? videoSettings = null, string? sourceProcess = null, string? sourceTitle = null, string? recordingsFolder = null)
    {
        _region = region; _video = video; _previous = first; Image = first; VideoPath = videoPath; _videoSettings = videoSettings ?? new VideoSettings(); _sourceProcess = sourceProcess; _sourceTitle = sourceTitle; _recordingsFolder = recordingsFolder;
        Title = video ? "Vibershot • Region video" : "Vibershot • Scrolling photo";
        Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Topmost = true; WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        var panel = new StackPanel { Margin = new Thickness(20) };
        // Same paper panel as the capture window: solid surface, 1px edge, soft shadow.
        Content = new Border { Margin = new Thickness(12), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("PaperEdge"), Background = (Brush)FindResource("Paper"), Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x14, 0x18, 0x1C), BlurRadius = 32, ShadowDepth = 8, Opacity = 0.2 } };
        var header = new TextBlock { Text = video ? "Recording" : "Scrolling photo", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        panel.Children.Add(header);
        _subtitle = new TextBlock { Text = video ? (UseMp4 ? $"Region video • MP4 {FrameRateSetting} fps • No audio" : "Region video • 10 fps • No audio") : "Scroll the page, then add a shot", FontSize = 12, Foreground = (Brush)FindResource("Ink2") };
        panel.Children.Add(_subtitle);
        _status.Foreground = (Brush)FindResource("Ink2"); _status.FontSize = 11.5;
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(buttons);
        if (!video) buttons.Children.Add(_add);
        else {
            _finish.Content = "Stop & save…"; _finish.Style = (Style)FindResource("PrimaryButton");
            buttons.Children.Add(_pause);
            _pause.Click += async (_, _) => await TogglePause();
        }
        buttons.Children.Add(_finish);
        var cancel = new Button { Content = video ? "Open folder" : "Cancel" }; buttons.Children.Add(cancel);
        cancel.Click += (_, _) => {
            if (!video) { Close(); return; }
            try { Directory.CreateDirectory(RecoveryFolder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", RecoveryFolder) { UseShellExecute = true }); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message + "\n" + RecoveryFolder); }
        };
        _add.Click += async (_, _) => await AddShot();
        _finish.Click += async (_, _) => await Finish();
        SourceInitialized += (_, _) =>
        {
            // Windows 10 2004+: keep session controls out of desktop captures.
            if (!SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11) && video)
                _status.Text = "Keep this control window outside the selected region.";
        };
        Loaded += async (_, _) =>
        {
            PlaceControls();
            if (!video) { _status.Text = "Select only page content. Scroll down by less than a page; keep overlapping content visible. Shots are added manually."; return; }
            ShowFrame();
            if (UseMp4 && await StartMp4()) return;
            StartAvi();
        };
        _timer.Tick += (_, _) =>
        {
            try
            {
                _writer!.AddFrame(CaptureService.Region(region.X, region.Y, region.Width, region.Height));
                _frames++; UpdateStatus();
            }
            catch (Exception ex) { _timer.Stop(); CloseFrame(); _pause.IsEnabled = false; _status.Text = "Recording stopped: " + ex.Message + " • Stop & save to keep captured frames."; }
        };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _timer.Stop(); CloseFrame();
            if (_recorder is not null) { _recorder.Dispose(); _finished = true; return; }
            bool finalized = true;
            try { _writer?.Dispose(); }
            catch (Exception ex) { finalized = false; System.Windows.MessageBox.Show("Could not finalize recording: " + ex.Message + "\nRecovery file retained: " + _temporaryVideo); }
            if (video && _writer is not null) {
                Completed = _frames > 0;
                if (_frames > 0 && finalized) { WriteSidecar(_temporaryVideo); SaveRecording(_temporaryVideo!, "Motion JPEG video|*.avi", ".avi"); }
            }
            _finished = true;
        };
    }
    // Outline of the recorded region; lives as long as frames are being captured.
    private void ShowFrame()
    {
        try { _frame = new RecordingFrameWindow(_region); _frame.Show(); }
        catch (Exception) { _frame = null; } // Cosmetic only; never block the recording.
    }
    private void CloseFrame() { var frame = _frame; _frame = null; frame?.Close(); }
    private void SetFramePaused(bool paused) { if (_frame is not null) _frame.Paused = paused; }
    private bool UseMp4 => _videoSettings.Format == VideoFormat.Mp4;
    private FrameRate FrameRateSetting => FrameRate.TryParse(_videoSettings.FrameRate, out var rate) ? rate : FrameRate.Fps30;
    private static string RecoveryFolder => RecorderSettings.DefaultRecordingsFolder;

    /// <summary>Starts the ffmpeg recorder; false (with a status explaining why) means fall back to AVI.</summary>
    private async Task<bool> StartMp4()
    {
        if (!FfmpegLocator.IsAvailable) { _downgradeNote = "ffmpeg not found (run tools\\get-ffmpeg.ps1)"; return false; }
        var settings = new RecorderSettings
        {
            Region = new PixelRect(_region.X, _region.Y, _region.Width, _region.Height),
            FrameRate = FrameRateSetting,
            Quality = VideoSettings.ClampQuality(_videoSettings.Quality),
            PreferHardwareEncoder = _videoSettings.PreferHardwareEncoder,
            ShowCursor = _videoSettings.ShowCursor,
        };
        var recorder = new RecorderSession(settings);
        recorder.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() => { _recorderStatus = status; UpdateStatus(); });
        _busy = true; _pause.IsEnabled = false; _finish.IsEnabled = false;
        try
        {
            await recorder.StartAsync();
            _recorder = recorder; _temporaryVideo = recorder.Status.CurrentFile; VideoPath = _temporaryVideo;
            return true;
        }
        catch (Exception ex) when (ex is RecorderStartException or FfmpegNotFoundException)
        {
            recorder.Dispose();
            _downgradeNote = ex.Message.Replace(Environment.NewLine, " ");
            return false;
        }
        finally { _busy = false; _pause.IsEnabled = true; _finish.IsEnabled = true; }
    }
    private void StartAvi()
    {
        _subtitle.Text = "Region video • AVI 10 fps • No audio";
        try { Directory.CreateDirectory(RecoveryFolder); _temporaryVideo = Path.Combine(RecoveryFolder, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.avi"); VideoPath = _temporaryVideo; _writer = new VideoWriter(_temporaryVideo, _region.Width, _region.Height); UpdateStatus(); _timer.Start(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Recording failed"); Close(); }
    }
    private async Task TogglePause()
    {
        if (_recorder is null)
        {
            _paused = !_paused; _pause.Content = _paused ? "Resume" : "Pause"; if (_paused) _timer.Stop(); else _timer.Start(); SetFramePaused(_paused); UpdateStatus();
            return;
        }
        if (_busy) return;
        _busy = true; _pause.IsEnabled = false; _finish.IsEnabled = false;
        try
        {
            if (_recorder.State == RecorderState.Recording) { await _recorder.PauseAsync(); _pause.Content = "Resume"; SetFramePaused(true); }
            else if (_recorder.State == RecorderState.Paused) { await _recorder.ResumeAsync(); _pause.Content = "Pause"; SetFramePaused(false); }
        }
        catch (Exception ex) { _status.Text = "Resume failed: " + ex.Message; }
        finally { _busy = false; _pause.IsEnabled = true; _finish.IsEnabled = true; }
    }
    private async Task Finish()
    {
        if (_busy || _finished) return;
        if (_recorder is null) { Completed = true; Close(); return; }
        _busy = true; _pause.IsEnabled = false; _finish.IsEnabled = false;
        string? final = null;
        try { final = await _recorder.StopAsync(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, "Could not finalize recording: " + ex.Message + "\nSegments retained in: " + RecoveryFolder, "Recording"); }
        finally { _busy = false; CloseFrame(); }
        Completed = final is not null;
        if (final is not null) { _temporaryVideo = final; VideoPath = final; WriteSidecar(final); SaveRecording(final, "MP4 video|*.mp4", ".mp4"); }
        else VideoPath = null;
        Close();
    }
    private void UpdateStatus()
    {
        if (_recorder is null && _recorderStatus is null)
        {
            string note = _downgradeNote is null ? "" : $"MP4 recording unavailable: {_downgradeNote} • Recording AVI at 10 fps instead.\n";
            _status.Text = $"{note}{(_paused ? "Paused" : "Recording")} • {_frames / 10.0:F1}s • 10 fps • No audio\nRecovery file: {_temporaryVideo}";
            return;
        }
        var s = _recorderStatus!;
        string state = s.State switch
        {
            RecorderState.Starting => "Starting",
            RecorderState.Recording => "Recording",
            RecorderState.Paused => "Paused",
            RecorderState.Finalizing => "Finalizing",
            RecorderState.Stopped => "Stopped",
            RecorderState.Failed => "Failed",
            _ => "Idle",
        };
        string size = s.Bytes >= 1_048_576 ? $"{s.Bytes / 1_048_576.0:F1} MB" : $"{s.Bytes / 1024.0:F0} KB";
        string line = $"{state} • {s.Elapsed:m\\:ss} • {s.Fps:F1} fps • {s.Frames} frames • {size}";
        if (!string.IsNullOrEmpty(s.Encoder)) line += $" • {s.Encoder} via {s.Grabber}";
        if (s.Message is not null) line += "\n" + s.Message;
        if (s.CurrentFile is not null) line += "\nRecovery file: " + s.CurrentFile;
        _status.Text = line;
    }
    // Gallery sidecar next to the recording (recovery copy and saved copy alike).
    private double RecordedSeconds => _recorderStatus?.Elapsed.TotalSeconds ?? _frames / 10.0;
    private void WriteSidecar(string? path) { if (path != null) Vibershot.Gallery.CaptureSidecar.ForVideo(_region.Width, _region.Height, RecordedSeconds, _sourceProcess, _sourceTitle).TryWrite(path); }
    private void SaveRecording(string source, string filter, string extension)
    {
        try {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save recording (Cancel keeps recovery copy)", Filter = filter, FileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}{extension}", AddExtension = true, DefaultExt = extension };
            if (_recordingsFolder != null) { try { Directory.CreateDirectory(_recordingsFolder); dialog.InitialDirectory = _recordingsFolder; } catch (Exception) { } }
            if (dialog.ShowDialog() == true) {
                if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) { File.Copy(source, dialog.FileName, true); WriteSidecar(dialog.FileName); }
                VideoPath = dialog.FileName;
            } else System.Windows.MessageBox.Show("Recording kept at:\n" + source, "Recording retained");
        } catch (Exception ex) { System.Windows.MessageBox.Show("Save failed: " + ex.Message + "\nRecovery copy retained: " + source); }
    }
    private void PlaceControls()
    {
        // Native coordinates preserve placement across monitors with different DPI.
        var handle = new WindowInteropHelper(this).Handle;
        GetWindowRect(handle, out var rect);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        var capture = new System.Drawing.Rectangle(_region.X, _region.Y, _region.Width, _region.Height);
        long least = long.MaxValue;
        System.Drawing.Rectangle best = default;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens) {
            var area = screen.WorkingArea;
            foreach (int x in new[] { capture.Right + 12, capture.Left - w - 12, area.Left, area.Right - w })
            foreach (int y in new[] { capture.Top, capture.Bottom + 12, capture.Top - h - 12, area.Top, area.Bottom - h }) {
                var candidate = new System.Drawing.Rectangle(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - w)), Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - h)), w, h);
                var overlap = System.Drawing.Rectangle.Intersect(candidate, capture);
                long size = (long)overlap.Width * overlap.Height;
                if (size < least) { least = size; best = candidate; }
            }
        }
        if (least != long.MaxValue) SetWindowPos(handle, IntPtr.Zero, best.X, best.Y, 0, 0, 0x15);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    private async Task AddShot()
    {
        if (_busy || _finished) return;
        _busy = true; _add.IsEnabled = false; _finish.IsEnabled = false; Hide();
        try
        {
            await Task.Delay(2000);
            var next = CaptureService.Region(_region.X, _region.Y, _region.Width, _region.Height);
            Image = ScrollStitcher.Append(Image!, _previous, next); _previous = next; _shots++;
            _status.Text = $"{_shots} shots • {Image.PixelWidth} × {Image.PixelHeight}. Scroll down and add another, or Finish.";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; _add.IsEnabled = true; _finish.IsEnabled = true; Show(); Activate(); }
    }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
