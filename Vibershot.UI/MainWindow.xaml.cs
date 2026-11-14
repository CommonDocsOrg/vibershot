using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Vibershot.Core;
namespace Vibershot.UI;
public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    public MainWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        VersionLabel.Text = "v" + AppVersion.Current;
        RefreshShortcutChips();
    }
    /// <summary>
    /// Updates the footer line and colors the status dot from the message:
    /// green for a saved or copied capture, red for a failure, neutral otherwise.
    /// </summary>
    public void ShowStatus(string message)
    {
        StatusLabel.Text = message;
        string key = message.StartsWith("Saved", StringComparison.OrdinalIgnoreCase) || message.Contains("Copied", StringComparison.OrdinalIgnoreCase) ? "Ok"
            : message.Contains("failed", StringComparison.OrdinalIgnoreCase) || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || message.Contains("in use", StringComparison.OrdinalIgnoreCase) ? "Danger"
            : "Ink3";
        StatusDot.Fill = (Brush)FindResource(key);
    }
    /// <summary>Shows the configured shortcuts on the capture rows: Print Screen or the Full desktop hotkey, and the region hotkey.</summary>
    public void RefreshShortcutChips()
    {
        var region = _config.Hotkeys.Gesture(HotkeyAction.CaptureRegion);
        bool printScreenRegion = _config.PrintScreen == PrintScreenAction.CaptureTool;
        string regionText = printScreenRegion ? (region.IsValid ? "PrtScn · " + region : "PrtScn") : region.IsValid ? region.ToString() : "";
        RegionChipText.Text = regionText;
        RegionChip.Visibility = regionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.ToolTip = regionText.Length > 0 ? $"Select part of your screen ({regionText})" : "Select part of your screen";
        var full = _config.Hotkeys.Gesture(HotkeyAction.FullDesktop);
        string fullText = _config.PrintScreen == PrintScreenAction.FullDesktop ? "PrtScn" : full.IsValid ? full.ToString() : "";
        FullDesktopChipText.Text = fullText;
        FullDesktopChip.Visibility = fullText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>
    /// Closing the launcher hides it to the tray; the app keeps running so hotkeys still work.
    /// Exit from the tray menu shuts the app down explicitly.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons handle their own mouse events; only the empty header starts dragging.
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        // Segoe Fluent Icons: Pinned (tilted) when on, Pin (upright) when off; the soft fill marks the toggle as on.
        PinButton.Content = Topmost ? "" : "";
        PinButton.Foreground = (Brush)FindResource(Topmost ? "Accent" : "Ink2");
        PinButton.Background = Topmost ? (Brush)FindResource("AccentSoft") : Brushes.Transparent;
        PinButton.ToolTip = Topmost ? "Always on top · On" : "Always on top · Off";
        AutomationProperties.SetName(PinButton, Topmost ? "Turn off always on top" : "Turn on always on top");
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void CaptureButton_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).CaptureNow();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).CaptureNow(false);
    // Opens whatever image is on the clipboard; a blank canvas otherwise.
    private void Editor_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenEditorFromClipboard();
    private void VideoEditor_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenVideoEditor();
    private void Gallery_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowGallery();
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(_config) { Owner = this }.ShowDialog() == true)
            ((App)Application.Current).ApplySettings();
        // Focus returns to the gear when the dialog closes; drop it so no focus ring lingers there.
        Keyboard.ClearFocus();
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_config.OutputFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_config.OutputFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowStatus("Could not open folder: " + ex.Message); }
    }
}
