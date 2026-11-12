using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vibershot.Gallery;

/// <summary>File operations behind the gallery actions: Recycle Bin delete, rename, import, shell open.</summary>
public static class GalleryFiles
{
    /// <summary>Moves the media file, its sidecar and its cached thumbnail to the Recycle Bin.</summary>
    public static void Delete(GalleryItem item, ThumbnailCache? thumbnails)
    {
        thumbnails?.Delete(item);
        var sidecar = CaptureSidecar.PathFor(item.Path);
        RecycleBin(item.Path);
        if (File.Exists(sidecar)) { try { File.Delete(sidecar); } catch { } }
    }

    /// <summary>Sends one file to the Recycle Bin (silent, undoable) instead of deleting it outright.</summary>
    public static void RecycleBin(string path)
    {
        if (!File.Exists(path)) return;
        var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = path + "\0\0", fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI };
        int result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted) throw new IOException($"Could not move to the Recycle Bin (error {result}): {path}");
    }

    /// <summary>Renames the file and its sidecar in place; the extension is kept. Returns the new path.</summary>
    public static string Rename(GalleryItem item, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) throw new ArgumentException("Enter a name.");
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("A name cannot contain \\ / : * ? \" < > |");
        var ext = Path.GetExtension(item.Path);
        if (!newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName += ext;
        var target = Path.Combine(item.Folder, newName);
        if (string.Equals(target, item.Path, StringComparison.Ordinal)) return item.Path;
        if (File.Exists(target) && !string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("A file with that name already exists.");
        File.Move(item.Path, target);
        var sidecar = CaptureSidecar.PathFor(item.Path);
        if (File.Exists(sidecar)) { try { File.Move(sidecar, CaptureSidecar.PathFor(target), overwrite: true); } catch { } }
        return target;
    }

    /// <summary>Copies an outside file into <paramref name="folder"/> with an "Imported" sidecar. Returns the new path, or null for unsupported types.</summary>
    public static string? Import(string source, string folder)
    {
        if (GalleryItem.KindFor(source) is null || !File.Exists(source)) return null;
        Directory.CreateDirectory(folder);
        var target = UniquePath(Path.Combine(folder, Path.GetFileName(source)));
        File.Copy(source, target);
        var sourceSidecar = CaptureSidecar.PathFor(source);
        if (File.Exists(sourceSidecar)) { try { File.Copy(sourceSidecar, CaptureSidecar.PathFor(target), overwrite: true); return target; } catch { } }
        var sidecar = new CaptureSidecar { Kind = CaptureKind.Imported, Created = DateTime.Now };
        if (GalleryItem.KindFor(target) == GalleryItemKind.Image && ThumbnailCache.ProbeSize(target) is var (w, h)) { sidecar.Width = w; sidecar.Height = h; }
        sidecar.TryWrite(target);
        return target;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
