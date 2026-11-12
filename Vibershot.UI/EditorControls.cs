using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Vibershot.Editing;

namespace Vibershot.UI;

/// <summary>
/// A row of preset colour swatches with an optional "none" swatch and a hex box for anything
/// else. Used by the editor's properties panel for stroke, fill, text and background colours.
/// </summary>
public sealed class ColorStrip : StackPanel
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xF0, 0x61, 0x2A), Color.FromRgb(0x15, 0x18, 0x1C), Colors.White, Color.FromRgb(0xD6, 0x45, 0x45),
        Color.FromRgb(0x1F, 0x9D, 0x6A), Color.FromRgb(0x2F, 0x6F, 0xDB), Color.FromRgb(0xF2, 0xC9, 0x4C), Color.FromRgb(0x8E, 0x5B, 0xD8),
    ];
    private readonly WrapPanel _swatches = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _hex = new() { FontSize = 11.5, Padding = new Thickness(8, 5, 8, 5), Width = 96, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly List<(Border Swatch, Color? Color)> _entries = new();
    private Color? _value;
    private bool _syncing;

    public ColorStrip()
    {
        _hex.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } };
        _hex.LostFocus += (_, _) => CommitHex();
        Children.Add(_swatches);
        Children.Add(_hex);
        Loaded += (_, _) => { if (_entries.Count == 0) Build(); };
    }

    public bool AllowNone { get; set; }

    public Color? Value
    {
        get => _value;
        set { _value = value; Sync(); }
    }

    public event Action<Color?>? ValueChanged;

    private void Build()
    {
        if (AllowNone) AddSwatch(null, "No colour");
        foreach (var color in Palette) AddSwatch(color, DocumentJson.ColorToString(color));
        Sync();
    }

    private void AddSwatch(Color? color, string tip)
    {
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.FindResource("PaperEdge"),
            Background = color is { } c ? new SolidColorBrush(c) : Brushes.White, ToolTip = tip,
        };
        if (color == null)
            swatch.Child = new Line { X1 = 4, Y1 = 17, X2 = 17, Y2 = 4, Stroke = (Brush)Application.Current.FindResource("Danger"), StrokeThickness = 1.5 };
        swatch.MouseLeftButtonDown += (_, e) => { Set(color); e.Handled = true; };
        _swatches.Children.Add(swatch);
        _entries.Add((swatch, color));
    }

    private void Set(Color? color)
    {
        _value = color;
        Sync();
        ValueChanged?.Invoke(color);
    }

    private void CommitHex()
    {
        if (_syncing) return;
        var text = _hex.Text.Trim();
        if (text.Length == 0) { if (AllowNone && _value != null) Set(null); return; }
        try
        {
            var parsed = DocumentJson.ParseColor(text);
            if (parsed != _value) Set(parsed);
        }
        catch (FormatException) { Sync(); }
    }

    private void Sync()
    {
        _syncing = true;
        foreach (var (swatch, color) in _entries)
        {
            bool selected = color == _value;
            swatch.BorderThickness = new Thickness(selected ? 2 : 1);
            swatch.BorderBrush = (Brush)Application.Current.FindResource(selected ? "Ink" : "PaperEdge");
        }
        _hex.Text = _value is { } c ? (c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : DocumentJson.ColorToString(c)) : "";
        _syncing = false;
    }
}

/// <summary>Save / Discard / Cancel prompt shown when an editor window with unsaved edits closes.</summary>
internal static class SaveChangesDialog
{
    public enum Choice { Cancel, Discard, Save }

    /// <param name="message">Replaces the default "Save changes to …?" headline when given (the hint line stays).</param>
    public static Choice Show(Window owner, string documentName, string? message = null)
    {
        var result = Choice.Cancel;
        var window = new Window
        {
            Owner = owner, Title = "Vibershot Editor", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Style = (Style)Application.Current.FindResource(typeof(Window)),
        };
        WindowAppearance.Apply(window);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = message ?? $"Save changes to {documentName}?", FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Your edits are lost if you don't save them as a project.", Style = (Style)Application.Current.FindResource("Hint"), Margin = new Thickness(0, 6, 0, 18), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 9, 16, 9) };
        var discard = new Button { Content = "Discard", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 9, 16, 9) };
        var save = new Button { Content = "Save project", IsDefault = true, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(18, 9, 18, 9), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        cancel.Click += (_, _) => window.Close();
        discard.Click += (_, _) => { result = Choice.Discard; window.Close(); };
        save.Click += (_, _) => { result = Choice.Save; window.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(discard);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }
}
