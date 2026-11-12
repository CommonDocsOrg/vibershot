using System.Windows.Media.Imaging;
using Vibershot.Core;
namespace Vibershot.UI;

public partial class PreviewWindow : Window
{
    private readonly BitmapSource _image;
    private readonly AppConfig _config;
    private readonly Vibershot.Gallery.CaptureSidecar? _sidecar;
    /// <param name="sidecar">Gallery metadata for the capture; written next to any file saved from here.</param>
    public PreviewWindow(BitmapSource image, string status, AppConfig config, Vibershot.Gallery.CaptureSidecar? sidecar = null)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _image = image;
        _config = config;
        _sidecar = sidecar;
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
        try
        {
            var path = ScreenshotSaveDialog.Save(_image, _config, _sidecar, this);
            if (path != null) InfoLabel.Text = $"Saved: {path}";
        }
        catch (Exception ex) { InfoLabel.Text = $"Save failed: {ex.Message}"; }
    }
}
