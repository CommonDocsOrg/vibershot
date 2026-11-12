using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Vibershot.Core;
using Vibershot.Gallery;
using Forms = System.Windows.Forms;

namespace Vibershot.UI;
public partial class App : Application
{
    private readonly AppConfig _config = AppConfig.Load();
    private MainWindow _main = null!;
    private GalleryWindow? _gallery;
    private HotkeyManager? _hotkeys;
    private HotkeyService? _printScreen;
    private Forms.NotifyIcon? _tray;
    private bool _capturing, _setupShown;
    public AppConfig Config => _config;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _main = new MainWindow(_config); MainWindow = _main;
        // Launched at sign-in: stay in the tray until the user asks for the launcher.
        bool startInTray = e.Args.Contains(StartupRegistration.TrayArgument, StringComparer.OrdinalIgnoreCase);
        if (!startInTray) _main.Show();
        StartupRegistration.RefreshIfEnabled();
        // Global hotkeys hang off the launcher's HWND, which only exists once the window is shown
        // or its handle is forced; a tray launch never shows it, so force it here.
        new System.Windows.Interop.WindowInteropHelper(_main).EnsureHandle();
        _hotkeys = new HotkeyManager(_main);
        _hotkeys.Triggered += OnHotkey;
        ApplyHotkeySettings();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show capture window", null, (_, _) => ShowLauncher());
        menu.Items.Add("Capture region", null, (_, _) => CaptureNow());
        menu.Items.Add("Full desktop", null, (_, _) => CaptureNow(false));
        menu.Items.Add("Gallery", null, (_, _) => ShowGallery());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Vibershot • running in tray", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowLauncher();
        ProbeFfmpeg();
        if (!startInTray) ShowFirstRunIfNeeded();
        // "--edit <file>" opens an image or .vibeshot project straight in the editor.
        int edit = Array.FindIndex(e.Args, a => a.Equals("--edit", StringComparison.OrdinalIgnoreCase));
        if (edit >= 0 && edit + 1 < e.Args.Length) OpenEditor(e.Args[edit + 1]);
        RunLibraryCleanup();
    }
    private void ShowLauncher()
    {
        _main.Show(); _main.WindowState = WindowState.Normal; _main.Activate();
        ShowFirstRunIfNeeded();
    }
    /// <summary>
    /// The first-run page (library location and Print Screen) on a fresh install. Shown once per
    /// session at most, and only when a window is on screen, so a tray launch waits until the
    /// user asks for the launcher or makes a capture.
    /// </summary>
    private void ShowFirstRunIfNeeded()
    {
        if (_config.SetupCompleted || _setupShown) return;
        _setupShown = true;
        try
        {
            if (new FirstRunWindow(_config) { Owner = _main.IsVisible ? _main : null }.ShowDialog() == true) ApplySettings();
        }
        catch (Exception ex) { _main.ShowStatus("Setup page failed: " + ex.Message); }
    }
    /// <summary>Reports a missing or broken ffmpeg in the launcher footer instead of at the first recording.</summary>
    private async void ProbeFfmpeg()
    {
        if (_config.Video.Format != Vibershot.Core.VideoFormat.Mp4) return;
        var probe = await Task.Run(Vibershot.Media.FfmpegLocator.Probe);
        if (!probe.Ok) _main.ShowStatus("MP4 recording unavailable: " + probe.Error + " Videos record as AVI.");
    }
    /// <summary>Tidies old library files in the background when the cleanup setting is on and a day has passed.</summary>
    private async void RunLibraryCleanup()
    {
        try
        {
            var report = await Task.Run(() => LibraryCleanup.RunIfDue(_config, DateTime.Now));
            if (report is { Count: > 0 }) { _config.Save(); _main.ShowStatus(report.Summary); }
            else if (report != null) _config.Save();
        }
        catch (Exception ex) { _main.ShowStatus("Library cleanup failed: " + ex.Message); }
    }
    /// <summary>Re-applies everything the settings window can change at run time. Safe to call again.</summary>
    public void ApplySettings()
    {
        ApplyHotkeySettings();
        _main.RefreshShortcutChips();
    }
    /// <summary>Registers the configured shortcuts and Print Screen, reporting whatever could not be taken.</summary>
    public void ApplyHotkeySettings()
    {
        _hotkeys?.Apply(_config.Hotkeys);
        ApplyPrintScreenSetting();
        var problems = new List<string>();
        if (_hotkeys != null)
            foreach (var (action, gesture) in _hotkeys.Failures) problems.Add($"{gesture} ({HotkeySettings.Label(action)}) is in use by another app");
        if (problems.Count > 0) { _main.ShowStatus(string.Join(" • ", problems) + ". Change it in Settings › Hotkeys."); return; }
        var region = _hotkeys?.Active(HotkeyAction.CaptureRegion) ?? "";
        if (_config.PrintScreen == PrintScreenAction.Off && _printScreen == null)
            _main.ShowStatus(region.Length > 0 ? $"{region} • Select a region • Esc cancels" : "No capture shortcut set. Use Capture region or Settings › Hotkeys.");
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
        var region = _hotkeys?.Active(HotkeyAction.CaptureRegion) ?? "";
        string also = region.Length > 0 ? $" • {region} selects a region" : "";
        try
        {
            _printScreen = new HotkeyService(_main, 2, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SNAPSHOT, "Print Screen");
            _printScreen.Triggered += (_, _) => CaptureNow(region: _config.PrintScreen == PrintScreenAction.CaptureTool);
            _main.ShowStatus(action == PrintScreenAction.FullDesktop
                ? "Print Screen snaps the full desktop" + also
                : (region.Length > 0 ? $"Print Screen or {region}" : "Print Screen") + " • Select a region • Esc cancels");
        }
        catch (Exception)
        {
            _main.ShowStatus("Print Screen is in use by another app. In Windows Settings, turn off \"Use the Print screen key to open screen capture\".");
        }
    }
    /// <summary>True when Windows (or another app) currently owns Print Screen, so the takeover could not register.</summary>
    public bool PrintScreenBlocked => _config.PrintScreen != PrintScreenAction.Off && _printScreen == null;
    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.CaptureRegion: CaptureNow(); break;
            case HotkeyAction.FullDesktop: CaptureNow(false); break;
            case HotkeyAction.RecordVideo: CaptureNow(true, CaptureAction.Video); break;
            case HotkeyAction.OpenGallery: ShowGallery(); break;
            case HotkeyAction.OpenEditor: OpenEditorFromClipboard(); break;
        }
    }
    /// <summary>Opens the editor on whatever image is on the clipboard, or on a blank canvas.</summary>
    public void OpenEditorFromClipboard()
    {
        System.Windows.Media.Imaging.BitmapSource? image = null;
        try { image = ClipboardService.GetImage(); } catch (Exception) { }
        OpenEditor(image);
    }
    /// <summary>Opens the image editor on <paramref name="image"/>, or on a blank 1280×720 canvas when null.</summary>
    public void OpenEditor(System.Windows.Media.Imaging.BitmapSource? image)
    {
        try { new EditorWindow(_config, image).Show(); }
        catch (Exception ex) { _main.ShowStatus("Editor failed to open: " + ex.Message); }
    }
    /// <summary>Opens an image file or a .vibeshot project from the gallery in the editor.</summary>
    public void OpenEditor(string path)
    {
        try
        {
            if (string.Equals(System.IO.Path.GetExtension(path), Vibershot.Editing.ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            {
                EditorWindow.OpenProject(_config, path).Show();
                return;
            }
            EditorWindow.OpenImage(_config, path).Show();
        }
        catch (Exception ex) { _main.ShowStatus("Editor failed to open: " + ex.Message); }
    }
    /// <summary>
    /// Shows the gallery (one instance per session), optionally selecting a file once it is indexed.
    /// Images and projects open in the image editor; videos still open with the shell until the video editor exists.
    /// </summary>
    public void ShowGallery(string? selectPath = null)
    {
        if (_gallery == null)
        {
            _gallery = new GalleryWindow(_config) { OpenInEditor = item => OpenEditor(item.Path) };
            _gallery.Closed += (_, _) => _gallery = null;
        }
        _gallery.Show();
        if (_gallery.WindowState == WindowState.Minimized) _gallery.WindowState = WindowState.Normal;
        _gallery.Activate();
        if (selectPath != null) _gallery.SelectFile(selectPath);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _printScreen?.Dispose();
        _hotkeys?.Dispose();
        var menu = _tray?.ContextMenuStrip; _tray?.Dispose(); menu?.Dispose();
        base.OnExit(e);
    }
    /// <param name="region">Open the region tool; otherwise snap the full desktop at once.</param>
    /// <param name="preset">Skip the action chooser and take this action after the selection (a Record video hotkey).</param>
    public async void CaptureNow(bool region = true, CaptureAction? preset = null)
    {
        if (_capturing) return;
        ShowFirstRunIfNeeded();
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
            // Gallery sidecar facts: how the capture was made and which app it came from.
            var kind = CaptureKind.FullDesktop; string? sourceProcess = null, sourceTitle = null;
            if (region)
            {
                var overlay = new RegionWindow(desktop, bounds, _config.ShowCaptureActions, preset);
                image = overlay.ShowDialog() == true ? overlay.Result : null;
                sourceProcess = overlay.SourceProcess; sourceTitle = overlay.SourceTitle;
                kind = overlay.Action == CaptureAction.Scrolling ? CaptureKind.Scrolling : overlay.SelectedTarget ? CaptureKind.Window : CaptureKind.Region;
                if (image != null && overlay.Action != CaptureAction.Screenshot)
                {
                    var session = new CaptureSessionWindow(overlay.SelectedBounds, image, overlay.Action == CaptureAction.Video, null, _config.Video, sourceProcess, sourceTitle, _config.Gallery.RecordingsFolder);
                    session.ShowDialog();
                    if (overlay.Action == CaptureAction.Video) { _main.ShowStatus(session.VideoPath == null ? "No video recorded." : "Video available: " + session.VideoPath); return; }
                    if (!session.Completed) { _main.ShowStatus("Capture cancelled."); return; }
                    image = session.Image;
                }
            }
            if (image == null) { _main.ShowStatus("Capture cancelled." + RetryHint()); return; }
            FinishScreenshot(image, CaptureSidecar.ForImage(image, kind, sourceProcess, sourceTitle));
        }
        catch (Exception ex) { _main.ShowStatus("Capture failed: " + ex.Message); }
        finally
        {
            foreach (var window in hidden) window.Show();
            _capturing = false;
        }
    }
    private string RetryHint()
    {
        var region = _hotkeys?.Active(HotkeyAction.CaptureRegion) ?? "";
        return region.Length > 0 ? $" {region} to try again." : "";
    }
    /// <summary>
    /// Runs the After capture switches in order: save to every destination, copy, then open the
    /// gallery and/or the editor, falling back to the preview when neither opens. Every step
    /// reports into the launcher footer; one failing step never stops the others.
    /// </summary>
    private void FinishScreenshot(System.Windows.Media.Imaging.BitmapSource image, CaptureSidecar sidecar)
    {
        var after = _config.AfterCapture;
        var status = new List<string> { $"{image.PixelWidth} × {image.PixelHeight}" };
        var saved = new List<string>();
        string? libraryCopy = null;
        if (after.SaveAsFile)
        {
            if (after.AskWhereToSave)
            {
                try
                {
                    var path = ScreenshotSaveDialog.Save(image, _config, sidecar);
                    if (path != null) { saved.Add(path); status.Add("Saved: " + path); }
                    else status.Add("Save cancelled");
                }
                catch (Exception ex) { status.Add("Save failed: " + ex.Message); }
            }
            else
            {
                var library = _config.Gallery.ScreenshotsFolder;
                foreach (var folder in after.EnabledFolders(_config.Gallery))
                {
                    try
                    {
                        var file = CaptureSaver.Save(image, folder, after.Format, _config, sidecar);
                        saved.Add(file.Path);
                        if (AfterCaptureSettings.SameFolder(folder, library)) libraryCopy = file.Path;
                    }
                    catch (Exception ex) { status.Add($"Save to {folder} failed: {ex.Message}"); }
                }
                if (saved.Count > 0) status.Add(saved.Count == 1 ? "Saved: " + saved[0] : $"Saved to {saved.Count} folders: " + saved[0]);
            }
        }
        if (after.CopyToClipboard)
        {
            try { ClipboardService.SetImage(image); status.Add("Copied"); }
            catch (Exception) { status.Add("Clipboard busy; use Copy image to retry"); }
        }
        _main.ShowStatus(string.Join(" • ", status));
        bool opened = false;
        if (after.OpenGallery && (libraryCopy ?? saved.FirstOrDefault()) is string select) { ShowGallery(select); opened = true; }
        if (after.OpenEditor)
        {
            // The saved project is the editor's document, so Ctrl+S writes back to the same file.
            var project = saved.FirstOrDefault(p => p.EndsWith(Vibershot.Editing.ProjectFile.Extension, StringComparison.OrdinalIgnoreCase));
            if (project != null) OpenEditor(project); else OpenEditor(image);
            opened = true;
        }
        if (!opened && after.ShowPreview) new PreviewWindow(image, string.Join(" • ", status), _config, sidecar).Show();
    }
}
