using System.Collections.Generic;
using System.Windows.Automation;

namespace Vibershot.UI;

// Snapshot native geometry before the overlay exists, so hit testing never selects the overlay.
internal sealed class WindowTargets
{
    internal sealed record Target(IntPtr Handle, Rect Bounds, Rect Client, List<Rect> Areas);
    private readonly List<Target> _windows = new();
    internal WindowTargets()
    {
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 9, out NativeRect frame, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(hwnd, out frame)) return true;
            var bounds = frame.Rect;
            if (bounds.Width < 2 || bounds.Height < 2) return true;
            var client = bounds;
            if (GetClientRect(hwnd, out var cr))
            {
                var origin = new NativePoint();
                if (ClientToScreen(hwnd, ref origin)) client = new Rect(origin.X, origin.Y, Math.Max(0, cr.Right), Math.Max(0, cr.Bottom));
            }
            var areas = new List<Rect>();
            EnumChildWindows(hwnd, (child, _) =>
            {
                // Chromium exposes its page viewport as a native render host, independent of browser toolbar sizes.
                var name = new System.Text.StringBuilder(256);
                GetClassName(child, name, name.Capacity);
                if (IsWindowVisible(child) && name.ToString().Contains("RenderWidgetHost") && GetWindowRect(child, out var rect))
                {
                    var area = Rect.Intersect(bounds, rect.Rect);
                    if (!area.IsEmpty && area.Width > 1 && area.Height > 1) areas.Add(area);
                }
                return true;
            }, IntPtr.Zero);
            _windows.Add(new Target(hwnd, bounds, client, areas));
            return true;
        }, IntPtr.Zero);
    }
    internal Target? At(Point point) => _windows.Find(w => w.Bounds.Contains(point));

    // Called on a worker: accessibility providers belong to other processes and may be slow.
    internal static List<Rect> DocumentAreas(Target target)
    {
        var result = new List<Rect>();
        try
        {
            var root = AutomationElement.FromHandle(target.Handle);
            var documents = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            for (int i = 0; i < Math.Min(documents.Count, 64); i++)
            {
                var current = documents[i].Current;
                if (current.IsOffscreen) continue;
                var area = Rect.Intersect(target.Bounds, current.BoundingRectangle);
                if (!area.IsEmpty && area.Width >= 32 && area.Height >= 32) result.Add(area);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException) { }
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect Rect => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
