using System.Collections.ObjectModel;
using Vibershot.Core;

namespace Vibershot.Gallery;

/// <summary>
/// Folder scan plus sidecars, no database. <see cref="Items"/> is mutated only through
/// <c>post</c>, so a WPF caller passes a dispatcher and tests pass an inline runner.
/// </summary>
public sealed class GalleryIndex : IDisposable
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private readonly Action<Action> _post;
    private readonly Dictionary<string, GalleryItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private readonly object _scanLock = new();
    private Task? _scan;
    private bool _rescanQueued, _disposed;

    public GalleryIndex(IEnumerable<string> folders, Action<Action>? post = null)
    {
        _post = post ?? (action => action());
        Folders = Dedupe(folders);
        _debounce = new Timer(_ => RescanAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ObservableCollection<GalleryItem> Items { get; } = new();

    public IReadOnlyList<string> Folders { get; }

    /// <summary>Raised (through <c>post</c>) after every merge that touched the collection.</summary>
    public event Action? Changed;

    /// <summary>Raised (through <c>post</c>) for every item a merge added.</summary>
    public event Action<GalleryItem>? ItemAdded;

    public bool IsScanning { get { lock (_scanLock) return _scan is { IsCompleted: false }; } }

    /// <summary>Recovery copies of recordings that were never saved (also cleaned up by the library tidy pass).</summary>
    public static string RecoveryFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Recordings");

    /// <summary>
    /// The library's own folders, the recovery recordings folder and the extra watched folders.
    /// Custom save destinations are deliberately not included: a capture written to the library
    /// and to a custom folder would otherwise show twice.
    /// </summary>
    public static List<string> DefaultFolders(AppConfig config)
    {
        var list = new List<string>
        {
            config.Gallery.ScreenshotsFolder,
            config.Gallery.RecordingsFolder,
            config.Gallery.ProjectsFolder,
            RecoveryFolder,
        };
        list.AddRange(config.Gallery.ExtraFolders.Where(f => !string.IsNullOrWhiteSpace(f)));
        return Dedupe(list);
    }

    /// <summary>Drops duplicates and folders nested inside another listed folder (the scan is recursive).</summary>
    public static List<string> Dedupe(IEnumerable<string> folders)
    {
        var full = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f =>
        {
            try { return Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return ""; }
        }).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return full.Where(f => !full.Any(other => !string.Equals(other, f, StringComparison.OrdinalIgnoreCase)
            && f.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public GalleryItem? Find(string path) { lock (_byPath) return _byPath.TryGetValue(path, out var item) ? item : null; }

    /// <summary>Lists media and project files with their sidecars. Safe on any thread.</summary>
    public static List<ScannedFile> Scan(IEnumerable<string> folders)
    {
        var result = new List<ScannedFile>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", Enumeration); }
            catch { continue; }
            foreach (var path in files)
            {
                if (GalleryItem.KindFor(path) is null) continue;
                var scanned = Describe(path);
                if (scanned is not null) result.Add(scanned);
            }
        }
        return result;
    }

    public static ScannedFile? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var sidecarPath = CaptureSidecar.PathFor(path);
            DateTime? sidecarModified = File.Exists(sidecarPath) ? File.GetLastWriteTime(sidecarPath) : null;
            return new ScannedFile(info.FullName, info.Length, info.LastWriteTime, info.CreationTime, CaptureSidecar.Read(path), sidecarModified);
        }
        catch { return null; }
    }

    /// <summary>Scans on the thread pool and merges on the posting thread. Overlapping calls coalesce.</summary>
    public Task RescanAsync()
    {
        lock (_scanLock)
        {
            if (_disposed) return Task.CompletedTask;
            if (_scan is { IsCompleted: false }) { _rescanQueued = true; return _scan; }
            _scan = Task.Run(async () =>
            {
                do
                {
                    _rescanQueued = false;
                    var files = Scan(Folders);
                    var done = new TaskCompletionSource();
                    _post(() => { try { Merge(files); } finally { done.SetResult(); } });
                    await done.Task.ConfigureAwait(false);
                    EnsureWatchers();
                } while (_rescanQueued && !_disposed);
            });
            return _scan;
        }
    }

    /// <summary>Reconciles <see cref="Items"/> with a scan result: adds, updates and removes. Call on the posting thread.</summary>
    public void Merge(IReadOnlyList<ScannedFile> files)
    {
        bool changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = new List<GalleryItem>();
        lock (_byPath)
        {
            foreach (var file in files)
            {
                if (!seen.Add(file.Path)) continue;
                if (_byPath.TryGetValue(file.Path, out var existing)) { changed |= existing.Apply(file); continue; }
                var item = new GalleryItem(file);
                _byPath[file.Path] = item;
                Items.Add(item);
                added.Add(item);
                changed = true;
            }
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(Items[i].Path)) continue;
                _byPath.Remove(Items[i].Path);
                Items.RemoveAt(i);
                changed = true;
            }
        }
        foreach (var item in added) ItemAdded?.Invoke(item);
        if (changed) Changed?.Invoke();
    }

    /// <summary>Describes one file on disk and adds or updates it immediately (used after an import or save).</summary>
    public GalleryItem? Refresh(string path)
    {
        var scanned = Describe(path);
        if (scanned is null || GalleryItem.KindFor(path) is null) return null;
        lock (_byPath)
        {
            if (_byPath.TryGetValue(scanned.Path, out var existing)) { if (existing.Apply(scanned)) Changed?.Invoke(); return existing; }
            var item = new GalleryItem(scanned);
            _byPath[scanned.Path] = item;
            Items.Add(item);
            ItemAdded?.Invoke(item);
            Changed?.Invoke();
            return item;
        }
    }

    public void Remove(GalleryItem item)
    {
        lock (_byPath)
        {
            if (!_byPath.Remove(item.Path)) return;
            Items.Remove(item);
        }
        Changed?.Invoke();
    }

    /// <summary>Re-keys an item after its file moved.</summary>
    public void Rekey(GalleryItem item, string newPath)
    {
        lock (_byPath)
        {
            _byPath.Remove(item.Path);
            item.Moved(newPath);
            _byPath[newPath] = item;
        }
        Changed?.Invoke();
    }

    /// <summary>Starts watching every existing folder; files appearing later trigger a debounced rescan.</summary>
    public void Watch() => EnsureWatchers();

    private void EnsureWatchers()
    {
        lock (_watchers)
        {
            if (_disposed) return;
            foreach (var folder in Folders)
            {
                if (_watchers.ContainsKey(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName };
                    watcher.Created += OnFileEvent; watcher.Deleted += OnFileEvent; watcher.Renamed += OnFileEvent; watcher.Changed += OnFileEvent;
                    watcher.Error += (_, _) => Bump();
                    watcher.EnableRaisingEvents = true;
                    _watchers[folder] = watcher;
                }
                catch { /* A folder that cannot be watched is still rescanned on activation. */ }
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        bool relevant = GalleryItem.KindFor(e.FullPath) is not null || CaptureSidecar.IsSidecar(e.FullPath) || e is RenamedEventArgs;
        if (relevant) Bump();
    }

    // Saves and copies arrive as bursts of events; one rescan after the burst is enough.
    private void Bump() { try { _debounce.Change(600, Timeout.Infinite); } catch (ObjectDisposedException) { } }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Dispose();
        lock (_watchers)
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
