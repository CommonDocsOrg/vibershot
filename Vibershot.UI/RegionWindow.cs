using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
namespace Vibershot.UI;
public enum CaptureAction { Screenshot, Video, Scrolling }

// Selection bounds and hit testing use physical desktop pixels; only drawing uses WPF units.
public sealed class RegionWindow : Window
{
    private readonly Canvas _canvas = new();
    // Overlay chrome follows the Dracula palette: purple selection, dark slate action bar and hint.
    private static readonly Color AccentColor = Color.FromRgb(0xBD, 0x93, 0xF9);
    private static readonly Color PaperColor = Color.FromRgb(0x28, 0x2A, 0x36), EdgeColor = Color.FromRgb(0x44, 0x47, 0x5A);
    private readonly Rectangle _selection = new() { Stroke = new SolidColorBrush(AccentColor), StrokeThickness = 2, IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = AccentColor, BlurRadius = 14, ShadowDepth = 0, Opacity = .8 } };
    private readonly System.Windows.Shapes.Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(150, 21, 24, 28)), IsHitTestVisible = false };
    private readonly Border _actions = new() { Background = new SolidColorBrush(PaperColor), BorderBrush = new SolidColorBrush(EdgeColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(4), Visibility = Visibility.Collapsed,
        Child = new StackPanel { Orientation = Orientation.Horizontal }, Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Opacity = .5 } };
    private readonly Border _hint = new() { Background = new SolidColorBrush(PaperColor), BorderBrush = new SolidColorBrush(EdgeColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), IsHitTestVisible = false,
        Child = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF2)), FontSize = 12, FontWeight = FontWeights.SemiBold } };
    private string HintText { set => ((TextBlock)_hint.Child).Text = value; }
    private UIElementCollection ActionButtons => ((StackPanel)_actions.Child).Children;
    private readonly WindowTargets _targets = new();
    private readonly HashSet<IntPtr> _queried = new();
    private readonly BitmapSource _desktop;
    private readonly Int32Rect _bounds;
    private readonly bool _showActions;
    private Point _start;
    private Rect _hover = Rect.Empty;
    private bool _dragging, _locked, _closed;
    private static int _providerBusy;
    public BitmapSource? Result { get; private set; }
    public Int32Rect SelectedBounds { get; private set; }
    public CaptureAction Action { get; private set; }
    /// <summary>Process name of the window under the selection, for the gallery sidecar; null when unknown.</summary>
    public string? SourceProcess { get; private set; }
    /// <summary>Title of that window at selection time; null when unknown.</summary>
    public string? SourceTitle { get; private set; }
    /// <summary>True when the user clicked a hovered window or content area rather than dragging a region.</summary>
    public bool SelectedTarget { get; private set; }
    private readonly CaptureAction? _preset;
    /// <param name="showActions">Offer screenshot, video or scrolling photo after the selection; otherwise take a screenshot at once.</param>
    /// <param name="preset">An action chosen before the tool opened (a "Record video" hotkey); the chooser is skipped.</param>
    public RegionWindow(BitmapSource desktop, Int32Rect bounds, bool showActions, CaptureAction? preset = null)
    {
        Title = "Vibershot region selection"; _desktop = desktop; _bounds = bounds; _showActions = showActions && preset is null; _preset = preset;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; Cursor = Cursors.Cross;
        Background = new ImageBrush(desktop) { Stretch = Stretch.Fill }; Content = _canvas;
        _canvas.Children.Add(_shade); _canvas.Children.Add(_selection);
        HintText = "Hover + click: window or content • Shift: whole window • Drag: region • Esc: cancel";
        Canvas.SetLeft(_hint, 24); Canvas.SetTop(_hint, 24); _canvas.Children.Add(_hint);
        AddAction("Save screenshot", () => Complete(CaptureAction.Screenshot));
        AddAction("Take video", () => Complete(CaptureAction.Video));
        AddAction("Scrolling photo", () => Complete(CaptureAction.Scrolling));
        AddAction("Cancel", Close); _canvas.Children.Add(_actions);
        SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        Loaded += (_, _) => UpdateHover();
        Closed += (_, _) => _closed = true;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); else if (!_locked && !_dragging) UpdateHover(); };
        KeyUp += (_, _) => { if (!_locked && !_dragging) UpdateHover(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_actions.IsMouseOver) return;
            _locked = false; Result = null; _actions.Visibility = Visibility.Collapsed;
            UpdateHover(); _start = PixelPoint(); _dragging = true; CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, _) =>
        {
            if (_dragging) { if ((PixelPoint() - _start).Length >= 4) Draw(new Rect(_start, PixelPoint())); }
            else if (!_locked) UpdateHover();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false; ReleaseMouseCapture();
            var end = PixelPoint();
            var pixels = (end - _start).Length < 4 ? _hover : new Rect(_start, end);
            if (pixels.IsEmpty) { UpdateHover(); return; }
            int x = (int)Math.Floor(pixels.X), y = (int)Math.Floor(pixels.Y);
            int w = Math.Min(bounds.Width, (int)Math.Ceiling(pixels.Right)) - x;
            int h = Math.Min(bounds.Height, (int)Math.Ceiling(pixels.Bottom)) - y;
            if (w < 2 || h < 2) { UpdateHover(); return; }
            Draw(new Rect(x, y, w, h)); _locked = true;
            SelectedTarget = (end - _start).Length < 4;
            (SourceProcess, SourceTitle) = Describe(_targets.At(new Point(_start.X + bounds.X, _start.Y + bounds.Y)));
            HintText = $"{w} × {h} • Choose an action • Hover + click or drag to replace • Esc: cancel";
            SelectedBounds = new Int32Rect(x + bounds.X, y + bounds.Y, w, h);
            Result = new CroppedBitmap(_desktop, new Int32Rect(x, y, w, h)); Result.Freeze();
            if (!_showActions) { Complete(_preset ?? CaptureAction.Screenshot); return; }
            _actions.Visibility = Visibility.Visible; _actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_actions, Math.Clamp(Canvas.GetLeft(_selection), 0, Math.Max(0, ActualWidth - _actions.DesiredSize.Width)));
            double below = Canvas.GetTop(_selection) + _selection.Height + 8;
            Canvas.SetTop(_actions, Math.Clamp(below + _actions.DesiredSize.Height <= ActualHeight ? below : Canvas.GetTop(_selection) - _actions.DesiredSize.Height - 8, 0, Math.Max(0, ActualHeight - _actions.DesiredSize.Height)));
            ((Button)ActionButtons[0]).Focus();
        };
    }
    private async void UpdateHover()
    {
        var local = PixelPoint(); var point = new Point(local.X + _bounds.X, local.Y + _bounds.Y);
        var target = _targets.At(point); _hover = Rect.Empty;
        if (target == null) { _selection.Width = 0; _selection.Height = 0; Shade(Rect.Empty); return; }
        var area = target.Bounds; string kind = "Window";
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var content = target.Areas.FindAll(r => r.Contains(point));
            content.Sort((a, b) => (a.Width * a.Height).CompareTo(b.Width * b.Height));
            if (content.Count > 0) { area = content[0]; kind = "App content"; }
            else if (target.Client.Contains(point) && target.Client != target.Bounds) { area = target.Client; kind = "Client area"; }
        }
        area.Intersect(new Rect(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height));
        if (!area.IsEmpty)
        {
            area.Offset(-_bounds.X, -_bounds.Y); _hover = area; Draw(area);
            HintText = $"{kind} • {(int)area.Width} × {(int)area.Height} • Click: select • Shift: whole window • Drag: region • Esc: cancel";
        }
        // One provider request at a time. A hung app must not block selection or queue more workers.
        if (!_queried.Contains(target.Handle) && System.Threading.Interlocked.CompareExchange(ref _providerBusy, 1, 0) == 0)
        {
            _queried.Add(target.Handle);
            try { var areas = await Task.Run(() => WindowTargets.DocumentAreas(target)); if (!_closed) target.Areas.AddRange(areas); }
            finally { System.Threading.Interlocked.Exchange(ref _providerBusy, 0); }
            if (!_closed && !_locked && !_dragging) UpdateHover();
        }
    }
    private void AddAction(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(12, 8, 12, 8), Cursor = Cursors.Hand };
        if (ActionButtons.Count == 0) button.Style = (Style)FindResource("PrimaryButton"); // The first action is the default one.
        button.Click += (_, _) => action(); ActionButtons.Add(button);
    }
    private void Complete(CaptureAction action) { Action = action; DialogResult = true; }
    // Source app and title for the gallery sidecar. Best effort: a protected process simply yields null.
    private static (string? Process, string? Title) Describe(WindowTargets.Target? target)
    {
        if (target == null) return (null, null);
        string? process = null, title = null;
        try { GetWindowThreadProcessId(target.Handle, out uint pid); if (pid != 0) { using var p = System.Diagnostics.Process.GetProcessById((int)pid); process = p.ProcessName; } } catch { }
        try { var text = new System.Text.StringBuilder(512); if (GetWindowText(target.Handle, text, text.Capacity) > 0) title = text.ToString(); } catch { }
        return (process, title);
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    private Point PixelPoint()
    {
        GetCursorPos(out var p);
        return new Point(Math.Clamp(p.X - _bounds.X, 0, _bounds.Width), Math.Clamp(p.Y - _bounds.Y, 0, _bounds.Height));
    }
    private void Shade(Rect region)
    {
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        if (!region.IsEmpty) geometry.Children.Add(new RectangleGeometry(region)); _shade.Data = geometry;
    }
    private void Draw(Rect pixels)
    {
        double sx = ActualWidth / _bounds.Width, sy = ActualHeight / _bounds.Height;
        var rect = new Rect(pixels.X * sx, pixels.Y * sy, pixels.Width * sx, pixels.Height * sy);
        Canvas.SetLeft(_selection, rect.X); Canvas.SetTop(_selection, rect.Y);
        _selection.Width = rect.Width; _selection.Height = rect.Height; Shade(rect);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Pixel { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pixel point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
