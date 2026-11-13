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

    // Fixed-size mode: a frame of an exact pixel size follows the pointer and a click places it.
    private static readonly (string Label, int Width, int Height)[] SizePresets = { ("720p", 1280, 720), ("1080p", 1920, 1080), ("1440p", 2560, 1440), ("Square", 1080, 1080) };
    private readonly Border _sizeBar = new() { Background = new SolidColorBrush(PaperColor), BorderBrush = new SolidColorBrush(EdgeColor), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 8, 6), Cursor = Cursors.Arrow,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Opacity = .5 } };
    private readonly List<System.Windows.Controls.Primitives.ToggleButton> _sizeChips = new();
    private readonly StackPanel _customPanel = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _customWidth = new(), _customHeight = new();
    private Size? _fixedSize;
    /// <summary>The custom fixed size last typed into the size bar, in screen pixels; the caller stores it for next time.</summary>
    public (int Width, int Height) CustomSize { get; private set; } = (1280, 720);

    /// <param name="showActions">Offer screenshot, video or scrolling photo after the selection; otherwise take a screenshot at once.</param>
    /// <param name="preset">An action chosen before the tool opened (a "Record video" hotkey); the chooser is skipped.</param>
    /// <param name="customSize">The custom fixed size to offer in the size bar.</param>
    public RegionWindow(BitmapSource desktop, Int32Rect bounds, bool showActions, CaptureAction? preset = null, (int Width, int Height)? customSize = null)
    {
        if (customSize is { } remembered) CustomSize = remembered;
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
        BuildSizeBar(); Canvas.SetLeft(_sizeBar, 24); Canvas.SetTop(_sizeBar, 68); _canvas.Children.Add(_sizeBar);
        SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        Loaded += (_, _) => UpdateHover();
        Closed += (_, _) => _closed = true;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        KeyDown += (_, _) => { if (!_locked && !_dragging) UpdateHover(); };
        KeyUp += (_, _) => { if (!_locked && !_dragging) UpdateHover(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_actions.IsMouseOver || _sizeBar.IsMouseOver) return;
            _locked = false; Result = null; _actions.Visibility = Visibility.Collapsed;
            if (_fixedSize is not null)
            {
                // A fixed-size frame is placed with one click, centred on the pointer.
                _start = PixelPoint(); Select(FixedRect(_start), target: false); e.Handled = true;
                return;
            }
            UpdateHover(); _start = PixelPoint(); _dragging = true; CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, _) =>
        {
            if (_dragging) { if ((PixelPoint() - _start).Length >= 4) Draw(new Rect(_start, PixelPoint())); }
            else if (_locked || _sizeBar.IsMouseOver) return;
            else if (_fixedSize is not null) ShowFixedFrame();
            else UpdateHover();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false; ReleaseMouseCapture();
            var end = PixelPoint();
            var pixels = (end - _start).Length < 4 ? _hover : new Rect(_start, end);
            Select(pixels, (end - _start).Length < 4);
        };
    }

    /// <summary>Locks a selection (in overlay pixels) and either finishes at once or shows the action bar beside it.</summary>
    private void Select(Rect pixels, bool target)
    {
        if (pixels.IsEmpty) { UpdateHover(); return; }
        int x = (int)Math.Floor(pixels.X), y = (int)Math.Floor(pixels.Y);
        int w = Math.Min(_bounds.Width, (int)Math.Ceiling(pixels.Right)) - x;
        int h = Math.Min(_bounds.Height, (int)Math.Ceiling(pixels.Bottom)) - y;
        if (w < 2 || h < 2) { UpdateHover(); return; }
        Draw(new Rect(x, y, w, h)); _locked = true;
        SelectedTarget = target;
        (SourceProcess, SourceTitle) = Describe(_targets.At(new Point(_start.X + _bounds.X, _start.Y + _bounds.Y)));
        HintText = _fixedSize is not null
            ? $"{w} × {h} • Choose an action • Click elsewhere to move the frame • Esc: cancel"
            : $"{w} × {h} • Choose an action • Hover + click or drag to replace • Esc: cancel";
        SelectedBounds = new Int32Rect(x + _bounds.X, y + _bounds.Y, w, h);
        Result = new CroppedBitmap(_desktop, new Int32Rect(x, y, w, h)); Result.Freeze();
        if (!_showActions) { Complete(_preset ?? CaptureAction.Screenshot); return; }
        _actions.Visibility = Visibility.Visible; _actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_actions, Math.Clamp(Canvas.GetLeft(_selection), 0, Math.Max(0, ActualWidth - _actions.DesiredSize.Width)));
        double below = Canvas.GetTop(_selection) + _selection.Height + 8;
        Canvas.SetTop(_actions, Math.Clamp(below + _actions.DesiredSize.Height <= ActualHeight ? below : Canvas.GetTop(_selection) - _actions.DesiredSize.Height - 8, 0, Math.Max(0, ActualHeight - _actions.DesiredSize.Height)));
        ((Button)ActionButtons[0]).Focus();
    }

    // ----- Fixed-size frames -----

    /// <summary>The size bar: Free (hover, click or drag as usual), a few standard frames, and a custom width × height.</summary>
    private void BuildSizeBar()
    {
        var ink3 = new SolidColorBrush(Color.FromRgb(0x62, 0x72, 0xA4));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "SIZE", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = ink3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) });
        AddSizeChip(row, "Free", "Pick a window, or drag any region", null);
        foreach (var (label, width, height) in SizePresets)
        {
            var chip = AddSizeChip(row, label, $"A {width} × {height} frame: move it into place and click", new Size(width, height));
            if (width > _bounds.Width || height > _bounds.Height) { chip.IsEnabled = false; chip.Opacity = .45; chip.ToolTip = $"{width} × {height} is larger than your desktop"; }
        }
        var custom = AddSizeChip(row, "Custom", "A frame of the exact size you type", null);
        custom.Tag = "custom";
        foreach (var (box, value, name) in new[] { (_customWidth, CustomSize.Width, "Frame width in pixels"), (_customHeight, CustomSize.Height, "Frame height in pixels") })
        {
            box.Text = value.ToString(); box.Width = 62; box.Padding = new Thickness(8, 5, 8, 5); box.FontSize = 12; box.TextAlignment = TextAlignment.Center; box.VerticalAlignment = VerticalAlignment.Center;
            box.FontFamily = (FontFamily)FindResource("MonoFont"); box.ToolTip = name; System.Windows.Automation.AutomationProperties.SetName(box, name);
            box.TextChanged += (_, _) => ApplyCustomSize();
            box.GotKeyboardFocus += (_, _) => box.SelectAll();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Keyboard.ClearFocus(); Focus(); e.Handled = true; } };
        }
        _customPanel.Children.Add(_customWidth);
        _customPanel.Children.Add(new TextBlock { Text = "×", Foreground = ink3, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
        _customPanel.Children.Add(_customHeight);
        _customPanel.Children.Add(new TextBlock { Text = "px", Foreground = ink3, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        row.Children.Add(_customPanel);
        _sizeBar.Child = row;
        _sizeChips[0].IsChecked = true;
    }

    private System.Windows.Controls.Primitives.ToggleButton AddSizeChip(StackPanel row, string label, string tip, Size? size)
    {
        var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = label, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip, Tag = size, Focusable = false };
        System.Windows.Automation.AutomationProperties.SetName(chip, label + " size");
        chip.Click += (_, _) => ChooseSize(chip);
        _sizeChips.Add(chip); row.Children.Add(chip);
        return chip;
    }

    private void ChooseSize(System.Windows.Controls.Primitives.ToggleButton chosen)
    {
        foreach (var chip in _sizeChips) chip.IsChecked = chip == chosen;
        bool custom = chosen.Tag as string == "custom";
        _customPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        _locked = false; Result = null; _actions.Visibility = Visibility.Collapsed;
        if (custom) { ApplyCustomSize(); _customWidth.Focus(); return; }
        _fixedSize = chosen.Tag as Size?;
        Focus();
        if (_fixedSize is null)
        {
            HintText = "Hover + click: window or content • Shift: whole window • Drag: region • Esc: cancel";
            UpdateHover();
        }
        else ShowFixedFrame();
    }

    /// <summary>Reads the custom boxes; a readable size becomes the frame, anything else waits for more typing.</summary>
    private void ApplyCustomSize()
    {
        if (_customPanel.Visibility != Visibility.Visible) return;
        if (int.TryParse(_customWidth.Text.Trim(), out int w) && int.TryParse(_customHeight.Text.Trim(), out int h) && w >= 16 && h >= 16)
        {
            CustomSize = (w, h);
            _fixedSize = new Size(Math.Min(w, _bounds.Width), Math.Min(h, _bounds.Height));
            if (!_locked) ShowFixedFrame();
        }
        else HintText = "Type a width and height of at least 16 px";
    }

    /// <summary>The fixed frame centred on a point, pushed back inside the desktop.</summary>
    private Rect FixedRect(Point centre)
    {
        var size = _fixedSize ?? new Size(1280, 720);
        double w = Math.Min(size.Width, _bounds.Width), h = Math.Min(size.Height, _bounds.Height);
        double x = Math.Clamp(Math.Round(centre.X - w / 2), 0, _bounds.Width - w), y = Math.Clamp(Math.Round(centre.Y - h / 2), 0, _bounds.Height - h);
        return new Rect(x, y, w, h);
    }

    private void ShowFixedFrame()
    {
        var frame = FixedRect(PixelPoint());
        _hover = Rect.Empty;
        Draw(frame);
        bool clipped = _fixedSize is { } wanted && (wanted.Width != frame.Width || wanted.Height != frame.Height || CustomSize.Width > _bounds.Width && _customPanel.Visibility == Visibility.Visible || CustomSize.Height > _bounds.Height && _customPanel.Visibility == Visibility.Visible);
        HintText = $"{(int)frame.Width} × {(int)frame.Height}{(clipped ? " (limited to your desktop)" : "")} • Move the frame into place • Click: place it • Esc: cancel";
    }
    private async void UpdateHover()
    {
        if (_fixedSize is not null) { if (!_locked) ShowFixedFrame(); return; }
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
