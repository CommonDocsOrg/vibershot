using System.Windows;
using System.Windows.Media.Imaging;

namespace Vibershot.Core;

public static class ClipboardService
{
    public static void SetImage(BitmapSource image) => Clipboard.SetImage(image);

    public static BitmapSource? GetImage() => Clipboard.GetImage();
}
