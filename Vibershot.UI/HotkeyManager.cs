using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Vibershot.Core;

namespace Vibershot.UI;

/// <summary>
/// Registers the configured global shortcuts (one <see cref="HotkeyService"/> per action) and
/// re-registers them when settings change. Print Screen takeover is handled separately by the
/// app because it has its own on/off setting and conflict message.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int FirstId = 100;
    private readonly Window _owner;
    private readonly Dictionary<HotkeyAction, HotkeyService> _registered = new();

    public HotkeyManager(Window owner) => _owner = owner;

    public event Action<HotkeyAction>? Triggered;

    /// <summary>Actions whose shortcut could not be registered in the last <see cref="Apply"/>, with the gesture text.</summary>
    public IReadOnlyList<(HotkeyAction Action, string Gesture)> Failures { get; private set; } = Array.Empty<(HotkeyAction, string)>();

    /// <summary>The shortcut text in force for an action, or "" when none is registered.</summary>
    public string Active(HotkeyAction action) => _registered.TryGetValue(action, out var s) ? s.Description : "";

    /// <summary>Releases every shortcut and registers the ones in <paramref name="settings"/>. Never throws.</summary>
    public void Apply(HotkeySettings settings)
    {
        foreach (var service in _registered.Values) service.Dispose();
        _registered.Clear();
        var failures = new List<(HotkeyAction, string)>();
        foreach (var action in HotkeySettings.Actions)
        {
            var gesture = settings.Gesture(action);
            if (!gesture.IsValid) continue;
            if (!TryResolve(gesture, out var modifiers, out var vk)) { failures.Add((action, gesture.ToString())); continue; }
            try
            {
                var service = new HotkeyService(_owner, FirstId + (int)action, modifiers | NativeMethods.MOD_NOREPEAT, vk, gesture.ToString());
                var captured = action;
                service.Triggered += (_, _) => Triggered?.Invoke(captured);
                _registered[action] = service;
            }
            catch (Exception) { failures.Add((action, gesture.ToString())); }
        }
        Failures = failures;
    }

    /// <summary>Win32 modifier flags and virtual key for a gesture; false when the key name is unknown.</summary>
    public static bool TryResolve(HotkeyGesture gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0; virtualKey = 0;
        if (gesture.IsEmpty) return false;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= NativeMethods.MOD_CTRL;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= NativeMethods.MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= NativeMethods.MOD_ALT;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Windows)) modifiers |= NativeMethods.MOD_WIN;
        var key = ToKey(gesture.Key);
        if (key is null) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return virtualKey != 0;
    }

    /// <summary>WPF key for a gesture key name ("S", "D5", "PrintScreen", "F11").</summary>
    public static Key? ToKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name == "PrintScreen") return Key.Snapshot;
        if (name == "Enter") return Key.Return;
        return Enum.TryParse<Key>(name, ignoreCase: true, out var key) && key != Key.None ? key : null;
    }

    /// <summary>Gesture key name for a WPF key; null for modifiers and keys that cannot be a shortcut.</summary>
    public static string? FromKey(Key key)
    {
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.None or Key.ImeProcessed or Key.DeadCharProcessed:
                return null;
            case Key.Snapshot: return "PrintScreen";
            case Key.Return: return "Enter";
            default: return key.ToString();
        }
    }

    /// <summary>The modifier flags currently held, for a key recorder.</summary>
    public static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) m |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) m |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) m |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) m |= HotkeyModifiers.Windows;
        return m;
    }

    /// <summary>Tries a registration and releases it at once: true when the combination is free system-wide.</summary>
    public static bool IsAvailable(Window owner, HotkeyGesture gesture)
    {
        if (!TryResolve(gesture, out var modifiers, out var vk)) return false;
        try { using var probe = new HotkeyService(owner, 9999, modifiers | NativeMethods.MOD_NOREPEAT, vk, gesture.ToString()); return true; }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        foreach (var service in _registered.Values) service.Dispose();
        _registered.Clear();
    }
}
