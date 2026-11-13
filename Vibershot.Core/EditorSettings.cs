namespace Vibershot.Core;

/// <summary>
/// Defaults for new items in the image editor and what happens after a capture.
/// Stored under <c>Editor</c> in config.json; older configs without it load the defaults.
/// </summary>
public sealed class EditorSettings
{
    public string DefaultFontFamily { get; set; } = "Segoe UI";
    public double DefaultFontSize { get; set; } = 18;

    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string DefaultStrokeColor { get; set; } = "#F0612A";

    public double DefaultStrokeWidth { get; set; } = 3;

    /// <summary>
    /// Canvas colour for new documents as #RRGGBB or #AARRGGBB; <c>#00000000</c> (or the word
    /// <c>transparent</c>) leaves the canvas clear so exports keep their alpha.
    /// </summary>
    public string CanvasBackground { get; set; } = White;

    /// <summary>Editor shortcut that shows the picture at one screen pixel per image pixel.</summary>
    public string ActualSizeShortcut { get; set; } = "Ctrl+0";

    /// <summary>Editor shortcut that fits the picture in the window; empty (the default) leaves it without one.</summary>
    public string FitShortcut { get; set; } = "";

    /// <summary>Id of the style theme the editor's library and drawing tools use; "default" follows the stroke colour and width above.</summary>
    public string Theme { get; set; } = "default";

    /// <summary>True when the editor's right sidebar (library and layers) is folded down to its strip.</summary>
    public bool SidebarCollapsed { get; set; }

    public const string White = "#FFFFFF";
    public const string Black = "#000000";
    public const string Transparent = "#00000000";

    /// <summary>Returns the canvas colour as AARRGGBB, falling back to white for unreadable values.</summary>
    public uint CanvasBackgroundArgb() => ParseArgb(CanvasBackground) ?? 0xFFFFFFFF;

    public static uint? ParseArgb(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return 0x00000000;
        var hex = text.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
        return value;
    }

    /// <summary>Pre-2026-11 switch; now <see cref="AfterCaptureSettings.OpenEditor"/>. Read for migration, never written.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("OpenEditorAfterCapture"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyOpenEditorAfterCapture { get; set; }
}
