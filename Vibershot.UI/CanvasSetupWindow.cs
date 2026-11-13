using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Vibershot.Editing;

namespace Vibershot.UI;

/// <summary>
/// The editor's three canvas dialogs, built on one frame: <see cref="AskNew"/> (a blank picture of a
/// chosen size), <see cref="AskCanvasSize"/> (add or trim space on any side) and
/// <see cref="AskImageSize"/> (scale the whole picture). Each returns null when cancelled.
/// </summary>
internal sealed class CanvasSetupWindow : Window
{
    private static readonly (string Name, int Width, int Height)[] Presets =
    [
        ("720p", 1280, 720), ("1080p", 1920, 1080), ("1440p", 2560, 1440), ("4K", 3840, 2160),
        ("Square", 1080, 1080), ("Portrait", 1080, 1920), ("Banner", 1500, 500),
    ];

    private readonly StackPanel _body = new() { Margin = new Thickness(24, 20, 24, 20) };
    private readonly TextBlock _summary = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _accept = new() { IsDefault = true, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(18, 9, 18, 9) };
    private bool _sync;

    private CanvasSetupWindow(Window? owner, string title, string hint, string acceptLabel)
    {
        Title = title; Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = owner == null;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        if (owner != null) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else { WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; }
        WindowAppearance.Apply(this);
        _body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold });
        _body.Children.Add(new TextBlock { Text = hint, Style = (Style)Application.Current.FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) });
        _accept.Content = acceptLabel;
        _accept.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _accept.Click += (_, _) => DialogResult = true;
        Content = _body;
    }

    // ----- Building blocks -----

    private void Label(string text) => _body.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("GroupLabel"), Margin = new Thickness(0, 16, 0, 8) });

    private static TextBox NumberBox(string name, double value, double width = 92)
    {
        var box = new TextBox
        {
            Text = value.ToString("0.##", CultureInfo.CurrentCulture), Width = width, Padding = new Thickness(10, 7, 10, 7), FontSize = 12.5,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"), VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, name);
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.PreviewMouseLeftButtonDown += (_, e) => { if (!box.IsKeyboardFocusWithin) { box.Focus(); e.Handled = true; } };
        return box;
    }

    private static bool Read(TextBox box, out double value)
    {
        var text = box.Text.Trim().TrimEnd('%').Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void Set(TextBox box, double value)
    {
        if (box.IsKeyboardFocusWithin) return; // Never rewrite the box the user is typing in.
        bool was = _sync; _sync = true;
        box.Text = Math.Round(value, 2).ToString("0.##", CultureInfo.CurrentCulture);
        _sync = was;
    }

    private static TextBlock Caption(string text, double left = 0) => new() { Text = text, FontSize = 11.5, Foreground = (Brush)Application.Current.FindResource("Ink2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 8, 0) };

    private static ToggleButton Chip(string text, string? tip = null)
    {
        var chip = new ToggleButton { Content = text, Style = (Style)Application.Current.FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
        AutomationProperties.SetName(chip, text);
        return chip;
    }

    private void Footer()
    {
        var grid = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 9, 16, 9) });
        buttons.Children.Add(_accept);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(_summary);
        grid.Children.Add(buttons);
        _body.Children.Add(grid);
    }

    /// <summary>Shows the resulting size, or why it cannot be used, and enables the accept button to match.</summary>
    private void Summarize(double width, double height, string okText, bool readable = true)
    {
        bool ok = readable && DocumentOps.IsValidCanvas(width, height);
        _accept.IsEnabled = ok;
        _summary.Foreground = (Brush)Application.Current.FindResource(ok ? "Ink2" : "Danger");
        _summary.Text = ok ? okText
            : !readable ? "Enter whole numbers of pixels."
            : width < 1 || height < 1 ? "The canvas must be at least 1 × 1 px."
            : $"Too large: keep each side under {DocumentOps.MaxCanvasSide:N0} px and the picture under {DocumentOps.MaxCanvasPixels / 1_000_000} megapixels.";
    }

    private static string Megapixels(double width, double height) => width * height >= 1_000_000 ? $" · {width * height / 1_000_000:0.#} MP" : "";

    // ----- New canvas -----

    /// <summary>Asks for the size and background of a new blank picture.</summary>
    public static (Size Size, Color Background)? AskNew(Window? owner, Size initial, Color background)
    {
        var window = new CanvasSetupWindow(owner, "New canvas", "Start from a blank picture. Pick a common size or type your own, in pixels.", "Create canvas");
        var width = NumberBox("Width in pixels", initial.Width);
        var height = NumberBox("Height in pixels", initial.Height);
        var chips = new List<(ToggleButton Chip, int Width, int Height)>();

        void Refresh()
        {
            bool readable = Read(width, out var w) & Read(height, out var h);
            w = Math.Round(w); h = Math.Round(h);
            foreach (var (chip, cw, ch) in chips) chip.IsChecked = readable && cw == w && ch == h;
            window.Summarize(w, h, $"{w:0} × {h:0} px{Megapixels(w, h)}", readable);
        }

        window.Label("SIZE");
        var presets = new WrapPanel();
        foreach (var (name, pw, ph) in Presets)
        {
            var chip = Chip(name, $"{pw} × {ph} px");
            chip.Click += (_, _) => { window.Set(width, pw); window.Set(height, ph); Refresh(); };
            chips.Add((chip, pw, ph));
            presets.Children.Add(chip);
        }
        window._body.Children.Add(presets);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var swap = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(12, 0, 0, 0), ToolTip = "Swap width and height" };
        AutomationProperties.SetName(swap, "Swap width and height");
        swap.Click += (_, _) =>
        {
            if (!Read(width, out var w) || !Read(height, out var h)) return;
            window.Set(width, h); window.Set(height, w); Refresh();
        };
        row.Children.Add(Caption("Width"));
        row.Children.Add(width);
        row.Children.Add(Caption("Height", 14));
        row.Children.Add(height);
        row.Children.Add(swap);
        window._body.Children.Add(row);
        width.TextChanged += (_, _) => { if (!window._sync) Refresh(); };
        height.TextChanged += (_, _) => { if (!window._sync) Refresh(); };

        window.Label("BACKGROUND");
        var strip = new ColorStrip { AllowNone = true, Value = background.A == 0 ? null : background, ToolTip = "The crossed swatch keeps the canvas transparent" };
        window._body.Children.Add(strip);
        window.Footer();
        Refresh();
        window.Loaded += (_, _) => width.Focus();

        if (window.ShowDialog() != true || !Read(width, out var fw) || !Read(height, out var fh)) return null;
        return (new Size(Math.Round(fw), Math.Round(fh)), strip.Value ?? Colors.Transparent);
    }

    // ----- Canvas size -----

    /// <summary>Asks how much space to add to (or trim from) each side; the four values are pixels, negative to trim.</summary>
    public static (double Left, double Top, double Right, double Bottom)? AskCanvasSize(Window owner, ImageDocument doc)
    {
        double cw = doc.Size.Width, ch = doc.Size.Height;
        var window = new CanvasSetupWindow(owner, "Canvas size",
            "Add space around the picture or trim it away. Nothing on the canvas is scaled; new space shows the canvas colour, or stays transparent.", "Resize canvas");
        var width = NumberBox("Canvas width", cw);
        var height = NumberBox("Canvas height", ch);
        var sides = new[] { NumberBox("Add to the left", 0, 70), NumberBox("Add to the top", 0, 70), NumberBox("Add to the right", 0, 70), NumberBox("Add to the bottom", 0, 70) };
        int anchorColumn = 1, anchorRow = 1;
        var anchors = new List<ToggleButton>();

        (double L, double T, double R, double B, bool Ok) Margins()
        {
            bool ok = Read(sides[0], out var l) & Read(sides[1], out var t) & Read(sides[2], out var r) & Read(sides[3], out var b);
            return (Math.Round(l), Math.Round(t), Math.Round(r), Math.Round(b), ok);
        }
        void Refresh()
        {
            var (l, t, r, b, ok) = Margins();
            double w = cw + l + r, h = ch + t + b;
            string change = l == 0 && t == 0 && r == 0 && b == 0 ? "No change" : $"{cw:0} × {ch:0} → {w:0} × {h:0} px";
            window.Summarize(w, h, change, ok);
        }
        // Shares a change in one dimension between its two sides according to the anchor: the anchored edge stays put.
        static (double Near, double Far) Share(double delta, int anchor) => anchor switch
        {
            0 => (0, delta),
            2 => (delta, 0),
            _ => (Math.Floor(delta / 2), delta - Math.Floor(delta / 2)),
        };
        void FromSize()
        {
            if (!Read(width, out var w) || !Read(height, out var h)) { window.Summarize(0, 0, "", readable: false); return; }
            var (l, r) = Share(Math.Round(w) - cw, anchorColumn);
            var (t, b) = Share(Math.Round(h) - ch, anchorRow);
            window.Set(sides[0], l); window.Set(sides[1], t); window.Set(sides[2], r); window.Set(sides[3], b);
            Refresh();
        }
        void FromSides()
        {
            var (l, t, r, b, ok) = Margins();
            if (ok) { window.Set(width, cw + l + r); window.Set(height, ch + t + b); }
            Refresh();
        }

        window.Label("NEW SIZE");
        var sizeRow = new Grid();
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition());
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var boxes = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var widthRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        widthRow.Children.Add(new TextBlock { Text = "Width", Width = 52, FontSize = 11.5, Foreground = (Brush)Application.Current.FindResource("Ink2"), VerticalAlignment = VerticalAlignment.Center });
        widthRow.Children.Add(width);
        widthRow.Children.Add(Caption("px", 8));
        var heightRow = new StackPanel { Orientation = Orientation.Horizontal };
        heightRow.Children.Add(new TextBlock { Text = "Height", Width = 52, FontSize = 11.5, Foreground = (Brush)Application.Current.FindResource("Ink2"), VerticalAlignment = VerticalAlignment.Center });
        heightRow.Children.Add(height);
        heightRow.Children.Add(Caption("px", 8));
        boxes.Children.Add(widthRow);
        boxes.Children.Add(heightRow);
        sizeRow.Children.Add(boxes);

        // Anchor: which part of the picture stays where it is when the size boxes change.
        var anchorPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        anchorPanel.Children.Add(new TextBlock { Text = "Keep the picture at", FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Ink2"), Margin = new Thickness(0, 0, 0, 6) });
        var anchorGrid = new UniformGrid { Rows = 3, Columns = 3, Width = 78, Height = 78, HorizontalAlignment = HorizontalAlignment.Left };
        string[] names = ["top left", "top", "top right", "left", "centre", "right", "bottom left", "bottom", "bottom right"];
        for (int i = 0; i < 9; i++)
        {
            int column = i % 3, rowIndex = i / 3;
            var dot = new ToggleButton { Style = (Style)Application.Current.FindResource("AnchorDot"), IsChecked = i == 4, ToolTip = "Keep the picture at the " + names[i] };
            AutomationProperties.SetName(dot, "Anchor " + names[i]);
            dot.Click += (_, _) =>
            {
                anchorColumn = column; anchorRow = rowIndex;
                foreach (var other in anchors) other.IsChecked = other == dot;
                FromSize();
            };
            anchors.Add(dot);
            anchorGrid.Children.Add(dot);
        }
        anchorPanel.Children.Add(anchorGrid);
        Grid.SetColumn(anchorPanel, 1);
        sizeRow.Children.Add(anchorPanel);
        window._body.Children.Add(sizeRow);

        window.Label("ADD TO EACH SIDE");
        var sideRow = new UniformGrid { Columns = 4 };
        string[] sideNames = ["Left", "Top", "Right", "Bottom"];
        for (int i = 0; i < 4; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, i < 3 ? 8 : 0, 0) };
            cell.Children.Add(new TextBlock { Text = sideNames[i], FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Ink2"), Margin = new Thickness(0, 0, 0, 4) });
            sides[i].Width = double.NaN;
            cell.Children.Add(sides[i]);
            sideRow.Children.Add(cell);
            sides[i].TextChanged += (_, _) => { if (!window._sync) FromSides(); };
        }
        window._body.Children.Add(sideRow);
        window._body.Children.Add(new TextBlock { Text = "Pixels to add; a negative number trims that side.", Style = (Style)Application.Current.FindResource("Hint"), Margin = new Thickness(0, 6, 0, 0) });

        var quick = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Quick(string text, string tip, Func<(double, double, double, double)> values)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            button.Click += (_, _) =>
            {
                var (l, t, r, b) = values();
                Keyboard.ClearFocus();
                window.Set(sides[0], l); window.Set(sides[1], t); window.Set(sides[2], r); window.Set(sides[3], b);
                FromSides();
            };
            quick.Children.Add(button);
        }
        double border = Math.Max(10, Math.Round(Math.Min(cw, ch) * 0.05 / 10) * 10);
        Quick($"+{border:0} px border", "Add the same space on every side", () => (border, border, border, border));
        if (doc.CropRect is { } crop)
            Quick("Trim to crop", "Make the crop permanent: the canvas becomes the cropped area", () => (-crop.X, -crop.Y, crop.Right - cw, crop.Bottom - ch));
        Quick("Reset", "Back to the current size", () => (0, 0, 0, 0));
        window._body.Children.Add(quick);

        width.TextChanged += (_, _) => { if (!window._sync) FromSize(); };
        height.TextChanged += (_, _) => { if (!window._sync) FromSize(); };
        window.Footer();
        Refresh();
        window.Loaded += (_, _) => width.Focus();

        if (window.ShowDialog() != true) return null;
        var result = Margins();
        if (!result.Ok || (result.L == 0 && result.T == 0 && result.R == 0 && result.B == 0)) return null;
        return (result.L, result.T, result.R, result.B);
    }

    // ----- Image size -----

    /// <summary>Asks for a new size for the whole picture.</summary>
    public static Size? AskImageSize(Window owner, ImageDocument doc)
    {
        double cw = doc.Size.Width, ch = doc.Size.Height;
        var window = new CanvasSetupWindow(owner, "Image size",
            "Scale the whole picture. Every layer, shape, line width and font scales with it; photos are resampled from their original pixels.", "Resize image");
        var width = NumberBox("Image width", cw);
        var height = NumberBox("Image height", ch);
        var percent = NumberBox("Scale in percent", 100, 70);
        var keep = new CheckBox { IsChecked = true, Style = (Style)Application.Current.FindResource("TickCheckBox"), Content = new TextBlock { Text = "Keep proportions", FontSize = 12 }, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(keep, "Keep proportions");

        void Refresh()
        {
            bool readable = Read(width, out var w) & Read(height, out var h);
            w = Math.Round(w); h = Math.Round(h);
            string text = w == cw && h == ch ? "No change" : $"{cw:0} × {ch:0} → {w:0} × {h:0} px{Megapixels(w, h)}";
            window.Summarize(w, h, text, readable);
        }
        void FromWidth()
        {
            if (Read(width, out var w))
            {
                if (keep.IsChecked == true) window.Set(height, Math.Max(1, Math.Round(w * ch / cw)));
                window.Set(percent, w / cw * 100);
            }
            Refresh();
        }
        void FromHeight()
        {
            if (Read(height, out var h))
            {
                if (keep.IsChecked == true) { window.Set(width, Math.Max(1, Math.Round(h * cw / ch))); window.Set(percent, h / ch * 100); }
            }
            Refresh();
        }
        void FromPercent()
        {
            if (Read(percent, out var p) && p > 0)
            {
                window.Set(width, Math.Max(1, Math.Round(cw * p / 100)));
                window.Set(height, Math.Max(1, Math.Round(ch * p / 100)));
            }
            Refresh();
        }

        window.Label("SCALE");
        var chips = new WrapPanel();
        foreach (int value in new[] { 25, 50, 75, 150, 200, 400 })
        {
            var chip = new Button { Content = value + "%", Padding = new Thickness(10, 5, 10, 5), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) => { Keyboard.ClearFocus(); window.Set(percent, value); FromPercent(); };
            chips.Children.Add(chip);
        }
        window._body.Children.Add(chips);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(Caption("Width"));
        row.Children.Add(width);
        row.Children.Add(Caption("Height", 14));
        row.Children.Add(height);
        row.Children.Add(Caption("", 6));
        row.Children.Add(percent);
        row.Children.Add(Caption("%", 6));
        window._body.Children.Add(row);
        window._body.Children.Add(keep);
        keep.Click += (_, _) => { if (keep.IsChecked == true) FromWidth(); };
        width.TextChanged += (_, _) => { if (!window._sync) FromWidth(); };
        height.TextChanged += (_, _) => { if (!window._sync) FromHeight(); };
        percent.TextChanged += (_, _) => { if (!window._sync) FromPercent(); };
        window.Footer();
        Refresh();
        window.Loaded += (_, _) => width.Focus();

        if (window.ShowDialog() != true || !Read(width, out var fw) || !Read(height, out var fh)) return null;
        var size = new Size(Math.Round(fw), Math.Round(fh));
        return size == doc.Size ? null : size;
    }
}
