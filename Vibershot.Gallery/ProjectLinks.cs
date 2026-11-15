using System.Text.Json;

namespace Vibershot.Gallery;

/// <summary>
/// Which media files the known video projects link to. Built by reading only the source list of
/// each <c>.vibevideo</c> file, so the library can refuse to tidy away (or warn before deleting)
/// a recording that a project still needs.
/// </summary>
public sealed class ProjectLinks
{
    public const string ProjectExtension = ".vibevideo";

    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private readonly Dictionary<string, List<string>> _users = new(StringComparer.OrdinalIgnoreCase);

    public static ProjectLinks Empty => new();

    /// <summary>Where the video editor keeps autosaved copies of projects with unsaved edits.</summary>
    public static string AutosaveFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Recovery");

    /// <summary>Projects that could not be read; their links are unknown.</summary>
    public int Unreadable { get; private set; }

    /// <summary>Number of distinct media files some project links to.</summary>
    public int Count => _users.Count;

    /// <summary>Reads every video project found in <paramref name="folders"/> and their subfolders.</summary>
    public static ProjectLinks Scan(IEnumerable<string> folders)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            try { foreach (var path in Directory.EnumerateFiles(folder, "*" + ProjectExtension, Enumeration)) projects.Add(path); }
            catch { /* A folder that cannot be listed has no projects we can know about. */ }
        }
        return From(projects);
    }

    /// <summary>Reads the given project files.</summary>
    public static ProjectLinks From(IEnumerable<string> projectPaths)
    {
        var links = new ProjectLinks();
        foreach (var project in projectPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The pattern "*.vibevideo" also matches longer extensions on some volumes.
            if (!project.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase)) continue;
            List<string> sources;
            try { sources = Sources(project); }
            catch { links.Unreadable++; continue; }
            foreach (var source in sources)
            {
                if (!links._users.TryGetValue(source, out var users)) links._users[source] = users = new List<string>();
                if (!users.Contains(project, StringComparer.OrdinalIgnoreCase)) users.Add(project);
            }
        }
        return links;
    }

    /// <summary>
    /// The media files one project links to, as full paths: both where each file was when the
    /// project was saved and where it would be beside the project now, since either may be the
    /// copy the project opens. Throws when the file cannot be read as a project.
    /// </summary>
    public static List<string> Sources(string projectPath)
    {
        var result = new List<string>();
        string folder = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        using var document = JsonDocument.Parse(File.ReadAllText(projectPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Not a project file.");
        if (!document.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return result;
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object) continue;
            Add(result, Text(source, "Path"), null);
            Add(result, Text(source, "RelativePath"), folder);
        }
        return result;
    }

    /// <summary>The projects that link to <paramref name="mediaPath"/>; empty when none do.</summary>
    public IReadOnlyList<string> UsedBy(string mediaPath)
    {
        try { mediaPath = Path.GetFullPath(mediaPath); } catch { }
        return _users.TryGetValue(mediaPath, out var users) ? users : Array.Empty<string>();
    }

    public bool IsUsed(string mediaPath) => UsedBy(mediaPath).Count > 0;

    /// <summary>A project's name as shown to the user; an autosave is named after the project it belongs to.</summary>
    public static string DisplayName(string projectPath)
    {
        string name = Path.GetFileNameWithoutExtension(projectPath);
        return name.EndsWith(".autosave", StringComparison.OrdinalIgnoreCase) ? name[..^".autosave".Length] + " (unsaved edits)" : name;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Add(List<string> list, string? path, string? relativeTo)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = relativeTo is null ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(relativeTo, path));
            if (!list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
        }
        catch { /* A path this machine cannot even parse links to nothing here. */ }
    }
}
