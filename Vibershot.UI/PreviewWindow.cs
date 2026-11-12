using System.Windows.Media.Imaging;
using Microsoft.Win32;
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
            using (var stream = File.Create(dialog.FileName))
                ImageEncoding.Encode(_image, ScreenshotSaveDialog.FormatFor(dialog.FilterIndex), _config.WebpQuality, stream);
            _sidecar?.TryWrite(dialog.FileName);
            InfoLabel.Text = $"Saved: {dialog.FileName}";
        }
        catch (Exception ex) { InfoLabel.Text = $"Save failed: {ex.Message}"; }
    }
}
