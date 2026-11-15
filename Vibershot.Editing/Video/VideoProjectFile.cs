using System.Collections.Immutable;
using System.IO;

namespace Vibershot.Editing.Video;

/// <summary>
/// Reads and writes <c>.vibevideo</c> projects: one JSON file that links to its media by absolute
/// path and by path relative to the project, so a folder holding both can be moved or shared.
/// </summary>
public static class VideoProjectFile
{
    public const string Extension = ".vibevideo";

    /// <summary>Writes the project next to a temp file and swaps it in, so a failed save never truncates the old one.</summary>
    public static VideoProject Save(string path, VideoProject project)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(folder);
        var stamped = project with
        {
            Version = VideoProject.CurrentVersion,
            Sources = project.Sources.Select(source => source with { RelativePath = Relative(folder, source.Path) }).ToImmutableList(),
        };
        string temp = path + ".tmp";
        File.WriteAllText(temp, DocumentJson.Serialize(stamped));
        File.Move(temp, path, overwrite: true);
        return stamped;
    }

    /// <summary>
    /// Loads a project. A source whose absolute path is gone is looked up by its relative path
    /// beside the project file; sources still missing afterwards are reported by <see cref="Missing"/>.
    /// </summary>
    public static VideoProject Load(string path)
    {
        var project = DocumentJson.Deserialize<VideoProject>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The project file is empty.");
        if (project.Version > VideoProject.CurrentVersion)
            throw new InvalidDataException($"This project was saved by a newer Vibershot (format {project.Version}).");
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return project with
        {
            Sources = project.Sources.Select(source =>
            {
                if (File.Exists(source.Path) || string.IsNullOrEmpty(source.RelativePath)) return source;
                string candidate = Path.GetFullPath(Path.Combine(folder, source.RelativePath));
                return File.Exists(candidate) ? source with { Path = candidate } : source;
            }).ToImmutableList(),
        };
    }

    public static IReadOnlyList<MediaSource> Missing(VideoProject project) =>
        project.Sources.Where(source => !File.Exists(source.Path)).ToList();

    /// <summary>Points a source at a new file (after the user locates a missing one).</summary>
    public static VideoProject Relink(VideoProject project, Guid sourceId, string newPath)
    {
        long size = 0;
        try { if (File.Exists(newPath)) size = new FileInfo(newPath).Length; } catch (Exception) { }
        return project with
        {
            Sources = project.Sources.Select(source => source.Id != sourceId ? source
                : source with { Path = newPath, FileSize = size > 0 ? size : source.FileSize }).ToImmutableList(),
        };
    }

    /// <summary>
    /// True when <paramref name="candidate"/> looks like the file <paramref name="source"/> was made
    /// from: the same name and, when the project recorded one, the same size. Recordings share
    /// names such as <c>screen.mp4</c>, so the size is what tells one session's file from another's.
    /// </summary>
    public static bool LooksLike(MediaSource source, string candidate, bool requireSize = true)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(candidate), source.Name, StringComparison.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(candidate);
            if (!info.Exists) return false;
            return source.FileSize > 0 ? info.Length == source.FileSize : !requireSize;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Looks for every missing source in <paramref name="folders"/> and their subfolders, and
    /// relinks the ones found. Only a file with the source's name and recorded size is taken, so
    /// a search never links a project to the wrong recording.
    /// </summary>
    public static VideoProject FindMissing(VideoProject project, IEnumerable<string> folders)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        var roots = folders.Where(folder => !string.IsNullOrWhiteSpace(folder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var source in Missing(project))
        {
            if (source.Name.Length == 0) continue;
            string? found = null;
            foreach (string root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    found = Directory.EnumerateFiles(root, source.Name, options).FirstOrDefault(candidate => LooksLike(source, candidate));
                }
                catch (Exception) { }
                if (found != null) break;
            }
            if (found != null) project = Relink(project, source.Id, found);
        }
        return project;
    }

    /// <summary>
    /// After one file was located by hand: finds the other missing sources that moved with it.
    /// Each is looked for where it would sit relative to the located file (a folder moved as a
    /// whole keeps its shape), then by name in the located file's folder.
    /// </summary>
    public static VideoProject RelinkAlongside(VideoProject project, string oldPath, string newPath)
    {
        string? oldFolder = Path.GetDirectoryName(oldPath), newFolder = Path.GetDirectoryName(newPath);
        if (string.IsNullOrEmpty(oldFolder) || string.IsNullOrEmpty(newFolder)) return project;
        foreach (var source in Missing(project))
        {
            var candidates = new List<string>();
            try
            {
                string relative = Path.GetRelativePath(oldFolder, source.Path);
                if (!Path.IsPathRooted(relative)) candidates.Add(Path.GetFullPath(Path.Combine(newFolder, relative)));
            }
            catch (Exception) { }
            if (source.Name.Length > 0) candidates.Add(Path.Combine(newFolder, source.Name));
            if (candidates.FirstOrDefault(candidate => LooksLike(source, candidate, requireSize: false)) is { } found)
                project = Relink(project, source.Id, found);
        }
        return project;
    }

    private static string? Relative(string folder, string target)
    {
        try
        {
            string relative = Path.GetRelativePath(folder, target);
            return Path.IsPathRooted(relative) ? null : relative;
        }
        catch (Exception) { return null; }
    }
}
