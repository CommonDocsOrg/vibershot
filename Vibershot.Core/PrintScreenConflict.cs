using System.Diagnostics;
using Microsoft.Win32;

namespace Vibershot.Core;

/// <summary>Something that is probably holding Print Screen, and how to make it let go.</summary>
/// <param name="Name">The app as the user knows it ("OneDrive").</param>
/// <param name="Fix">One sentence telling the user what to change.</param>
/// <param name="SettingsUri">A link that opens the setting, when there is one.</param>
public sealed record PrintScreenSuspect(string Name, string Fix, string? SettingsUri = null);

/// <summary>
/// Works out who holds Print Screen when Vibershot cannot register it. Windows has no call
/// that names the owner of a hotkey, so this checks the usual culprits: the Snipping Tool
/// setting and running apps that are known to claim the key.
/// </summary>
public static class PrintScreenConflict
{
    public const string WindowsSettingsUri = "ms-settings:easeofaccess-keyboard";
    private const string ChangeHotkey = "Change or clear its Print Screen hotkey in its settings, or quit it.";

    /// <summary>Process name (no ".exe") to the app and its fix. Apps that claim Print Screen by default.</summary>
    private static readonly (string Process, PrintScreenSuspect Suspect)[] KnownApps =
    {
        ("OneDrive", new("OneDrive", "In OneDrive Settings › Sync and backup, turn off 'Save screenshots I capture to OneDrive'.")),
        ("ShareX", new("ShareX", "In ShareX › Hotkey settings, change or remove the Print Screen hotkeys.")),
        ("Greenshot", new("Greenshot", "In Greenshot › Preferences › General, clear the Print Screen hotkeys.")),
        ("Lightshot", new("Lightshot", "In Lightshot › Options › Hotkeys, change the general hotkey.")),
        ("Snagit32", new("Snagit", "In Snagit › Capture preferences › Hotkeys, change the global capture hotkey.")),
        ("SnagitCapture", new("Snagit", "In Snagit › Capture preferences › Hotkeys, change the global capture hotkey.")),
        ("Dropbox", new("Dropbox", "In Dropbox Preferences, turn off 'Share screenshots using Dropbox'.")),
        ("GyStation", new("Gyazo", ChangeHotkey)),
        ("picpick", new("PicPick", ChangeHotkey)),
        ("flameshot", new("Flameshot", ChangeHotkey)),
        ("Screenpresso", new("Screenpresso", ChangeHotkey)),
        ("Monosnap", new("Monosnap", ChangeHotkey)),
        ("Snipaste", new("Snipaste", ChangeHotkey)),
        ("FSCapture", new("FastStone Capture", ChangeHotkey)),
        ("ScreenSketch", new("Snipping Tool", ChangeHotkey)),
    };

    public static readonly PrintScreenSuspect WindowsSnipping = new("Windows (Snipping Tool)",
        "Turn off 'Use the Print screen key to open screen capture' in Windows Settings › Accessibility › Keyboard.", WindowsSettingsUri);

    /// <summary>
    /// The likely holders, most likely first, from the Windows setting and the names of running
    /// processes. Empty when nothing known is running; each app is listed once.
    /// </summary>
    /// <param name="altPrintScreenTaken">
    /// Whether Alt+Print Screen is also held. OneDrive claims both keys when it saves screenshots,
    /// so a free Alt+Print Screen clears it even though OneDrive is nearly always running.
    /// </param>
    public static IReadOnlyList<PrintScreenSuspect> Identify(bool windowsSnippingOn, IEnumerable<string> processNames, bool altPrintScreenTaken = true)
    {
        var running = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var found = new List<PrintScreenSuspect>();
        if (windowsSnippingOn) found.Add(WindowsSnipping);
        foreach (var (process, suspect) in KnownApps)
        {
            if (!running.Contains(process) || found.Any(f => f.Name == suspect.Name)) continue;
            if (process == "OneDrive" && !altPrintScreenTaken) continue;
            found.Add(suspect);
        }
        return found;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>True when Alt+Print Screen cannot be registered; it is claimed and released at once to find out.</summary>
    private static bool AltPrintScreenTaken()
    {
        const int probeId = 0x5650; const uint modAlt = 0x0001, modNoRepeat = 0x4000, vkSnapshot = 0x2C;
        if (!RegisterHotKey(IntPtr.Zero, probeId, modAlt | modNoRepeat, vkSnapshot)) return true;
        UnregisterHotKey(IntPtr.Zero, probeId);
        return false;
    }

    /// <summary>Looks at this PC: the Snipping Tool setting and what is running now.</summary>
    public static IReadOnlyList<PrintScreenSuspect> Detect()
    {
        bool snipping = false;
        try { snipping = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", 0) is int on && on != 0; }
        catch (Exception) { }
        var names = new List<string>();
        try
        {
            foreach (var process in Process.GetProcesses())
                using (process) names.Add(process.ProcessName);
        }
        catch (Exception) { }
        bool altTaken = true;
        try { altTaken = AltPrintScreenTaken(); } catch (Exception) { }
        return Identify(snipping, names, altTaken);
    }

    /// <summary>"OneDrive", "OneDrive or ShareX", or "another app" when nothing known was found.</summary>
    public static string Names(IReadOnlyList<PrintScreenSuspect> suspects) =>
        suspects.Count == 0 ? "another app" : string.Join(" or ", suspects.Select(s => s.Name));

    /// <summary>One line for a status bar or tray notice: who holds the key and the first thing to try.</summary>
    public static string Describe(IReadOnlyList<PrintScreenSuspect> suspects) =>
        suspects.Count == 0
            ? "Print Screen is held by another app. Quit other screenshot tools, or pick a different shortcut in Settings › Hotkeys."
            : $"Print Screen is held by {Names(suspects)}. {suspects[0].Fix}";
}
