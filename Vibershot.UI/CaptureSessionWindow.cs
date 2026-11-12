using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media;
using Vibershot.Core;

namespace Vibershot.UI;

public sealed class CaptureSessionWindow : Window
{
    private readonly Int32Rect _region;
    private readonly bool _video;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
    private readonly Button _add = new() { Content = "Add shot (2 second delay)", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _finish = new() { Content = "Finish", Margin = new Thickness(0, 0, 8, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private VideoWriter? _writer;
    private BitmapSource _previous;
    private bool _busy, _finished, _paused;
    private readonly Button _pause = new() { Content = "Pause", Margin = new Thickness(0, 0, 8, 0) };
    private int _frames, _shots = 1;
    private string? _temporaryVideo;
    public BitmapSource? Image { get; private set; }
    public string? VideoPath { get; private set; }
    public bool Completed { get; private set; }
    public CaptureSessionWindow(Int32Rect region, BitmapSource first, bool video, string? videoPath)
    {
        _region = region; _video = video; _previous = first; Image = first; VideoPath = videoPath;
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
        panel.Children.Add(new TextBlock { Text = video ? "Region video • 10 fps • No audio" : "Scroll the page, then add a shot", FontSize = 12, Foreground = (Brush)FindResource("Ink2") });
        _status.Foreground = (Brush)FindResource("Ink2"); _status.FontSize = 11.5;
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(buttons);
        if (!video) buttons.Children.Add(_add);
        else {
            _finish.Content = "Stop & save…"; _finish.Style = (Style)FindResource("PrimaryButton");
            buttons.Children.Add(_pause);
            _pause.Click += (_, _) => { _paused = !_paused; _pause.Content = _paused ? "Resume" : "Pause"; if (_paused) _timer.Stop(); else _timer.Start(); UpdateStatus(); };
        }
        buttons.Children.Add(_finish);
        var cancel = new Button { Content = video ? "Open folder" : "Cancel" }; buttons.Children.Add(cancel);
        cancel.Click += (_, _) => {
            if (!video) { Close(); return; }
            try { Directory.CreateDirectory(RecoveryFolder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", RecoveryFolder) { UseShellExecute = true }); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message + "\n" + RecoveryFolder); }
        };
        _add.Click += async (_, _) => await AddShot();
        _finish.Click += (_, _) => { Completed = true; Close(); };
        SourceInitialized += (_, _) =>
        {
            // Windows 10 2004+: keep session controls out of desktop captures.
            if (!SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11) && video)
                _status.Text = "Keep this control window outside the selected region.";
        };
        Loaded += (_, _) =>
        {
            PlaceControls();
            if (!video) { _status.Text = "Select only page content. Scroll down by less than a page; keep overlapping content visible. Shots are added manually."; return; }
            try { Directory.CreateDirectory(RecoveryFolder); _temporaryVideo = Path.Combine(RecoveryFolder, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.avi"); VideoPath = _temporaryVideo; _writer = new VideoWriter(_temporaryVideo, region.Width, region.Height); UpdateStatus(); _timer.Start(); }
            catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Recording failed"); Close(); }
        };
        _timer.Tick += (_, _) =>
        {
            try
            {
                _writer!.AddFrame(CaptureService.Region(region.X, region.Y, region.Width, region.Height));
                _frames++; UpdateStatus();
            }
            catch (Exception ex) { _timer.Stop(); _pause.IsEnabled = false; _status.Text = "Recording stopped: " + ex.Message + " • Stop & save to keep captured frames."; }
        };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _timer.Stop();
            bool finalized = true;
            try { _writer?.Dispose(); }
            catch (Exception ex) { finalized = false; System.Windows.MessageBox.Show("Could not finalize recording: " + ex.Message + "\nRecovery file retained: " + _temporaryVideo); }
            if (video) {
                Completed = _frames > 0;
                if (_frames > 0 && finalized) SaveRecording();
            }
            _finished = true;
        };
    }
    private static string RecoveryFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Recordings");
    private void UpdateStatus() => _status.Text = $"{(_paused ? "Paused" : "Recording")} • {_frames / 10.0:F1}s • 10 fps • No audio\nRecovery file: {_temporaryVideo}";
    private void SaveRecording()
    {
        try {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save recording (Cancel keeps recovery copy)", Filter = "Motion JPEG video|*.avi", FileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.avi", AddExtension = true, DefaultExt = ".avi" };
            if (dialog.ShowDialog() == true) {
                if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(_temporaryVideo!), StringComparison.OrdinalIgnoreCase)) File.Copy(_temporaryVideo!, dialog.FileName, true);
                VideoPath = dialog.FileName;
            } else System.Windows.MessageBox.Show("Recording kept at:\n" + _temporaryVideo, "Recording retained");
        } catch (Exception ex) { System.Windows.MessageBox.Show("Save failed: " + ex.Message + "\nRecovery copy retained: " + _temporaryVideo); }
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
