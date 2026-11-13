using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Vibershot.Core;
using Vibershot.Editing.Video;
using Vibershot.Gallery;
using Vibershot.Media;

namespace Vibershot.UI;

/// <summary>
/// Export dialog for the video editor: output size, frame rate, quality and file, then a progress
/// bar while ffmpeg renders the timeline from the original files.
/// </summary>
public sealed class ExportVideoWindow : Window
{
    private readonly AppConfig _config;
    private readonly FfmpegRunner _runner;
    private readonly VideoProject _project;
    private readonly ComboBox _size = new(), _rate = new();
    private readonly Slider _quality = new() { Minimum = 1, Maximum = 100, SmallChange = 1, LargeChange = 10 };
    private readonly CheckBox _audio = new() { Content = "Include audio", IsChecked = true };
    private readonly TextBox _path = new();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _export = new() { Content = "Export", IsDefault = true, Padding = new Thickness(22, 9, 22, 9), Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button _cancel = new() { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 9, 16, 9) };
    private readonly Button _reveal = new() { Content = "Show in folder", Padding = new Thickness(16, 9, 16, 9), Visibility = Visibility.Collapsed };
    private readonly List<(string Label, int Height)> _sizes = new();
    private CancellationTokenSource? _running;

    /// <summary>The finished file, or null when nothing was exported.</summary>
    public string? ExportedPath { get; private set; }

    public ExportVideoWindow(AppConfig config, FfmpegRunner runner, VideoProject project, string name)
    {
        _config = config; _runner = runner; _project = project;
        Title = "Export video"; Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowAppearance.Apply(this);

        _sizes.Add(($"Project  ·  {project.Width} × {project.Height}", project.Height));
        foreach (int height in new[] { 2160, 1440, 1080, 720 })
            if (height != project.Height) _sizes.Add(($"{height}p  ·  {Scaled(height).Width} × {Scaled(height).Height}", height));
        foreach (var (label, _) in _sizes) _size.Items.Add(label);
        _size.SelectedIndex = 0;
        _rate.Items.Add($"Project  ·  {project.Rate} fps");
        foreach (var preset in FrameRate.Presets) _rate.Items.Add(preset + " fps");
        _rate.SelectedIndex = 0;
        _quality.Value = VideoSettings.ClampQuality(config.VideoEditor.ExportQuality);
        _audio.IsEnabled = project.AllClips.Any(clip => clip is AudioClip);
        if (!_audio.IsEnabled) { _audio.IsChecked = false; _audio.Content = "Include audio (the timeline has none)"; }
        string folder = config.Gallery.RecordingsFolder;
        _path.Text = Unique(Path.Combine(folder, name + ".mp4"));

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = "Export to MP4", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(Row("Resolution", _size));
        panel.Children.Add(Row("Frame rate", _rate));
        var quality = new Grid();
        quality.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); quality.ColumnDefinitions.Add(new ColumnDefinition()); quality.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var smaller = Hint("Smaller file"); var sharper = Hint("Sharper");
        Grid.SetColumn(_quality, 1); Grid.SetColumn(sharper, 2);
        _quality.Margin = new Thickness(10, 0, 10, 0); _quality.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(_quality, "Quality");
        quality.Children.Add(smaller); quality.Children.Add(_quality); quality.Children.Add(sharper);
        panel.Children.Add(Row("Quality", quality));
        panel.Children.Add(Row("", _audio));
        var file = new Grid();
        file.ColumnDefinitions.Add(new ColumnDefinition()); file.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        browse.Click += (_, _) => Browse();
        Grid.SetColumn(browse, 1);
        System.Windows.Automation.AutomationProperties.SetName(_path, "Save to");
        file.Children.Add(_path); file.Children.Add(browse);
        panel.Children.Add(Row("Save to", file));
        panel.Children.Add(_progress);
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _export.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _export.Click += (_, _) => Export();
        _cancel.Click += (_, _) => { if (_running != null) _running.Cancel(); else Close(); };
        _reveal.Click += (_, _) => { if (ExportedPath != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ExportedPath}\"") { UseShellExecute = true }); };
        buttons.Children.Add(_reveal); buttons.Children.Add(_cancel); buttons.Children.Add(_export);
        panel.Children.Add(buttons);
        Content = panel;
        Closing += (_, e) => { if (_running != null) { _running.Cancel(); e.Cancel = true; } };
    }

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("Ink3") };

    private static Grid Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("Ink2") });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>The project canvas scaled to a height, keeping its aspect, on even pixels.</summary>
    private (int Width, int Height) Scaled(int height) =>
        (FrameReader.Even((int)Math.Round(_project.Width * (double)height / _project.Height)), FrameReader.Even(height));

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string folder = Path.GetDirectoryName(path) ?? "", name = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void Browse()
    {
        var dialog = new SaveFileDialog { Title = "Export video", Filter = "MP4 video (*.mp4)|*.mp4", AddExtension = true, FileName = Path.GetFileName(_path.Text) };
        try { string? folder = Path.GetDirectoryName(_path.Text); if (folder != null && Directory.Exists(folder)) dialog.InitialDirectory = folder; } catch (Exception) { }
        if (dialog.ShowDialog(this) == true) _path.Text = dialog.FileName;
    }

    private async void Export()
    {
        string output = _path.Text.Trim();
        if (output.Length == 0) { _status.Text = "Choose where to save the video."; return; }
        if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) output += ".mp4";
        if (_project.Sources.Any(source => string.Equals(source.Path, output, StringComparison.OrdinalIgnoreCase))) { _status.Text = "That file is one of the project's sources. Choose a different name."; return; }
        var (width, height) = _size.SelectedIndex <= 0 ? (_project.Width, _project.Height) : Scaled(_sizes[_size.SelectedIndex].Height);
        var rate = _rate.SelectedIndex <= 0 ? _project.Rate : FrameRate.Presets[_rate.SelectedIndex - 1];
        int quality = (int)Math.Round(_quality.Value);
        _config.VideoEditor.ExportQuality = quality;
        try { _config.Save(); } catch (Exception) { }

        string partial = output + ".exporting.mp4";
        // Text, shapes and captions are drawn into pictures here for ffmpeg to lay over the video.
        string graphics = Path.Combine(Path.GetTempPath(), "Vibershot", "export-" + Guid.NewGuid().ToString("N"));
        var cancel = _running = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            _status.Text = "Finding an encoder…";
            _progress.Value = 0;
            var encoder = await EncoderProbe.ResolveAsync(_runner, _config.Video.PreferHardwareEncoder, cancel.Token);
            if (encoder == null) { _status.Text = "No H.264 encoder works on this PC."; return; }
            var plan = TimelineOps.ToPlan(_project, null, width, height, graphics) with { FrameRate = rate };
            long totalFrames = Math.Max(1, (long)Math.Round(plan.Duration * rate.Value));
            var clock = Stopwatch.StartNew();
            var progress = new Progress<FfmpegProgress>(p =>
            {
                double done = Math.Clamp(p.OutTime.TotalSeconds / Math.Max(0.001, plan.Duration), 0, 1);
                _progress.Value = done;
                string left = done > 0.02 ? $" · about {FormatTime(clock.Elapsed.TotalSeconds * (1 - done) / done)} left" : "";
                _status.Text = $"Exporting… frame {Math.Min(p.Frame, totalFrames):N0} of {totalFrames:N0}{left} · {encoder.DisplayName}";
            });
            var result = await _runner.RunAsync(ExportArguments.Build(plan, encoder, quality, partial, _audio.IsChecked == true), progress, cancel.Token);
            if (result.Cancelled) { TryDelete(partial); _status.Text = "Export cancelled."; return; }
            if (!result.Succeeded) { TryDelete(partial); _status.Text = "Export failed: " + result.ErrorText(3); return; }
            File.Move(partial, output, overwrite: true);
            try { CaptureSidecar.ForVideo(width, height, plan.Duration).Write(output); } catch (Exception) { }
            ExportedPath = output;
            _progress.Value = 1;
            _status.Text = $"Exported in {FormatTime(clock.Elapsed.TotalSeconds)}: {output}";
            _reveal.Visibility = Visibility.Visible;
            _export.Visibility = Visibility.Collapsed;
            _cancel.Content = "Close";
        }
        catch (OperationCanceledException) { TryDelete(partial); _status.Text = "Export cancelled."; }
        catch (Exception ex) { TryDelete(partial); _status.Text = "Export failed: " + ex.Message; }
        finally
        {
            _running = null;
            SetBusy(false);
            try { if (Directory.Exists(graphics)) Directory.Delete(graphics, true); } catch (Exception) { }
        }
    }

    private void SetBusy(bool busy)
    {
        _progress.Visibility = Visibility.Visible;
        _export.IsEnabled = !busy; _size.IsEnabled = _rate.IsEnabled = _quality.IsEnabled = _path.IsEnabled = !busy;
        _audio.IsEnabled = !busy && _project.AllClips.Any(clip => clip is AudioClip);
        Cursor = busy ? Cursors.AppStarting : null;
    }

    private static string FormatTime(double seconds) => seconds < 90 ? $"{Math.Max(1, (int)Math.Round(seconds))} s" : $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }
}
