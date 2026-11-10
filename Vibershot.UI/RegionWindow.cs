using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Vibershot.UI;

public enum CaptureAction { Screenshot, Video, Scrolling }

// All coordinates in SelectedBounds are physical desktop pixels, including negative monitor origins.
public sealed class RegionWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new() { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(155, 20, 24, 30)), IsHitTestVisible = false };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Color.FromRgb(25, 35, 45)), Visibility = Visibility.Collapsed };
    private readonly BitmapSource _desktop;
    private readonly Int32Rect _bounds;
    private readonly bool _showActions;
    private Point _start;
    private bool _dragging;
    public BitmapSource? Result { get; private set; }
    public Int32Rect SelectedBounds { get; private set; }
    public CaptureAction Action { get; private set; }

    public RegionWindow(BitmapSource desktop, Int32Rect bounds, bool showActions)
    {
        Title = "Vibershot region selection"; _desktop = desktop; _bounds = bounds; _showActions = showActions;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; Cursor = Cursors.Cross;
        Background = new ImageBrush(desktop) { Stretch = Stretch.Fill }; Content = _canvas;
        _canvas.Children.Add(_shade); _canvas.Children.Add(_selection);
        var label = new TextBlock { Text = "Drag a region • Choose an action • Esc to cancel", Foreground = Brushes.White, Background = Brushes.Black, Padding = new Thickness(10), IsHitTestVisible = false };
        Canvas.SetLeft(label, 24); Canvas.SetTop(label, 24); _canvas.Children.Add(label);
        AddAction("Save screenshot", () => Complete(CaptureAction.Screenshot));
        AddAction("Take video", () => Complete(CaptureAction.Video));
        AddAction("Scrolling photo", () => Complete(CaptureAction.Scrolling));
        AddAction("Cancel", Close);
        _canvas.Children.Add(_actions);
        SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        Loaded += (_, _) => Shade(Rect.Empty);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_actions.IsMouseOver) return;
            Result = null; _actions.Visibility = Visibility.Collapsed;
            _start = PixelPoint(); _dragging = true; CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, _) => { if (_dragging) DrawSelection(PixelPoint()); };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false; ReleaseMouseCapture();
            var end = PixelPoint(); DrawSelection(end);
            int x = (int)Math.Min(_start.X, end.X), y = (int)Math.Min(_start.Y, end.Y);
            int w = (int)Math.Abs(end.X - _start.X), h = (int)Math.Abs(end.Y - _start.Y);
            if (w < 2 || h < 2) return;
            SelectedBounds = new Int32Rect(x + bounds.X, y + bounds.Y, w, h);
            Result = new CroppedBitmap(_desktop, new Int32Rect(x, y, w, h)); Result.Freeze();
            if (!_showActions) { Complete(CaptureAction.Screenshot); return; }
            _actions.Visibility = Visibility.Visible; _actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_actions, Math.Clamp(Canvas.GetLeft(_selection), 0, Math.Max(0, ActualWidth - _actions.DesiredSize.Width)));
            double below = Canvas.GetTop(_selection) + _selection.Height + 8;
            Canvas.SetTop(_actions, Math.Clamp(below + _actions.DesiredSize.Height <= ActualHeight ? below : Canvas.GetTop(_selection) - _actions.DesiredSize.Height - 8, 0, Math.Max(0, ActualHeight - _actions.DesiredSize.Height)));
            ((Button)_actions.Children[0]).Focus();
        };
    }
    private void AddAction(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(10, 9, 10, 9), Cursor = Cursors.Hand };
        button.Click += (_, _) => action(); _actions.Children.Add(button);
    }
    private void Complete(CaptureAction action) { Action = action; DialogResult = true; }
    private Point PixelPoint()
    {
        GetCursorPos(out var p);
        return new Point(Math.Clamp(p.X - _bounds.X, 0, _bounds.Width), Math.Clamp(p.Y - _bounds.Y, 0, _bounds.Height));
    }
    private void Shade(Rect region)
    {
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        if (!region.IsEmpty) geometry.Children.Add(new RectangleGeometry(region));
        _shade.Data = geometry;
    }
    private void DrawSelection(Point end)
    {
        double sx = ActualWidth / _bounds.Width, sy = ActualHeight / _bounds.Height;
        var rect = new Rect(Math.Min(_start.X, end.X) * sx, Math.Min(_start.Y, end.Y) * sy, Math.Abs(end.X - _start.X) * sx, Math.Abs(end.Y - _start.Y) * sy);
        Canvas.SetLeft(_selection, rect.X); Canvas.SetTop(_selection, rect.Y);
        _selection.Width = rect.Width; _selection.Height = rect.Height; Shade(rect);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Pixel { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pixel point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
