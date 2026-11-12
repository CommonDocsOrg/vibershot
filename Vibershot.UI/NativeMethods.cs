using System.Runtime.InteropServices;

namespace Vibershot.UI;

internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;

    // Hotkey modifier flags (user32 RegisterHotKey).
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CTRL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // Virtual key code for the Print Screen key.
    public const uint VK_SNAPSHOT = 0x2C;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // DWM Mica backdrop (Windows 11 22H2+). dwmapi.dll ships with the OS.
    public enum DwmSystemBackdropType : int
    {
        Default = -1,
        Behind = 0,
        Mica = 6,
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attrName, ref int attrValue, int attrSize);
}
