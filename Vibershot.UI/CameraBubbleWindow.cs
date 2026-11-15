using System.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Media.Camera;

namespace Vibershot.UI;

/// <summary>
/// The webcam's live picture, floating over the screen while a recording is set up and made.
/// It is left out of screen captures, so the camera stays its own track; where it sits over the
/// recorded region, and its shape, become the camera clip's starting place in the video editor.
/// Drag to move, wheel to resize, right-click for shape and mirror.
/// </summary>
public sealed class CameraBubbleWindow : Window
{
    public const int MinSize = 120, MaxSize = 720;

    private readonly WriteableBitmap _bitmap = new(CameraSession.PreviewWidth, CameraSession.PreviewHeight, 96, 96, PixelFormats.Bgr32, null);
    private readonly byte[] _frame = new byte[CameraSession.PreviewWidth * CameraSession.PreviewHeight * 4];
    private readonly Border _shape = new() { BorderBrush = new SolidColorBrush(Color.FromRgb(0xBD, 0x93, 0xF9)), BorderThickness = new Thickness(2), Background = Brushes.Black };
    private readonly ImageBrush _picture;
    private readonly TextBlock _note = new() { Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), Text = "Starting camera…" };
    private readonly Int32Rect _region;
    private CameraSession? _camera;
    private int _pending;
    private int _size;
    private double _scale = 1;

    public CameraBubbleWindow(Int32Rect region, string shape, bool mirror, int size)
    {
        _region = region; Shape = shape; Mirror = mirror; _size = Math.Clamp(size, MinSize, MaxSize);
        Title = "Vibershot camera";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.SizeAll;
        ToolTip = "Your camera. Drag to move, scroll to resize, right-click for shape.\nIt is recorded as its own layer, not as part of the screen.";
        _picture = new ImageBrush(_bitmap) { Stretch = Stretch.UniformToFill };
        var fill = new Border { Background = _picture };
        var grid = new Grid(); grid.Children.Add(fill); grid.Children.Add(_note);
        _shape.Child = grid;
        Content = _shape;
        ContextMenu = BuildMenu();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } };
        MouseWheel += (_, e) => Resize(_size + (e.Delta > 0 ? 20 : -20));
        SizeChanged += (_, _) => ApplyShape();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // No taskbar entry, never takes focus from the app being recorded, and absent from captures.
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x80 | 0x08000000);
            ExcludedFromCapture = SetWindowDisplayAffinity(handle, 0x11);
            _scale = ((HwndSource)PresentationSource.FromVisual(this)!).CompositionTarget.TransformToDevice.M11;
            var (width, height) = PixelSize();
            // Bottom right corner of the recorded region, a little way in.
            int margin = Math.Max(12, Math.Min(_region.Width, _region.Height) / 24);
            int x = _region.X + _region.Width - width - margin, y = _region.Y + _region.Height - height - margin;
            if (width + 2 * margin > _region.Width || height + 2 * margin > _region.Height) { x = _region.X + _region.Width + 12; y = _region.Y + _region.Height - height; }
            SetWindowPos(handle, new IntPtr(-1), x, y, width, height, 0x0010);
            ApplyShape();
        };
        Closed += (_, _) => Attach(null);
    }

    /// <summary>"Circle", "Rounded" or "Rectangle".</summary>
    public string Shape { get; private set; }
    public bool Mirror { get; private set; }
    public int BubbleSize => _size;

    /// <summary>False on Windows versions that cannot hide a window from capture: the bubble would be burned into the screen recording.</summary>
    public bool ExcludedFromCapture { get; private set; } = true;

    /// <summary>Raised when the user changes shape, mirror or size, so the choice can be remembered.</summary>
    public event Action? LookChanged;

    /// <summary>The bubble's box in screen pixels.</summary>
    public Int32Rect PixelBounds
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return default;
            return new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    /// <summary>Shows this camera's preview (null shows none).</summary>
    public void Attach(CameraSession? camera)
    {
        if (_camera != null) _camera.FrameArrived -= OnFrame;
        _camera = camera;
        _note.Text = camera == null ? "" : "Starting camera…";
        _note.Visibility = Visibility.Visible;
        if (camera != null) camera.FrameArrived += OnFrame;
    }

    public void ShowNote(string text) { _note.Text = text; _note.Visibility = Visibility.Visible; }

    private void OnFrame()
    {
        // Camera thread. Drawing happens on the UI thread; frames that arrive while one is waiting are skipped.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            var camera = _camera;
            if (camera == null) return;
            camera.CopyLatest(_frame);
            _bitmap.WritePixels(new Int32Rect(0, 0, CameraSession.PreviewWidth, CameraSession.PreviewHeight), _frame, CameraSession.PreviewWidth * 4, 0);
            if (_note.Visibility == Visibility.Visible) _note.Visibility = Visibility.Collapsed;
        });
    }

    private (int Width, int Height) PixelSize() => Shape == "Rectangle" ? (_size * 16 / 9, _size) : (_size, _size);

    private void Resize(int size)
    {
        size = Math.Clamp(size, MinSize, MaxSize);
        if (size == _size) return;
        var old = PixelBounds;
        _size = size;
        var (width, height) = PixelSize();
        var handle = new WindowInteropHelper(this).Handle;
        // Grow and shrink about the centre, so the bubble stays where it was put.
        if (handle != IntPtr.Zero) SetWindowPos(handle, IntPtr.Zero, old.X + (old.Width - width) / 2, old.Y + (old.Height - height) / 2, width, height, 0x0014);
        LookChanged?.Invoke();
    }

    private void SetShape(string shape)
    {
        if (Shape == shape) return;
        var old = PixelBounds;
        Shape = shape;
        var (width, height) = PixelSize();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowPos(handle, IntPtr.Zero, old.X + old.Width - width, old.Y + old.Height - height, width, height, 0x0014);
        ApplyShape();
        LookChanged?.Invoke();
    }

    private void ApplyShape()
    {
        double shorter = Math.Min(ActualWidth, ActualHeight);
        double radius = Shape == "Circle" ? shorter / 2 : Shape == "Rounded" ? shorter * 0.12 : 0;
        _shape.CornerRadius = new CornerRadius(radius);
        // A Border's corner radius does not cut its child, so the picture is clipped to the same outline.
        if (_shape.Child is FrameworkElement child)
            child.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth - 4), Math.Max(0, ActualHeight - 4)), Math.Max(0, radius - 2), Math.Max(0, radius - 2));
        _picture.RelativeTransform = Mirror ? new ScaleTransform(-1, 1, 0.5, 0.5) : Transform.Identity;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        foreach (string shape in new[] { "Circle", "Rounded", "Rectangle" })
        {
            var item = new MenuItem { Header = shape == "Rounded" ? "Rounded square" : shape, IsCheckable = true };
            item.Click += (_, _) => SetShape(shape);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var (label, size) in new[] { ("Small", 160), ("Medium", 220), ("Large", 320) })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Resize(size);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var mirror = new MenuItem { Header = "Mirror", IsCheckable = true };
        mirror.Click += (_, _) => { Mirror = !Mirror; ApplyShape(); LookChanged?.Invoke(); };
        menu.Items.Add(mirror);
        menu.Opened += (_, _) =>
        {
            for (int i = 0; i < 3; i++) ((MenuItem)menu.Items[i]).IsChecked = (string)((MenuItem)menu.Items[i]).Header == (Shape == "Rounded" ? "Rounded square" : Shape);
            mirror.IsChecked = Mirror;
        };
        return menu;
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
