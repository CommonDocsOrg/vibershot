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

    /// <summary>Fills <paramref name="host"/> with the Print Screen notice while the key is held elsewhere, and clears it otherwise.</summary>
    public static void RefreshPrintScreenWarning(ContentControl host) =>
        host.Content = PrintScreenBlocked ? BuildPrintScreenWarning(host) : null;

    /// <summary>
    /// The yellow notice shown while something else holds Print Screen. It names the likely
    /// holder (OneDrive, Snipping Tool, another screenshot tool), says what to change, and has
    /// a "Check again" link that retries the takeover and updates <paramref name="host"/>.
    /// </summary>
    public static Border BuildPrintScreenWarning(ContentControl host)
    {
        var suspects = Vibershot.Core.PrintScreenConflict.Detect();
        string names = Vibershot.Core.PrintScreenConflict.Names(suspects);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("WarningInk") };
        text.Inlines.Add(new Run($"Print Screen is held by {names}, so Vibershot can't use it yet. ") { FontWeight = FontWeights.SemiBold });
        if (suspects.Count == 0)
            text.Inlines.Add("Windows doesn't say which app has it. Quit other screenshot tools, or pick a different shortcut under Hotkeys. ");
        foreach (var suspect in suspects)
        {
            text.Inlines.Add((suspects.Count > 1 ? suspect.Name + ": " : "") + suspect.Fix + " ");
            if (suspect.SettingsUri is not { } uri) continue;
            var open = new Hyperlink(new Run("Open Windows Settings"));
            open.Click += (_, _) => OpenWindowsSetting(uri);
            text.Inlines.Add(open);
            text.Inlines.Add(" ");
        }
        var retry = new Hyperlink(new Run("Check again"));
        retry.Click += (_, _) =>
        {
            (Application.Current as App)?.ApplyPrintScreenSetting();
            RefreshPrintScreenWarning(host);
        };
        text.Inlines.Add(retry);
        var box = new Border { Child = text };
        box.SetResourceReference(FrameworkElement.StyleProperty, "WarningBox");
        AutomationProperties.SetName(box, $"Print Screen is held by {names}");
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
