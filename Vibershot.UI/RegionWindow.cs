using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Vibershot.Core;

namespace Vibershot.UI;

// Select from a frozen desktop so the overlay never appears in the captured image.
public sealed class RegionWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new() { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(35, 134, 227, 205)) };
    private readonly BitmapSource _desktop;
    private readonly Int32Rect _bounds;
    private Point _start;
    private bool _dragging;
    public BitmapSource? Result { get; private set; }

    public RegionWindow(BitmapSource desktop, Int32Rect bounds)
    {
        Title = "Vibershot region selection"; _desktop = desktop; _bounds = bounds;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = true; Cursor = Cursors.Cross;
        Background = new ImageBrush(desktop) { Stretch = Stretch.Fill };
        Content = _canvas;
        _canvas.Children.Add(_selection);
        var label = new TextBlock { Text = "Drag to capture a region  •  Esc to cancel", Foreground = Brushes.White, Background = Brushes.Black, Padding = new Thickness(12), FontSize = 16 };
        Canvas.SetLeft(label, 24); Canvas.SetTop(label, 24); _canvas.Children.Add(label);
        SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, _) => { _start = PixelPoint(); _dragging = true; CaptureMouse(); };
        MouseMove += (_, _) => { if (_dragging) DrawSelection(PixelPoint()); };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false; ReleaseMouseCapture();
            var end = PixelPoint();
            int x = (int)Math.Min(_start.X, end.X), y = (int)Math.Min(_start.Y, end.Y);
            int w = (int)Math.Abs(end.X - _start.X), h = (int)Math.Abs(end.Y - _start.Y);
            if (w < 2 || h < 2) return;
            Result = new CroppedBitmap(_desktop, new Int32Rect(x, y, w, h)); Result.Freeze();
            DialogResult = true;
        };
    }
    private Point PixelPoint()
    {
        GetCursorPos(out var p);
        return new Point(Math.Clamp(p.X - _bounds.X, 0, _bounds.Width), Math.Clamp(p.Y - _bounds.Y, 0, _bounds.Height));
    }
    private void DrawSelection(Point end)
    {
        double sx = ActualWidth / _bounds.Width, sy = ActualHeight / _bounds.Height;
        Canvas.SetLeft(_selection, Math.Min(_start.X, end.X) * sx);
        Canvas.SetTop(_selection, Math.Min(_start.Y, end.Y) * sy);
        _selection.Width = Math.Abs(end.X - _start.X) * sx;
        _selection.Height = Math.Abs(end.Y - _start.Y) * sy;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Pixel { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pixel point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
