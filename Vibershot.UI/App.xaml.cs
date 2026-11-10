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
                var overlay = new RegionWindow(desktop, bounds);
                overlay.ShowDialog(); image = overlay.Result;
            }
            if (image == null) { _main.ShowStatus("Capture cancelled. Ctrl+Shift+S to try again."); return; }
            string status = $"{image.PixelWidth} × {image.PixelHeight}";
            if (_config.Autosave)
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
