using Microsoft.Win32;

namespace Vibershot.UI;

/// <summary>
/// Registers Vibershot in the per-user Run key so it launches at sign-in (into the tray).
/// The registry is the source of truth so the checkbox always reflects what Windows will do.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Vibershot";
    public const string TrayArgument = "--tray";

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Keeps the stored path current if the app was moved or rebuilt elsewhere.</summary>
    public static void RefreshIfEnabled()
    {
        try
        {
            if (IsEnabled && !string.IsNullOrEmpty(Environment.ProcessPath))
            {
                SetEnabled(true);
            }
        }
        catch
        {
            // Not critical; the settings dialog will surface errors when the user changes it.
        }
    }
}
