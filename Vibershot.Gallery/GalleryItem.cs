using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Vibershot.Gallery;

public enum GalleryItemKind { Image, Video, Project }

/// <summary>A file found by the scan, as the index saw it, plus its sidecar if any.</summary>
public sealed record ScannedFile(string Path, long Size, DateTime Modified, DateTime Created, CaptureSidecar? Sidecar, DateTime? SidecarModified = null);

/// <summary>
/// One file in the library. Bindable; property changes are raised on whichever thread calls the
/// setter, so the index and thumbnail cache post their updates through the UI dispatcher.
/// </summary>
public sealed class GalleryItem : INotifyPropertyChanged
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };
    public static readonly string[] VideoExtensions = { ".avi", ".mp4", ".mkv", ".webm", ".mov" };
    public static readonly string[] ProjectExtensions = { ".vibeshot", ".vibevideo" };

    public static GalleryItemKind? KindFor(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return GalleryItemKind.Image;
        if (VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return GalleryItemKind.Video;
        if (ProjectExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return GalleryItemKind.Project;
        return null;
    }

    private BitmapSource? _thumbnail;
    private bool _isRenaming;
    private string _group = "";
    private int _width, _height;

    public GalleryItem(ScannedFile file)
    {
        Path = file.Path;
        Kind = KindFor(file.Path) ?? GalleryItemKind.Image;
        Sidecar = file.Sidecar ?? new CaptureSidecar { Kind = CaptureKind.Unknown, Created = file.Created };
        Apply(file);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; private set; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
    public GalleryItemKind Kind { get; }
    public long Size { get; private set; }
    public DateTime Modified { get; private set; }

    /// <summary>Capture time from the sidecar, or the file's creation time without one.</summary>
    public DateTime Created { get; private set; }

    public CaptureSidecar Sidecar { get; private set; }
    public bool HasSidecar { get; private set; }
    public DateTime? SidecarModified { get; private set; }

    public CaptureKind CaptureKind => Sidecar.Kind;
    public string? SourceProcess => Sidecar.SourceProcess;
    public string? SourceTitle => Sidecar.SourceTitle;
    public double? Duration => Sidecar.DurationSeconds;
    public IReadOnlyList<string> Tags => Sidecar.Tags;
    public string TagsText => string.Join(", ", Sidecar.Tags);

    public int Width { get => _width; private set { if (_width != value) { _width = value; Changed(); Changed(nameof(Badge)); } } }
    public int Height { get => _height; private set { if (_height != value) { _height = value; Changed(); Changed(nameof(Badge)); } } }

    public bool Favourite
    {
        get => Sidecar.Favourite;
        set { if (Sidecar.Favourite != value) { Sidecar.Favourite = value; Changed(); } }
    }

    /// <summary>Frozen, cache-sized preview; null until the thumbnail cache delivers one.</summary>
    public BitmapSource? Thumbnail { get => _thumbnail; set { if (!ReferenceEquals(_thumbnail, value)) { _thumbnail = value; Changed(); } } }

    public bool IsRenaming { get => _isRenaming; set { if (_isRenaming != value) { _isRenaming = value; Changed(); } } }

    /// <summary>Header the view groups this item under (set by the window from the current sort).</summary>
    public string Group { get => _group; set { if (_group != value) { _group = value; Changed(); } } }

    /// <summary>Tile badge: duration for videos, pixel size for images, extension for projects.</summary>
    public string Badge => Kind switch
    {
        GalleryItemKind.Video => Duration is double d ? FormatDuration(d) : Width > 0 ? $"{Width}×{Height}" : "video",
        GalleryItemKind.Project => Extension,
        _ => Width > 0 ? $"{Width}×{Height}" : "",
    };

    /// <summary>Second caption line: the source app, else the capture kind.</summary>
    public string Hint => !string.IsNullOrEmpty(SourceProcess) ? SourceProcess! : Sidecar.Kind switch
    {
        CaptureKind.FullDesktop => "Full desktop",
        CaptureKind.Scrolling => "Scrolling photo",
        CaptureKind.Region => "Region",
        CaptureKind.Window => "Window",
        CaptureKind.Video => "Video",
        CaptureKind.Imported => "Imported",
        _ => Kind == GalleryItemKind.Project ? "Project" : Created.ToString("HH:mm"),
    };

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Refreshes file facts and sidecar from a rescan. Returns true when anything changed.</summary>
    internal bool Apply(ScannedFile file)
    {
        bool changed = Size != file.Size || Modified != file.Modified || SidecarModified != file.SidecarModified || HasSidecar != (file.Sidecar is not null);
        Size = file.Size; Modified = file.Modified;
        HasSidecar = file.Sidecar is not null;
        SidecarModified = file.SidecarModified;
        if (file.Sidecar is not null) Sidecar = file.Sidecar;
        else if (!HasSidecar && Sidecar.Kind == CaptureKind.Unknown) Sidecar.Created = file.Created;
        Created = HasSidecar ? Sidecar.Created : file.Created;
        if (Sidecar.Width > 0) { Width = Sidecar.Width; Height = Sidecar.Height; }
        if (changed) NotifyAll();
        return changed;
    }

    /// <summary>Pixel size learned while decoding a thumbnail, for files without a sidecar.</summary>
    public void SetProbedSize(int width, int height)
    {
        if (Width == 0 && width > 0) { Width = width; Height = height; }
    }

    /// <summary>Called after the file (and sidecar) moved on disk.</summary>
    internal void Moved(string newPath)
    {
        Path = newPath;
        Changed(nameof(Path)); Changed(nameof(Name)); Changed(nameof(Folder)); Changed(nameof(Extension));
    }

    public void SaveSidecar()
    {
        Sidecar.Write(Path);
        HasSidecar = true;
        try { SidecarModified = File.GetLastWriteTime(CaptureSidecar.PathFor(Path)); } catch { }
        NotifyAll();
    }

    public void NotifyAll() => Changed(string.Empty);

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}
