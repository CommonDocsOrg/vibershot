using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vibershot.Core;

namespace Vibershot.UI;

public static class ScreenshotSaveDialog
{
    public static string? Save(BitmapSource image, AppConfig config)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png|JPEG image|*.jpg", FilterIndex = config.Format == ImageFormat.Jpeg ? 2 : 1,
            InitialDirectory = Directory.Exists(config.OutputFolder) ? config.OutputFolder : "",
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}", AddExtension = true
        };
        if (dialog.ShowDialog() != true) return null;
        BitmapEncoder encoder = dialog.FilterIndex == 2 ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(dialog.FileName); encoder.Save(stream);
        return dialog.FileName;
    }
}
