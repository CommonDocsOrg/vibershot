using System.Windows.Input;
using System.Windows.Media;
using Vibershot.Core;

namespace Vibershot.UI;

/// <summary>
/// Captures the next key combination pressed while recording. Shown in place of the keycaps on a
/// hotkey row; while recording it listens to the owning window's preview key events so the
/// combination is taken before any button or text box can react to it.
/// </summary>
public sealed class HotkeyRecorder : ContentControl
{
    private Window? _window;
    private readonly TextBlock _prompt = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    public HotkeyRecorder()
    {
        Focusable = true;
        IsTabStop = true;
        VerticalContentAlignment = VerticalAlignment.Center;
        Loaded += (_, _) => UpdatePrompt();
    }

    /// <summary>Raised with the valid gesture the user pressed; recording has stopped by then.</summary>
    public event Action<HotkeyGesture>? Captured;

    /// <summary>Raised when the user pressed Esc or the recorder lost focus without a gesture.</summary>
    public event Action? Cancelled;

    /// <summary>Raised when a press could not be used (a letter without a modifier), with the hint to show.</summary>
    public event Action<string>? Rejected;

    public bool IsRecording { get; private set; }

    /// <summary>Starts listening; the control takes keyboard focus so Esc and the keys reach it first.</summary>
    public void Start()
    {
        if (IsRecording) return;
        _window = Window.GetWindow(this);
        if (_window is null) return;
        IsRecording = true;
        _window.PreviewKeyDown += OnPreviewKeyDown;
        _window.PreviewKeyUp += OnPreviewKeyUp;
        LostKeyboardFocus += OnLostFocus;
        UpdatePrompt();
        Focus();
    }

    /// <summary>Stops listening without raising any event.</summary>
    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnPreviewKeyDown;
            _window.PreviewKeyUp -= OnPreviewKeyUp;
        }
        LostKeyboardFocus -= OnLostFocus;
    }

    private void UpdatePrompt()
    {
        _prompt.Text = "Press the new keys…";
        _prompt.Foreground = TryFindResource("Tangerine") as Brush ?? Brushes.OrangeRed;
        Content = _prompt;
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsRecording) return;
        Stop();
        Cancelled?.Invoke();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsRecording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && HotkeyManager.CurrentModifiers() == HotkeyModifiers.None) { Stop(); Cancelled?.Invoke(); return; }
        Consider(key);
    }

    /// <summary>Print Screen only ever arrives as a key-up in Windows, so it is accepted on release.</summary>
    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsRecording) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Snapshot) return;
        e.Handled = true;
        Consider(key);
    }

    private void Consider(Key key)
    {
        var name = HotkeyManager.FromKey(key);
        if (name is null) return; // A modifier on its own: wait for the key.
        var gesture = new HotkeyGesture(HotkeyManager.CurrentModifiers(), name);
        if (!gesture.IsValid) { Rejected?.Invoke("Add Ctrl, Shift, Alt or Win"); return; }
        Stop();
        Captured?.Invoke(gesture);
    }
}

/// <summary>Draws a shortcut as a row of keycaps, or "Not set" when the gesture is empty.</summary>
public static class Keycaps
{
    public static FrameworkElement Build(HotkeyGesture gesture, double fontSize = 11.5)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (gesture.IsEmpty)
        {
            panel.Children.Add(new TextBlock { Text = "Not set", Foreground = Brush("Ink3"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }
        var parts = gesture.ToString().Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) panel.Children.Add(new TextBlock { Text = "+", Foreground = Brush("Ink3"), FontSize = 11, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = parts[i], FontSize = fontSize };
            text.SetResourceReference(FrameworkElement.StyleProperty, "KeycapText");
            var cap = new Border { Child = text };
            cap.SetResourceReference(FrameworkElement.StyleProperty, "Keycap");
            panel.Children.Add(cap);
        }
        return panel;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
