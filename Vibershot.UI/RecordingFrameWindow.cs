using System.Windows.Interop;
using System.Windows.Media;

namespace Vibershot.UI;

/// <summary>
/// Click-through tangerine outline drawn just outside a region while it is being recorded, so the
/// user can see what the video covers. The stroke sits entirely outside the recorded pixels and the
/// window is excluded from capture, so it never appears in the recording either way.
/// </summary>
public sealed class RecordingFrameWindow : Window
{
    private const int Pad = 3; // Physical pixels: 1px dark hairline plus a 2px tangerine stroke.
    private static readonly Color AccentColor = Color.FromRgb(0xF0, 0x61, 0x2A);
    private readonly Int32Rect _region;
    private readonly Border _edge = new() { BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x14, 0x18, 0x1C)), CornerRadius = new CornerRadius(3) };
    private readonly Border _stroke = new() { BorderBrush = new SolidColorBrush(AccentColor), CornerRadius = new CornerRadius(2) };

    public RecordingFrameWindow(Int32Rect region)
    {
        _region = region;
        Title = "Vibershot recording frame";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; IsHitTestVisible = false; Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _edge.Child = _stroke; Content = _edge;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Mouse clicks pass through; no taskbar entry; never takes focus from the app being recorded.
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x20 | 0x80 | 0x08000000);
            SetWindowDisplayAffinity(handle, 0x11);
            var source = (HwndSource)PresentationSource.FromVisual(this)!;
            double scale = source.CompositionTarget.TransformToDevice.M11;
            _edge.BorderThickness = new Thickness(1 / scale);
            _stroke.BorderThickness = new Thickness((Pad - 1) / scale);
            SetWindowPos(handle, new IntPtr(-1), _region.X - Pad, _region.Y - Pad, _region.Width + 2 * Pad, _region.Height + 2 * Pad, 0x0010);
        };
    }

    /// <summary>Dims the outline while recording is paused.</summary>
    public bool Paused { set => Opacity = value ? 0.4 : 1.0; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
