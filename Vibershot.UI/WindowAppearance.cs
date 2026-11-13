using System.Windows.Interop;
namespace Vibershot.UI;

// Keep native title-bar dragging, system menu, keyboard shortcuts and resizing.
internal static class WindowAppearance
{
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            // Light caption in the Paper color (COLORREF is 0x00BBGGRR) with Ink text, rounded corners.
            int dark = 0, rounded = 2, caption = 0x00FFFFFF, text = 0x00262018;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        };
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
