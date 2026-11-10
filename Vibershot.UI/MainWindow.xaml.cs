using Vibershot.Core;
namespace Vibershot.UI;
public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    public MainWindow(AppConfig config) { InitializeComponent(); WindowAppearance.Apply(this); _config = config; }
    public void ShowStatus(string message) => StatusLabel.Text = message;
    private void CaptureButton_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).CaptureNow();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).CaptureNow(false);
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => new SettingsWindow(_config) { Owner = this }.ShowDialog();
}
