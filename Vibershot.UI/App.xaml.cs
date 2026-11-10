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
    private Forms.NotifyIcon? _tray;
    private bool _capturing;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _main = new MainWindow(_config); MainWindow = _main; _main.Show();
        _main.ShowStatus("Ctrl+Shift+S • Select a region • Esc cancels");
        try
        {
            _hotkey = new HotkeyService(_main, 1, NativeMethods.MOD_CTRL | NativeMethods.MOD_SHIFT | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(Key.S), "Ctrl+Shift+S");
            _hotkey.Triggered += (_, _) => CaptureNow();
        }
        catch (Exception) { _main.ShowStatus("Ctrl+Shift+S is unavailable. Use Capture region."); }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show capture window", null, (_, _) => { _main.Show(); _main.WindowState = WindowState.Normal; _main.Activate(); });
        menu.Items.Add("Capture region", null, (_, _) => CaptureNow());
        menu.Items.Add("Full desktop", null, (_, _) => CaptureNow(false));
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Vibershot", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => { _main.Show(); _main.WindowState = WindowState.Normal; _main.Activate(); };
    }
    protected override void OnExit(ExitEventArgs e)
    {
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
                    string? videoPath = null;
                    if (overlay.Action == CaptureAction.Video)
                    {
                        var dialog = new Microsoft.Win32.SaveFileDialog
                        {
                            Filter = "Motion JPEG video|*.avi", FileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}",
                            AddExtension = true, DefaultExt = ".avi"
                        };
                        if (dialog.ShowDialog() != true) { _main.ShowStatus("Recording cancelled."); return; }
                        videoPath = dialog.FileName;
                    }
                    var session = new CaptureSessionWindow(overlay.SelectedBounds, image, overlay.Action == CaptureAction.Video, videoPath);
                    session.ShowDialog();
                    if (!session.Completed) { _main.ShowStatus("Capture cancelled."); return; }
                    if (videoPath != null) { _main.ShowStatus("Video saved: " + videoPath); return; }
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
                try { status = "Saved: " + FileSaveService.Save(image, _config.OutputFolder, _config.Format); }
                catch (Exception ex) { status = "Autosave failed: " + ex.Message; }
            }
            if (_config.CopyToClipboard)
            {
                try { ClipboardService.SetImage(image); status += " • Copied"; }
                catch (Exception) { status += " • Clipboard busy; use Copy image to retry."; }
            }
            _main.ShowStatus(status);
            new PreviewWindow(image, status).Show();
        }
        catch (Exception ex) { _main.ShowStatus("Capture failed: " + ex.Message); }
        finally
        {
            foreach (var window in hidden) window.Show();
            _capturing = false;
        }
    }
}
