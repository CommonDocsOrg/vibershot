using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vibershot.Core;
namespace Vibershot.UI;

public partial class PreviewWindow : Window
{
    private readonly BitmapSource _image;
    public PreviewWindow(BitmapSource image, string status)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _image = image;
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
            Filter = "PNG image|*.png|JPEG image|*.jpg",
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            BitmapEncoder encoder = dialog.FilterIndex == 2 ? new JpegBitmapEncoder() : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_image));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            InfoLabel.Text = $"Saved: {dialog.FileName}";
        }
        catch (Exception ex) { InfoLabel.Text = $"Save failed: {ex.Message}"; }
    }
}
