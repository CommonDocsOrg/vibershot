using System.Runtime.InteropServices;

namespace Vibershot.Media;

/// <summary>One monitor as Windows enumerates it; the index is what ddagrab's output_idx expects.</summary>
public sealed record DisplayInfo(int Index, PixelRect Bounds, bool IsPrimary, string DeviceName);

/// <summary>
/// Monitor enumeration via user32 so the recorder can pick the DXGI output containing a region.
/// EnumDisplayMonitors lists outputs in the same order DXGI exposes them on single-adapter
/// systems; gdigrab (absolute desktop coordinates) is the fallback when that assumption fails.
/// </summary>
public static class Displays
{
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var list = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeRect __, IntPtr ___) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var r = info.Monitor;
                list.Add(new DisplayInfo(list.Count, new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (info.Flags & 1) != 0, info.Device ?? ""));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The display sharing the most area with <paramref name="region"/>, or null when it touches none.</summary>
    public static DisplayInfo? Containing(PixelRect region, IReadOnlyList<DisplayInfo> displays)
    {
        DisplayInfo? best = null;
        long bestArea = 0;
        foreach (var display in displays)
        {
            long area = display.Bounds.Intersect(region).Area;
            if (area > bestArea) { bestArea = area; best = display; }
        }
        return best;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
