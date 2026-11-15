using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Vibershot.Core;
using Vibershot.Editing.Video;
using Vibershot.Media;
using Vibershot.Media.Audio;
using Vibershot.Media.Camera;

namespace Vibershot.UI;

/// <summary>
/// The part of the recording window that deals with sound and the webcam: the choices shown
/// before a recording starts (what to record, which devices, how loud, with live meters and the
/// camera preview), and wrapping the finished tracks up into a video editor project.
/// </summary>
public sealed partial class CaptureSessionWindow
{
    private readonly DispatcherTimer _levels = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly StackPanel _setup = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _systemToggle = new() { Content = "System audio", ToolTip = "Record what the computer plays, on its own track" };
    private readonly CheckBox _micToggle = new() { Content = "Microphone", ToolTip = "Record a microphone, on its own track" };
    private readonly CheckBox _cameraToggle = new() { Content = "Camera", ToolTip = "Record a webcam as its own video layer" };
    private readonly ComboBox _micPicker = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _cameraPicker = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Slider _micVolume = new() { Minimum = 0, Maximum = 200, SmallChange = 5, LargeChange = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _micVolumeLabel = new() { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private readonly TextBlock _systemNote = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _cameraNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _micDetails = new() { Margin = new Thickness(0, 0, 0, 0) };
    private readonly StackPanel _cameraDetails = new();
    private readonly CheckBox _mute = new() { Content = "Mute microphone", Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
    private readonly CheckBox _skipNextTime = new() { Content = "Start straight away next time", Margin = new Thickness(0, 12, 0, 0), ToolTip = "Skip these choices and record at once with what is switched on now. Change it back in Settings → Video." };
    private readonly Button _start = new() { Content = "Start recording", Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
    private readonly Button _cancelSetup = new() { Content = "Cancel", IsCancel = true };
    private Rectangle _systemMeter = null!, _micMeter = null!;
    private double _systemShown, _micShown;
    private FfmpegRunner? _runner;
    private CaptureRig? _rig;
    private CameraBubbleWindow? _bubble;
    private IReadOnlyList<CameraDeviceInfo> _cameras = Array.Empty<CameraDeviceInfo>();
    private EncoderInfo? _encoder;
    private Action? _saveSettings;
    private bool _extras, _filling, _settingUp;
    private string? _projectPath;
    private int _cameraChange;

    /// <summary>Set when the user chose "Open in editor" after a recording: the project to open once this window has closed.</summary>
    public string? OpenInEditorPath { get; private set; }

    private const double MeterWidth = 120;

    /// <summary>Shows the choices and opens the chosen devices, so meters move and the camera shows before anything is recorded.</summary>
    private void BeginSetup()
    {
        _settingUp = true;
        _runner = new FfmpegRunner();
        string root = _recordingsFolder ?? RecoveryFolder;
        _rig = new CaptureRig(_runner, Path.Combine(root, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}"));

        var ink2 = (Brush)FindResource("Ink2"); var ink3 = (Brush)FindResource("Ink3");
        _systemNote.Foreground = ink3; _cameraNote.Foreground = ink3; _micVolumeLabel.Foreground = ink2;
        _mute.Style = (Style)FindResource("TickCheckBox");
        _skipNextTime.Style = (Style)FindResource("TickCheckBox");
        _start.Style = (Style)FindResource("PrimaryButton");

        _setup.Children.Add(Row(_systemToggle, _systemMeter = Meter()));
        _setup.Children.Add(_systemNote);
        _setup.Children.Add(Divider());
        _setup.Children.Add(Row(_micToggle, _micMeter = Meter()));
        var volume = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var volumeLabel = new TextBlock { Text = "Volume", FontSize = 11.5, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(volumeLabel, Dock.Left); DockPanel.SetDock(_micVolumeLabel, Dock.Right);
        volume.Children.Add(volumeLabel); volume.Children.Add(_micVolumeLabel); volume.Children.Add(_micVolume);
        _micDetails.Children.Add(_micPicker); _micDetails.Children.Add(volume);
        _setup.Children.Add(_micDetails);
        _setup.Children.Add(Divider());
        _setup.Children.Add(Row(_cameraToggle, null));
        _cameraDetails.Children.Add(_cameraPicker); _cameraDetails.Children.Add(_cameraNote);
        _setup.Children.Add(_cameraDetails);
        _setup.Children.Add(_skipNextTime);
        _panel.Children.Insert(_panel.Children.IndexOf(_status), _setup);
        _panel.Children.Insert(_panel.Children.IndexOf(_buttons), _mute);

        _pause.Visibility = _finish.Visibility = Visibility.Collapsed;
        if (_openFolder != null) _openFolder.Visibility = Visibility.Collapsed;
        _buttons.Children.Add(_start); _buttons.Children.Add(_cancelSetup);

        // Fill the controls from the remembered choices before any handler is attached.
        _filling = true;
        _systemToggle.IsChecked = _videoSettings.RecordSystemAudio;
        _micToggle.IsChecked = _videoSettings.RecordMicrophone;
        _cameraToggle.IsChecked = _videoSettings.RecordCamera;
        _micVolume.Value = Math.Clamp(_videoSettings.MicrophoneVolume, 0, 200);
        _skipNextTime.IsChecked = _videoSettings.StartImmediately;
        FillMicrophones();
        _filling = false;

        _systemToggle.Click += (_, _) => { _videoSettings.RecordSystemAudio = _systemToggle.IsChecked == true; ApplySystemAudio(); Remember(); };
        _micToggle.Click += (_, _) => { _videoSettings.RecordMicrophone = _micToggle.IsChecked == true; ApplyMicrophone(); Remember(); };
        _micPicker.SelectionChanged += (_, _) => { if (_filling) return; _videoSettings.MicrophoneId = (_micPicker.SelectedItem as ComboBoxItem)?.Tag as string; ApplyMicrophone(); Remember(); };
        _micVolume.ValueChanged += (_, _) => { _videoSettings.MicrophoneVolume = (int)Math.Round(_micVolume.Value); ApplyMicrophoneVolume(); };
        _micVolume.LostMouseCapture += (_, _) => Remember();
        _cameraToggle.Click += async (_, _) => { _videoSettings.RecordCamera = _cameraToggle.IsChecked == true; Remember(); await ApplyCameraAsync(); };
        _cameraPicker.SelectionChanged += async (_, _) => { if (_filling) return; _videoSettings.CameraName = (_cameraPicker.SelectedItem as ComboBoxItem)?.Content as string; Remember(); await ApplyCameraAsync(); };
        _mute.Click += (_, _) => { if (_rig?.Microphone is { } microphone) microphone.Muted = _mute.IsChecked == true; };
        _skipNextTime.Click += (_, _) => { _videoSettings.StartImmediately = _skipNextTime.IsChecked == true; Remember(); };
        _start.Click += async (_, _) => await StartRecordingAsync();
        _cancelSetup.Click += (_, _) => Close();
        _levels.Tick += (_, _) => UpdateLevels();

        ApplySystemAudio();
        ApplyMicrophone();
        _levels.Start();
        _status.Text = _videoSettings.StartImmediately
            ? "Starting with the choices from last time…"
            : "Choose what to record with the screen, then start. Each choice is recorded as its own track.";
        _ = OpenCameraThenMaybeStartAsync();
        UpdateSubtitle();
    }

    /// <summary>With "start straight away" on, recording begins as soon as the camera (when one is on) shows its first frame.</summary>
    private async Task OpenCameraThenMaybeStartAsync()
    {
        try
        {
            await LoadCamerasAsync();
            if (_videoSettings.StartImmediately && _settingUp) await StartRecordingAsync();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    /// <summary>The Record video shortcut pressed while this window is open: start if still choosing, stop if recording.</summary>
    public async void StartOrStop()
    {
        try
        {
            if (_settingUp) await StartRecordingAsync();
            else if (_recorder != null && _projectPath == null && !_busy && !_finished) await Finish();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    /// <summary>The pause shortcut: pauses or resumes a recording in progress.</summary>
    public async void PauseOrResume()
    {
        try { if (!_settingUp && _projectPath == null && _pause.IsEnabled && _pause.Visibility == Visibility.Visible) await TogglePause(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    /// <summary>The mute shortcut: mutes or unmutes the microphone track while recording.</summary>
    public void ToggleMute()
    {
        if (_rig?.Microphone is not { } microphone || _settingUp) return;
        microphone.Muted = !microphone.Muted;
        _mute.IsChecked = microphone.Muted;
    }

    private Grid Row(CheckBox toggle, FrameworkElement? meter)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toggle.FontWeight = FontWeights.SemiBold;
        row.Children.Add(toggle);
        if (meter != null)
        {
            var track = new Border { Width = MeterWidth, Height = 6, CornerRadius = new CornerRadius(3), Background = (Brush)FindResource("Well"), VerticalAlignment = VerticalAlignment.Center, Child = meter, ToolTip = "Live level" };
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
        }
        return row;
    }

    private Rectangle Meter() => new() { Width = 0, Height = 6, RadiusX = 3, RadiusY = 3, HorizontalAlignment = HorizontalAlignment.Left, Fill = (Brush)FindResource("Ok") };

    private Border Divider() => new() { Height = 1, Background = (Brush)FindResource("Rule"), Margin = new Thickness(0, 12, 0, 12) };

    private void Remember() { try { _saveSettings?.Invoke(); } catch (Exception) { } UpdateSubtitle(); }

    private void UpdateSubtitle()
    {
        if (!UseMp4) return;
        _subtitle.Text = $"Region video • MP4 {FrameRateSetting} fps • {_videoSettings.Summary()}";
    }

    private void FillMicrophones()
    {
        _micPicker.Items.Clear();
        var microphones = AudioDevices.Microphones();
        foreach (var microphone in microphones)
            _micPicker.Items.Add(new ComboBoxItem { Content = microphone.Name + (microphone.IsDefault ? " (default)" : ""), Tag = microphone.Id });
        if (microphones.Count == 0)
        {
            _micPicker.Items.Add(new ComboBoxItem { Content = "No microphone found", IsEnabled = false });
            _micToggle.IsChecked = false; _micToggle.IsEnabled = false;
            return;
        }
        int index = microphones.ToList().FindIndex(microphone => microphone.Id == _videoSettings.MicrophoneId);
        _micPicker.SelectedIndex = Math.Max(0, index);
    }

    private void ApplySystemAudio()
    {
        bool on = _systemToggle.IsChecked == true;
        _rig?.UseSystemAudio(on);
        _systemNote.Text = on ? "Records " + (AudioDevices.DefaultOutput()?.Name ?? "the default output") : "Off";
        _systemShown = 0;
    }

    private void ApplyMicrophone()
    {
        bool on = _micToggle.IsChecked == true;
        _micDetails.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _rig?.UseMicrophone(on, (_micPicker.SelectedItem as ComboBoxItem)?.Tag as string);
        ApplyMicrophoneVolume();
        _micShown = 0;
    }

    private void ApplyMicrophoneVolume()
    {
        _micVolumeLabel.Text = $"{_micVolume.Value:0}%";
        if (_rig?.Microphone is { } microphone) microphone.Gain = _micVolume.Value / 100.0;
    }

    private async Task LoadCamerasAsync()
    {
        _cameraDetails.Visibility = _cameraToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _cameraNote.Text = "Looking for cameras…";
        _cameras = await CameraDevices.ListAsync(_runner!);
        if (!_settingUp) return;
        _filling = true;
        _cameraPicker.Items.Clear();
        foreach (var camera in _cameras) _cameraPicker.Items.Add(new ComboBoxItem { Content = camera.Name });
        if (_cameras.Count == 0)
        {
            _cameraPicker.Items.Add(new ComboBoxItem { Content = "No camera found", IsEnabled = false });
            _cameraToggle.IsChecked = false; _cameraToggle.IsEnabled = false;
        }
        else _cameraPicker.SelectedIndex = Math.Max(0, _cameras.ToList().FindIndex(camera => camera.Name == _videoSettings.CameraName));
        _filling = false;
        await ApplyCameraAsync();
    }

    /// <summary>Opens the chosen camera with its floating preview, or closes both when the camera is off.</summary>
    private async Task ApplyCameraAsync()
    {
        int change = ++_cameraChange;
        bool on = _cameraToggle.IsChecked == true && _cameras.Count > 0;
        _cameraDetails.Visibility = _cameraToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_rig == null || !_settingUp) return;
        if (!on)
        {
            _rig.UseCamera(null);
            CloseBubble();
            _cameraNote.Text = _cameras.Count == 0 ? "No camera found." : "";
            return;
        }
        _cameraNote.Text = "Starting camera…";
        _encoder ??= await EncoderProbe.ResolveAsync(_runner!, _videoSettings.PreferHardwareEncoder);
        if (change != _cameraChange || !_settingUp || _rig == null) return;
        if (_encoder == null) { _cameraNote.Text = "No H.264 encoder works, so the camera cannot be recorded."; return; }
        var device = _cameras[Math.Clamp(_cameraPicker.SelectedIndex, 0, _cameras.Count - 1)];
        _rig.UseCamera(device, _encoder);
        var session = _rig.Camera!;
        if (_bubble == null)
        {
            _bubble = new CameraBubbleWindow(_region, _videoSettings.CameraShape, _videoSettings.CameraMirror, _videoSettings.CameraSize);
            _bubble.LookChanged += () =>
            {
                if (_bubble == null) return;
                _videoSettings.CameraShape = _bubble.Shape; _videoSettings.CameraMirror = _bubble.Mirror; _videoSettings.CameraSize = _bubble.BubbleSize;
                Remember();
            };
            _bubble.Show();
        }
        _bubble.Attach(session);
        bool started = await session.Started;
        if (change != _cameraChange || _rig?.Camera != session) return;
        if (!started)
        {
            _cameraNote.Text = session.Error ?? "The camera could not be opened.";
            _bubble?.ShowNote("Camera unavailable");
            return;
        }
        _cameraNote.Text = _bubble is { ExcludedFromCapture: false }
            ? "This Windows version cannot hide the preview from the recording: keep it outside the recorded area."
            : "The preview floats over your screen: drag it where you want it, scroll over it to resize, right-click it for shape. It is recorded as its own layer.";
    }

    private void CloseBubble() { var bubble = _bubble; _bubble = null; bubble?.Close(); }

    private void UpdateLevels()
    {
        Show(_systemMeter, ref _systemShown, _rig?.SystemAudio);
        Show(_micMeter, ref _micShown, _rig?.Microphone);
        if (_rig?.SystemAudio?.Error is { } systemError) _systemNote.Text = "Unavailable: " + systemError;
        if (_rig?.Microphone?.Error is { } micError && _micToggle.IsChecked == true) { _micVolumeLabel.Text = "!"; _micVolumeLabel.ToolTip = micError; _micPicker.ToolTip = "Unavailable: " + micError; }
        if (_recorder != null && _rig?.Camera?.Error is { } cameraError && _cameraNote.Text != cameraError) _cameraNote.Text = cameraError;

        void Show(Rectangle meter, ref double shown, AudioTrackRecorder? recorder)
        {
            double level = recorder?.TakeLevel() ?? 0;
            // Decibels across the bar: -50 dB at the left, full scale at the right; falls back slowly so peaks can be read.
            double fraction = level <= 0 ? 0 : Math.Clamp((20 * Math.Log10(level) + 50) / 50, 0, 1);
            shown = Math.Max(fraction, shown * 0.85);
            meter.Width = MeterWidth * shown;
            meter.Fill = (Brush)FindResource(level >= 0.99 ? "Danger" : recorder is { Muted: true } ? "Ink3" : "Ok");
        }
    }

    /// <summary>Locks the choices in and starts the screen recording with whatever is switched on beside it.</summary>
    private async Task StartRecordingAsync()
    {
        if (_busy || !_settingUp) return;
        _settingUp = false;
        _extras = _rig is { } rig && (rig.SystemAudio != null || rig.Microphone != null || rig.Camera is { Error: null });
        foreach (var control in new Control[] { _systemToggle, _micToggle, _cameraToggle, _micPicker, _cameraPicker }) control.IsEnabled = false;
        _skipNextTime.Visibility = _videoSettings.StartImmediately ? Visibility.Visible : Visibility.Collapsed;
        _start.Visibility = _cancelSetup.Visibility = Visibility.Collapsed;
        _start.IsDefault = false; _cancelSetup.IsCancel = false;
        _pause.Visibility = _finish.Visibility = Visibility.Visible;
        // With sound or a camera the files go to the session folder, shown when the recording is done.
        if (_openFolder != null) _openFolder.Visibility = _extras ? Visibility.Collapsed : Visibility.Visible;
        _mute.Visibility = _rig?.Microphone != null ? Visibility.Visible : Visibility.Collapsed;
        if (!_extras) { DisposeRig(deleteFolder: true); _setup.Visibility = Visibility.Collapsed; _levels.Stop(); }
        if (await StartMp4()) return;
        // The MP4 recorder could not start: the old AVI recorder has no sound or camera.
        _extras = false;
        DisposeRig(deleteFolder: true);
        _setup.Visibility = _mute.Visibility = Visibility.Collapsed; _levels.Stop();
        StartAvi();
    }

    /// <summary>A plain screen recording goes where it always went; one with sound or a camera gets a session folder and stretch timing.</summary>
    private RecorderSettings RecorderSettingsForSession()
    {
        var settings = new RecorderSettings
        {
            Region = new PixelRect(_region.X, _region.Y, _region.Width, _region.Height),
            FrameRate = FrameRateSetting,
            Quality = VideoSettings.ClampQuality(_videoSettings.Quality),
            PreferHardwareEncoder = _videoSettings.PreferHardwareEncoder,
            ShowCursor = _videoSettings.ShowCursor,
        };
        return _extras && _rig != null ? settings with { RecordingsFolder = _rig.Folder, FrameClock = true, FinalFileName = "screen.mp4" } : settings;
    }

    /// <summary>Closes the sound and camera tracks, lines them up with the screen recording and saves the editor project.</summary>
    private async Task FinishSessionAsync(string screenFile)
    {
        _busy = true;
        try
        {
            _status.Text = "Finishing tracks…";
            SessionCameraBox? box = null;
            if (_bubble != null && _bubble.PixelBounds is { Width: > 0 } bounds)
                box = new SessionCameraBox(bounds.X - _region.X, bounds.Y - _region.Y, bounds.Width, bounds.Height, _bubble.Shape, _bubble.Mirror);
            var even = new PixelRect(_region.X, _region.Y, _region.Width, _region.Height).ToEven();
            var rig = _rig!;
            var manifest = await rig.FinishAsync(screenFile, _recorder!.Stretches, even.Width, even.Height, FrameRateSetting, box, new Progress<string>(text => _status.Text = text));
            CloseBubble(); _levels.Stop();
            _setup.Visibility = _mute.Visibility = Visibility.Collapsed;

            async Task<MediaSource?> Probe(SessionTrack? track)
            {
                if (track == null) return null;
                try { return MediaSource.From(await Ffprobe.ProbeAsync(Path.Combine(rig.Folder, track.File))); }
                catch (Exception ex) { rig.Problems.Add($"{track.File}: {ex.Message}"); return null; }
            }
            var screen = MediaSource.From(await Ffprobe.ProbeAsync(screenFile));
            var project = SessionProject.Build(manifest, screen, await Probe(manifest.Camera), await Probe(manifest.SystemAudio), await Probe(manifest.Microphone));
            _projectPath = Path.Combine(rig.Folder, SessionProject.ProjectFileName);
            VideoProjectFile.Save(_projectPath, project);
            WriteSidecar(screenFile);
            Completed = true; VideoPath = _projectPath;
            ShowDone(manifest, rig);
        }
        catch (Exception ex)
        {
            CloseBubble();
            System.Windows.MessageBox.Show(this, "The recording was saved, but its tracks could not be put together: " + ex.Message + "\nFiles are in: " + _rig?.Folder, "Recording");
            Completed = true; VideoPath = screenFile;
            Close();
        }
        finally { _busy = false; }
    }

    private void ShowDone(SessionManifest manifest, CaptureRig rig)
    {
        var tracks = new List<string> { "screen" };
        if (manifest.Camera != null) tracks.Add("camera");
        if (manifest.Microphone != null) tracks.Add("microphone");
        if (manifest.SystemAudio != null) tracks.Add("system audio");
        string problems = rig.Problems.Count == 0 ? "" : "\nNot recorded: " + string.Join("; ", rig.Problems);
        ((TextBlock)_panel.Children[0]).Text = "Recording saved";
        _status.Text = $"{RecordedSeconds:F0} s • separate tracks: {string.Join(", ", tracks)}.{problems}\n{rig.Folder}";
        _buttons.Children.Clear();
        var edit = new Button { Content = "Open in editor", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 8, 0), IsDefault = true, ToolTip = "Open the tracks in the video editor to trim, move the camera, or mute a sound" };
        var save = new Button { Content = "Save as MP4…", Margin = new Thickness(0, 0, 8, 0), ToolTip = "Mix everything down to one video file" };
        var folder = new Button { Content = "Open folder", Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close", IsCancel = true };
        edit.Click += (_, _) => { OpenInEditorPath = _projectPath; Close(); };
        save.Click += async (_, _) => await SaveMixdownAsync(new[] { edit, save, folder, close });
        folder.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{rig.Folder}\"") { UseShellExecute = true }); } catch (Exception ex) { _status.Text = ex.Message; } };
        close.Click += (_, _) => Close();
        foreach (var button in new[] { edit, save, folder, close }) _buttons.Children.Add(button);
    }

    /// <summary>Renders the project as it stands (camera over the screen, every sound mixed) to one MP4.</summary>
    private async Task SaveMixdownAsync(Button[] buttons)
    {
        if (_busy || _projectPath == null || _runner == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save as MP4", Filter = "MP4 video|*.mp4", FileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4", AddExtension = true, DefaultExt = ".mp4" };
        if (_recordingsFolder != null) { try { Directory.CreateDirectory(_recordingsFolder); dialog.InitialDirectory = _recordingsFolder; } catch (Exception) { } }
        if (dialog.ShowDialog(this) != true) return;
        _busy = true;
        foreach (var button in buttons) button.IsEnabled = false;
        string graphics = Path.Combine(Path.GetTempPath(), "vibershot-mixdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            _encoder ??= await EncoderProbe.ResolveAsync(_runner, _videoSettings.PreferHardwareEncoder);
            if (_encoder == null) throw new InvalidOperationException("No H.264 encoder works with this ffmpeg build.");
            var project = VideoProjectFile.Load(_projectPath);
            var plan = TimelineOps.ToPlan(project, graphicsFolder: graphics);
            var progress = new Progress<FfmpegProgress>(p => _status.Text = $"Saving MP4… {Math.Clamp(p.OutTime.TotalSeconds / Math.Max(0.1, plan.Duration), 0, 1):P0}");
            var result = await _runner.RunAsync(ExportArguments.Build(plan, _encoder, VideoSettings.ClampQuality(_videoSettings.Quality), dialog.FileName), progress);
            if (!result.Succeeded) throw new InvalidOperationException(result.ErrorText(3));
            WriteSidecar(dialog.FileName);
            VideoPath = dialog.FileName;
            _status.Text = "Saved " + dialog.FileName + "\nThe separate tracks are still in the recording's folder.";
        }
        catch (Exception ex) { _status.Text = "Save failed: " + ex.Message; }
        finally
        {
            _busy = false;
            foreach (var button in buttons) button.IsEnabled = true;
            try { Directory.Delete(graphics, true); } catch (Exception) { }
        }
    }

    private void DisposeRig(bool deleteFolder)
    {
        var rig = _rig;
        _rig = null;
        CloseBubble();
        if (rig == null) return;
        rig.Dispose();
        if (!deleteFolder) return;
        // Only camera takes from before the recording can be in there. ffmpeg may hold one for a moment after it is stopped.
        string folder = rig.Folder;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
            {
                try { if (!File.Exists(Path.Combine(folder, "screen.mp4"))) Directory.Delete(folder, true); return; }
                catch (Exception) { await Task.Delay(400); }
            }
        });
    }

    /// <summary>Called as the window closes: lets go of devices, and of the session folder when nothing was recorded into it.</summary>
    private void CleanupSession()
    {
        _settingUp = false;
        _levels.Stop();
        DisposeRig(deleteFolder: _projectPath == null);
    }
}
