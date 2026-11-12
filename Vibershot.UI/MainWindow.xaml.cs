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
    }
    public void ShowStatus(string message) => StatusLabel.Text = message;
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
        PinButton.Foreground = Topmost ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(160, 180, 195));
        PinButton.ToolTip = Topmost ? "Always on top · On" : "Always on top · Off";
        AutomationProperties.SetName(PinButton, Topmost ? "Turn off always on top" : "Turn on always on top");
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void CaptureButton_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).CaptureNow();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).CaptureNow(false);
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(_config) { Owner = this }.ShowDialog() == true)
            ((App)Application.Current).ApplyPrintScreenSetting();
    }
}
