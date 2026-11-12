using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Vibershot.Gallery;

/// <summary>
/// Asks the Windows shell for the same frame Explorer shows for a video and falls back to the
/// film tile when no thumbnail handler covers the format. No ffmpeg involved.
/// </summary>
public sealed class ShellVideoThumbnailer : IVideoThumbnailer
{
    private readonly FilmTileThumbnailer _fallback = new();

    public BitmapSource? CreateThumbnail(string path, int longestEdge) => FromShell(path, longestEdge) ?? _fallback.CreateThumbnail(path, longestEdge);

    public static BitmapSource? FromShell(string path, int longestEdge)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var factory = (IShellItemImageFactory)SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid);
            // THUMBNAILONLY: never hand back the generic file icon.
            int hr = factory.GetImage(new NativeSize { Width = longestEdge, Height = longestEdge }, SIIGBF_RESIZETOFIT | SIIGBF_THUMBNAILONLY, out hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally { if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap); }
    }

    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern object SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
