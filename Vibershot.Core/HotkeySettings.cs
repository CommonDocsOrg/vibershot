namespace Vibershot.Core;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// A keyboard shortcut as text ("Ctrl+Shift+S"): modifiers plus one key name. Key names are the
/// WPF <c>Key</c> enum names with a few friendlier spellings accepted; the UI maps them to
/// virtual keys. An empty gesture means "not set".
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static readonly HotkeyGesture Empty = new(HotkeyModifiers.None, "");

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    /// <summary>Keys that work without a modifier; everything else needs at least one so typing is not hijacked.</summary>
    public static readonly string[] StandaloneKeys = { "PrintScreen", "Pause", "Scroll", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24" };

    /// <summary>True when the combination can be registered: a key, and a modifier unless the key stands alone.</summary>
    public bool IsValid => !IsEmpty && (Modifiers != HotkeyModifiers.None || StandaloneKeys.Contains(Key, StringComparer.OrdinalIgnoreCase));

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = Empty;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return true;
        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var modifier = ParseModifier(parts[i]);
            if (modifier is null) return false;
            modifiers |= modifier.Value;
        }
        var key = NormalizeKey(parts[^1]);
        if (key is null)
        {
            // "Ctrl+Shift" with no key is the empty gesture rather than an error.
            if (ParseModifier(parts[^1]) is not null) return true;
            return false;
        }
        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    public static HotkeyGesture Parse(string? text) =>
        TryParse(text, out var gesture) ? gesture : throw new FormatException($"'{text}' is not a shortcut like Ctrl+Shift+S.");

    private static HotkeyModifiers? ParseModifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Control,
        "shift" => HotkeyModifiers.Shift,
        "alt" => HotkeyModifiers.Alt,
        "win" or "windows" or "meta" => HotkeyModifiers.Windows,
        _ => null,
    };

    /// <summary>Canonical spelling of a key name; null when the name is a modifier or empty.</summary>
    public static string? NormalizeKey(string part)
    {
        part = part.Trim();
        if (part.Length == 0 || ParseModifier(part) is not null) return null;
        if (part.Length == 1) return char.IsLetter(part[0]) ? part.ToUpperInvariant() : char.IsDigit(part[0]) ? "D" + part : part;
        return part.ToLowerInvariant() switch
        {
            "printscreen" or "prtscn" or "prtsc" or "snapshot" => "PrintScreen",
            "esc" or "escape" => "Escape",
            "del" or "delete" => "Delete",
            "ins" or "insert" => "Insert",
            "pgup" or "pageup" or "prior" => "PageUp",
            "pgdn" or "pagedown" or "next" => "PageDown",
            "space" => "Space",
            "tab" => "Tab",
            "enter" or "return" => "Enter",
            "home" => "Home",
            "end" => "End",
            "up" => "Up",
            "down" => "Down",
            "left" => "Left",
            "right" => "Right",
            "pause" => "Pause",
            "scroll" or "scrolllock" => "Scroll",
            "backspace" or "back" => "Back",
            _ => char.ToUpperInvariant(part[0]) + part[1..],
        };
    }

    /// <summary>Friendly text for one key: "PrtScn" for PrintScreen, "5" for D5.</summary>
    public static string DisplayKey(string key) => key switch
    {
        "PrintScreen" => "PrtScn",
        "Escape" => "Esc",
        "Delete" => "Del",
        "Insert" => "Ins",
        "PageUp" => "PgUp",
        "PageDown" => "PgDn",
        "Back" => "Backspace",
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1..],
        _ => key,
    };

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(DisplayKey(Key));
        return string.Join("+", parts);
    }
}

/// <summary>Which capture or window a global shortcut starts.</summary>
public enum HotkeyAction
{
    CaptureRegion,
    FullDesktop,
    RecordVideo,
    OpenGallery,
    OpenEditor,
}

/// <summary>
/// Global shortcuts, stored as text under <c>Hotkeys</c> in config.json. An empty string leaves
/// that action without a shortcut. Print Screen takeover is a separate setting.
/// </summary>
public sealed class HotkeySettings
{
    public string CaptureRegion { get; set; } = "Ctrl+Shift+S";
    public string FullDesktop { get; set; } = "";
    public string RecordVideo { get; set; } = "";
    public string OpenGallery { get; set; } = "";
    public string OpenEditor { get; set; } = "";

    public string this[HotkeyAction action]
    {
        get => action switch
        {
            HotkeyAction.CaptureRegion => CaptureRegion,
            HotkeyAction.FullDesktop => FullDesktop,
            HotkeyAction.RecordVideo => RecordVideo,
            HotkeyAction.OpenGallery => OpenGallery,
            HotkeyAction.OpenEditor => OpenEditor,
            _ => "",
        };
        set
        {
            switch (action)
            {
                case HotkeyAction.CaptureRegion: CaptureRegion = value; break;
                case HotkeyAction.FullDesktop: FullDesktop = value; break;
                case HotkeyAction.RecordVideo: RecordVideo = value; break;
                case HotkeyAction.OpenGallery: OpenGallery = value; break;
                case HotkeyAction.OpenEditor: OpenEditor = value; break;
            }
        }
    }

    public static readonly HotkeyAction[] Actions = (HotkeyAction[])Enum.GetValues(typeof(HotkeyAction));

    public static string Label(HotkeyAction action) => action switch
    {
        HotkeyAction.CaptureRegion => "Capture region or window",
        HotkeyAction.FullDesktop => "Full desktop",
        HotkeyAction.RecordVideo => "Record video",
        HotkeyAction.OpenGallery => "Open gallery",
        HotkeyAction.OpenEditor => "Open editor",
        _ => action.ToString(),
    };

    /// <summary>The parsed gesture for an action; unreadable text counts as not set.</summary>
    public HotkeyGesture Gesture(HotkeyAction action) => HotkeyGesture.TryParse(this[action], out var g) ? g : HotkeyGesture.Empty;

    /// <summary>The other action already using <paramref name="gesture"/>, if any.</summary>
    public HotkeyAction? ConflictFor(HotkeyAction action, HotkeyGesture gesture)
    {
        if (gesture.IsEmpty) return null;
        foreach (var other in Actions)
        {
            if (other == action) continue;
            var existing = Gesture(other);
            if (!existing.IsEmpty && existing.Modifiers == gesture.Modifiers && string.Equals(existing.Key, gesture.Key, StringComparison.OrdinalIgnoreCase)) return other;
        }
        return null;
    }
}
