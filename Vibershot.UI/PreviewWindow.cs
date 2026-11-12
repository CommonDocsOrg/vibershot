using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vibershot.Core;
namespace Vibershot.UI;

public partial class PreviewWindow : Window
{
    private readonly BitmapSource _image;
    private readonly AppConfig _config;
    public PreviewWindow(BitmapSource image, string status, AppConfig config)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _image = image;
        _config = config;
        Title = $"Vibershot — {image.PixelWidth} × {image.PixelHeight}";
        DimensionsLabel.Text = $"{image.PixelWidth:N0} × {image.PixelHeight:N0} pixels  ·  Screenshot preview";
        CaptureImage.Source = image;
        InfoLabel.Text = status;
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { ClipboardService.SetImage(_image); InfoLabel.Text = "Copied to clipboard. Ready to paste."; }
        catch (Exception ex) { InfoLabel.Text = "Could not copy: " + ex.Message; }
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).OpenEditor(_image);
        Close();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = ScreenshotSaveDialog.Filter, FilterIndex = ScreenshotSaveDialog.FilterIndexFor(_config.Format),
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var stream = File.Create(dialog.FileName);
            ImageEncoding.Encode(_image, ScreenshotSaveDialog.FormatFor(dialog.FilterIndex), _config.WebpQuality, stream);
            InfoLabel.Text = $"Saved: {dialog.FileName}";
        }
        catch (Exception ex) { InfoLabel.Text = $"Save failed: {ex.Message}"; }
    }
}
