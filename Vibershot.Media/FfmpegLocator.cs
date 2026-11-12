using System.Diagnostics;

namespace Vibershot.Media;

/// <summary>Result of <see cref="FfmpegLocator.Probe"/>: where ffmpeg is and whether it runs.</summary>
public sealed record FfmpegProbeResult(bool Ok, string? FfmpegPath, string? FfprobePath, string? Version, string? Error)
{
    public override string ToString() => Ok ? $"{Version} ({FfmpegPath})" : Error ?? "ffmpeg not found";
}

/// <summary>
/// Finds ffmpeg.exe and ffprobe.exe: the app's own <c>ffmpeg\</c> output folder first, then
/// <c>tools\ffmpeg</c> above the executable for development builds, then PATH.
/// </summary>
public static class FfmpegLocator
{
    private static readonly object Gate = new();
    private static string? _ffmpeg, _ffprobe;
    private static bool _searched;

    /// <summary>Override for tests or a user-chosen folder; null restores the search.</summary>
    public static void UseFolder(string? folder)
    {
        lock (Gate)
        {
            _searched = folder is not null;
            _ffmpeg = folder is null ? null : Path.Combine(folder, "ffmpeg.exe");
            _ffprobe = folder is null ? null : Path.Combine(folder, "ffprobe.exe");
        }
    }

    public static string? FfmpegPath { get { EnsureSearched(); return _ffmpeg; } }
    public static string? FfprobePath { get { EnsureSearched(); return _ffprobe; } }
    public static bool IsAvailable => FfmpegPath is not null && File.Exists(FfmpegPath);

    /// <summary>Every folder the search looks in, for diagnostics.</summary>
    public static IEnumerable<string> SearchFolders()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "ffmpeg");
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg");
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry.Trim('"');
        }
    }

    private static void EnsureSearched()
    {
        lock (Gate)
        {
            if (_searched) return;
            _searched = true;
            foreach (var folder in SearchFolders())
            {
                try
                {
                    string ffmpeg = Path.Combine(folder, "ffmpeg.exe");
                    if (!File.Exists(ffmpeg)) continue;
                    _ffmpeg = ffmpeg;
                    string ffprobe = Path.Combine(folder, "ffprobe.exe");
                    _ffprobe = File.Exists(ffprobe) ? ffprobe : null;
                    return;
                }
                catch (Exception)
                {
                    // Unreadable PATH entry; keep looking.
                }
            }
        }
    }

    /// <summary>Runs <c>ffmpeg -version</c> and returns the first line, or an error message.</summary>
    public static FfmpegProbeResult Probe()
    {
        var path = FfmpegPath;
        if (path is null)
        {
            return new FfmpegProbeResult(false, null, null, null, "ffmpeg.exe not found. Run tools\\get-ffmpeg.ps1 or put ffmpeg in the app's ffmpeg folder.");
        }
        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-version");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                return new FfmpegProbeResult(false, path, FfprobePath, null, "ffmpeg -version did not finish.");
            }
            string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            if (process.ExitCode != 0 || !first.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            {
                return new FfmpegProbeResult(false, path, FfprobePath, null, $"ffmpeg at {path} failed to run (exit {process.ExitCode}).");
            }
            // "ffmpeg version N-118223-g1a2b3c Copyright ..." -> "ffmpeg version N-118223-g1a2b3c"
            int copyright = first.IndexOf(" Copyright", StringComparison.OrdinalIgnoreCase);
            string version = copyright > 0 ? first[..copyright] : first;
            return new FfmpegProbeResult(true, path, FfprobePath, version, null);
        }
        catch (Exception ex)
        {
            return new FfmpegProbeResult(false, path, FfprobePath, null, $"ffmpeg at {path} failed to run: {ex.Message}");
        }
    }
}
