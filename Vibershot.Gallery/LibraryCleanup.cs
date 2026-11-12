using Vibershot.Core;

namespace Vibershot.Gallery;

/// <summary>What one cleanup pass moved to the Recycle Bin.</summary>
public sealed record CleanupReport(int Screenshots, int Recordings, int Recovery, int Failed)
{
    public int Count => Screenshots + Recordings + Recovery;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Screenshots > 0) parts.Add($"{Screenshots} screenshot{(Screenshots == 1 ? "" : "s")}");
            if (Recordings > 0) parts.Add($"{Recordings} recording{(Recordings == 1 ? "" : "s")}");
            if (Recovery > 0) parts.Add($"{Recovery} recovery file{(Recovery == 1 ? "" : "s")}");
            var text = parts.Count == 0 ? "Library cleanup: nothing to remove" : "Library cleanup moved " + string.Join(", ", parts) + " to the Recycle Bin";
            return Failed > 0 ? text + $" ({Failed} could not be moved)" : text;
        }
    }
}

/// <summary>
/// Moves old files out of the library's own folders. Scope is deliberately narrow: the library's
/// Screenshots and Recordings folders and the recording recovery folder, never custom save
/// destinations, watched folders or the Projects folder. Age comes from the sidecar's capture
/// time when there is one, else the file's creation time. Files go to the Recycle Bin.
/// </summary>
public static class LibraryCleanup
{
    /// <summary>Runs a pass when cleanup is on and the last pass is more than a day old. Returns null when nothing ran.</summary>
    public static CleanupReport? RunIfDue(AppConfig config, DateTime now)
    {
        var settings = config.Gallery.Cleanup;
        if (!settings.Enabled) return null;
        if (settings.LastRun is DateTime last && now - last < TimeSpan.FromDays(1)) return null;
        var report = Run(config, now);
        settings.LastRun = now;
        return report;
    }

    public static CleanupReport Run(AppConfig config, DateTime now) => Run(config.Gallery, GalleryIndex.RecoveryFolder, now, GalleryFiles.RecycleBin);

    /// <summary>The pass itself, with the delete action injectable so tests do not touch the Recycle Bin.</summary>
    public static CleanupReport Run(GallerySettings gallery, string recoveryFolder, DateTime now, Action<string> remove)
    {
        var settings = gallery.Cleanup;
        int screenshots = 0, recordings = 0, recovery = 0, failed = 0;
        if (settings.Screenshots) screenshots = Sweep(gallery.ScreenshotsFolder, now.AddDays(-CleanupSettings.ClampDays(settings.ScreenshotDays)), remove, ref failed);
        if (settings.Recordings) recordings = Sweep(gallery.RecordingsFolder, now.AddDays(-CleanupSettings.ClampDays(settings.RecordingDays)), remove, ref failed);
        if (settings.Recovery && !AfterCaptureSettings.SameFolder(recoveryFolder, gallery.RecordingsFolder))
            recovery = Sweep(recoveryFolder, now.AddDays(-CleanupSettings.ClampDays(settings.RecoveryDays)), remove, ref failed);
        return new CleanupReport(screenshots, recordings, recovery, failed);
    }

    /// <summary>Files in <paramref name="folder"/> (not subfolders) older than <paramref name="cutoff"/>; sidecars go with their media.</summary>
    public static List<string> Candidates(string folder, DateTime cutoff)
    {
        var result = new List<string>();
        if (!Directory.Exists(folder)) return result;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (CaptureSidecar.IsSidecar(path)) continue;
            if (GalleryItem.KindFor(path) is null or GalleryItemKind.Project) continue;
            DateTime created;
            try { created = CaptureSidecar.Read(path)?.Created ?? File.GetCreationTime(path); }
            catch { continue; }
            if (created < cutoff) result.Add(path);
        }
        return result;
    }

    private static int Sweep(string folder, DateTime cutoff, Action<string> remove, ref int failed)
    {
        int moved = 0;
        foreach (var path in Candidates(folder, cutoff))
        {
            try
            {
                remove(path);
                var sidecar = CaptureSidecar.PathFor(path);
                if (File.Exists(sidecar)) { try { File.Delete(sidecar); } catch { } }
                moved++;
            }
            catch { failed++; }
        }
        return moved;
    }
}
