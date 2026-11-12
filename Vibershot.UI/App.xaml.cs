using System.Threading.Tasks;
using System.Windows.Input;
using Vibershot.Core;
using Forms = System.Windows.Forms;

namespace Vibershot.UI;
public partial class App : Application
{
    private readonly AppConfig _config = AppConfig.Load();
    private MainWindow _main = null!;
    private HotkeyService? _hotkey;
    private HotkeyService? _printScreen;
    private Forms.NotifyIcon? _tray;
    private bool _capturing;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _main = new MainWindow(_config); MainWindow = _main;
        // Launched at sign-in: stay in the tray until the user asks for the launcher.
        bool startInTray = e.Args.Contains(StartupRegistration.TrayArgument, StringComparer.OrdinalIgnoreCase);
        if (!startInTray) _main.Show();
        _main.ShowStatus("Ctrl+Shift+S • Select a region • Esc cancels");
        StartupRegistration.RefreshIfEnabled();
        try
        {
            _hotkey = new HotkeyService(_main, 1, NativeMethods.MOD_CTRL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.S), "Ctrl+Shift+S");
            _hotkey.Triggered += (_, _) => CaptureNow();
        }
        catch (Exception) { _main.ShowStatus("Ctrl+Shift+S is unavailable. Use Capture region."); }
        ApplyPrintScreenSetting();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show capture window", null, (_, _) => { _main.Show(); _main.WindowState = WindowState.Normal; _main.Activate(); });
        menu.Items.Add("Capture region", null, (_, _) => CaptureNow());
        menu.Items.Add("Full desktop", null, (_, _) => CaptureNow(false));
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Vibershot • running in tray", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => { _main.Show(); _main.WindowState = WindowState.Normal; _main.Activate(); };
        ProbeFfmpeg();
    }
    /// <summary>Reports a missing or broken ffmpeg in the launcher footer instead of at the first recording.</summary>
    private async void ProbeFfmpeg()
    {
        if (_config.Video.Format != Vibershot.Core.VideoFormat.Mp4) return;
        var probe = await Task.Run(Vibershot.Media.FfmpegLocator.Probe);
        if (!probe.Ok) _main.ShowStatus("MP4 recording unavailable: " + probe.Error + " Videos record as AVI.");
    }
    /// <summary>
    /// Registers or releases the Print Screen key to match the saved setting.
    /// Safe to call again after the user changes settings.
    /// </summary>
    public void ApplyPrintScreenSetting()
    {
        _printScreen?.Dispose();
        _printScreen = null;
        var action = _config.PrintScreen;
        if (action == PrintScreenAction.Off) return;
        try
        {
            _printScreen = new HotkeyService(_main, 2, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SNAPSHOT, "Print Screen");
            _printScreen.Triggered += (_, _) => CaptureNow(region: _config.PrintScreen == PrintScreenAction.CaptureTool);
            _main.ShowStatus(action == PrintScreenAction.FullDesktop
                ? "Print Screen snaps the full desktop • Ctrl+Shift+S selects a region"
                : "Print Screen or Ctrl+Shift+S • Select a region • Esc cancels");
        }
        catch (Exception)
        {
            _main.ShowStatus("Print Screen is in use by another app. In Windows Settings, turn off \"Use the Print screen key to open screen capture\".");
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _printScreen?.Dispose();
        _hotkey?.Dispose();
        var menu = _tray?.ContextMenuStrip; _tray?.Dispose(); menu?.Dispose();
        base.OnExit(e);
    }
    public async void CaptureNow(bool region = true)
    {
        if (_capturing) return;
        _capturing = true;
        var hidden = new System.Collections.Generic.List<Window>();
        try
        {
            foreach (Window window in Windows)
                if (window.IsVisible) hidden.Add(window);
            foreach (var window in hidden) window.Hide();
            // Let the desktop compositor remove our windows before reading the screen.
            await Task.Delay(180);
            var bounds = CaptureService.DesktopBounds;
            var desktop = CaptureService.Fullscreen();
            System.Windows.Media.Imaging.BitmapSource? image = desktop;
            if (region)
            {
                var overlay = new RegionWindow(desktop, bounds, _config.ShowCaptureActions);
                image = overlay.ShowDialog() == true ? overlay.Result : null;
                if (image != null && overlay.Action != CaptureAction.Screenshot)
                {
                    var session = new CaptureSessionWindow(overlay.SelectedBounds, image, overlay.Action == CaptureAction.Video, null, _config.Video);
                    session.ShowDialog();
                    if (overlay.Action == CaptureAction.Video) { _main.ShowStatus(session.VideoPath == null ? "No video recorded." : "Video available: " + session.VideoPath); return; }
                    if (!session.Completed) { _main.ShowStatus("Capture cancelled."); return; }
                    image = session.Image;
                }
            }
            if (image == null) { _main.ShowStatus("Capture cancelled. Ctrl+Shift+S to try again."); return; }
            string status = $"{image.PixelWidth} × {image.PixelHeight}";
            if (_config.ShowSaveDialog)
            {
                try
                {
                    var path = ScreenshotSaveDialog.Save(image, _config);
                    status = path == null ? "Save cancelled • Screenshot available in preview" : "Saved: " + path;
                }
                catch (Exception ex) { status = "Save failed: " + ex.Message; }
            }
            else if (_config.Autosave || (region && _config.ShowCaptureActions))
            {
                try { status = "Saved: " + FileSaveService.Save(image, _config.OutputFolder, _config.Format, _config.WebpQuality); }
                catch (Exception ex) { status = "Autosave failed: " + ex.Message; }
            }
            if (_config.CopyToClipboard)
            {
                try { ClipboardService.SetImage(image); status += " • Copied"; }
                catch (Exception) { status += " • Clipboard busy; use Copy image to retry."; }
            }
            _main.ShowStatus(status);
            new PreviewWindow(image, status, _config).Show();
        }
        catch (Exception ex) { _main.ShowStatus("Capture failed: " + ex.Message); }
        finally
        {
            foreach (var window in hidden) window.Show();
            _capturing = false;
        }
    }
}
