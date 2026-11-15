using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Vibershot.UI;

/// <summary>
/// Segoe Fluent Icons glyphs for UI built in code, and the glyph-plus-label content for buttons.
/// XAML uses the ButtonGlyph and ButtonText styles in App.xaml for the same look.
/// </summary>
internal static class Icons
{
    /// <summary>Windows 11 ships Segoe Fluent Icons; Windows 10 has the same code points in Segoe MDL2 Assets.</summary>
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public const string Record = "", Pause = "", Play = "", Stop = "", Check = "", Cancel = "", Add = "",
        Folder = "", Edit = "", Save = "", Screenshot = "", Video = "", Scroll = "",
        Speaker = "", Microphone = "", Webcam = "";

    public static TextBlock Glyph(string glyph, double size = 13, Brush? brush = null)
    {
        // Normal weight: a bold button would otherwise have WPF fatten the glyph with simulated bold.
        var block = new TextBlock { Text = glyph, FontFamily = Font, FontSize = size, FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (brush != null) block.Foreground = brush;
        return block;
    }

    /// <summary>
    /// Gives a button a glyph with an optional label after it. The label becomes the button's
    /// accessible name, since the content is no longer plain text.
    /// </summary>
    public static void Label(ButtonBase button, string glyph, string name, bool showText = true)
    {
        var icon = Glyph(glyph);
        Follow(icon, button);
        AutomationProperties.SetName(button, name);
        if (!showText) { button.Content = icon; return; }
        icon.Margin = new Thickness(0, 0, 8, 0);
        var text = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        Follow(text, button);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(icon); panel.Children.Add(text);
        button.Content = panel;
    }

    // The app-wide TextBlock style sets Ink, which would otherwise win over the button's own foreground.
    private static void Follow(TextBlock block, ButtonBase button) =>
        block.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
}
