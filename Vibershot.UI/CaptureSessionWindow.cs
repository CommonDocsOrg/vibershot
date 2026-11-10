using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private bool _busy, _finished;
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
        Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = video ? "Recording region • 10 fps • No audio" : "Scroll the browser, then add a shot", FontSize = 18 });
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(buttons);
        if (!video) buttons.Children.Add(_add);
        buttons.Children.Add(_finish);
        var cancel = new Button { Content = "Cancel" }; buttons.Children.Add(cancel);
        cancel.Click += (_, _) => Close();
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
            if (!video) { _status.Text = "Select only page content. Scroll down by less than a page; keep overlapping content visible. Shots are added manually."; return; }
            try { _temporaryVideo = Path.Combine(Path.GetDirectoryName(videoPath!)!, ".vibershot-" + Guid.NewGuid() + ".avi"); _writer = new VideoWriter(_temporaryVideo, region.Width, region.Height); _timer.Start(); }
            catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Recording failed"); Close(); }
        };
        _timer.Tick += (_, _) =>
        {
            try
            {
                _writer!.AddFrame(CaptureService.Region(region.X, region.Y, region.Width, region.Height));
                _frames++; _status.Text = $"Recording {_frames / 10.0:F1}s • Stop with Finish. AVI (Motion JPEG).";
            }
            catch (Exception ex) { _timer.Stop(); _status.Text = "Recording stopped: " + ex.Message + " • Finish to keep captured frames."; }
        };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _timer.Stop();
            try { _writer?.Dispose(); }
            catch (Exception ex) { Completed = false; System.Windows.MessageBox.Show("Could not finalize recording: " + ex.Message); }
                        if (video)
            {
                if (_frames == 0) Completed = false;
                try
                {
                    if (Completed && _temporaryVideo != null) File.Move(_temporaryVideo, videoPath!, true);
                    else if (_temporaryVideo != null && File.Exists(_temporaryVideo)) File.Delete(_temporaryVideo);
                }
                catch (Exception ex) { Completed = false; System.Windows.MessageBox.Show("Could not save video: " + ex.Message + "\nTemporary recording: " + _temporaryVideo); }
            }
            _finished = true;
        };
    }
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

