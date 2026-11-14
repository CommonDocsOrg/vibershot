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
    public static VideoProject Relink(VideoProject project, Guid sourceId, string newPath) =>
        project with { Sources = project.Sources.Select(source => source.Id == sourceId ? source with { Path = newPath } : source).ToImmutableList() };

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
