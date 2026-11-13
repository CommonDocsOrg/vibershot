using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;

namespace Vibershot.UI;

/// <summary>Small pieces shared by the Settings and first-run windows.</summary>
internal static class SettingsHelpers
{
    /// <summary>Where <see cref="Vibershot.Core.AppConfig.Save"/> writes: %APPDATA%\Vibershot\config.json.</summary>
    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibershot", "config.json");

    /// <summary>True when Print Screen takeover is on but Windows still owns the key.</summary>
    public static bool PrintScreenBlocked => (Application.Current as App)?.PrintScreenBlocked ?? false;

    /// <summary>Opens the Windows folder picker; null when the user cancels.</summary>
    public static string? PickFolder(string description, string? start)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) dialog.SelectedPath = start;
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    /// <summary>True when <paramref name="path"/> sits inside this PC's OneDrive folder.</summary>
    public static bool IsInOneDrive(string path)
    {
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrWhiteSpace(oneDrive) || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.GetFullPath(oneDrive).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public const string OneDriveSentence = "This folder is inside OneDrive on this PC, so it syncs.";

    /// <summary>True for a usable absolute folder path.</summary>
    public static bool IsAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    /// <summary>The yellow notice shown while Windows holds Print Screen for Snipping Tool, with a link to the setting.</summary>
    public static Border BuildPrintScreenWarning()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("WarningInk") };
        text.Inlines.Add("Windows is still holding Print Screen for Snipping Tool. Turn off 'Use the Print screen key to open screen capture' in ");
        var link = new Hyperlink(new Run("Windows Settings › Accessibility › Keyboard"));
        link.Click += (_, _) => OpenWindowsSetting("ms-settings:easeofaccess-keyboard");
        text.Inlines.Add(link);
        text.Inlines.Add(", then come back here.");
        var box = new Border { Child = text };
        box.SetResourceReference(FrameworkElement.StyleProperty, "WarningBox");
        AutomationProperties.SetName(box, "Print Screen is held by Windows");
        return box;
    }

    public static void OpenWindowsSetting(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>Opens a folder in Explorer, ignoring failures.</summary>
    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    /// <summary>A hint-styled text block; bold rows and hints are built in code for repeated list rows.</summary>
    public static TextBlock Hint(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(FrameworkElement.StyleProperty, "Hint");
        return block;
    }

    /// <summary>Mono text used for paths.</summary>
    public static TextBlock MonoText(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = text };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        return block;
    }

    /// <summary>Link-styled button for an inline action.</summary>
    public static Button LinkButton(string label, string automationName, RoutedEventHandler onClick)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        AutomationProperties.SetName(button, automationName);
        button.Click += onClick;
        return button;
    }

    public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
