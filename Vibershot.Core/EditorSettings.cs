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

    /// <summary>Open the editor instead of the preview after a screenshot.</summary>
    public bool OpenEditorAfterCapture { get; set; } = false;
}
