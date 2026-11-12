namespace Vibershot.Core;

/// <summary>One extra folder a screenshot is written to besides the library.</summary>
public sealed class SaveDestination
{
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// What happens after a screenshot. Every switch is independent: any mix of saving, copying,
/// opening the gallery and opening the editor is allowed, and with everything off the capture
/// is only shown in the preview (when that is on) and otherwise discarded.
/// Stored under <c>AfterCapture</c> in config.json.
/// </summary>
public sealed class AfterCaptureSettings
{
    /// <summary>Write the capture to disk at all.</summary>
    public bool SaveAsFile { get; set; } = true;

    /// <summary>Show a Save As dialog for every capture instead of writing to the destinations below.</summary>
    public bool AskWhereToSave { get; set; } = false;

    /// <summary>Write a copy into the library's Screenshots folder. The gallery only shows this copy.</summary>
    public bool SaveToLibrary { get; set; } = true;

    /// <summary>Extra folders that each receive their own copy.</summary>
    public List<SaveDestination> Destinations { get; set; } = new();

    /// <summary>File format for saved captures; the editable project format keeps full quality.</summary>
    public ImageFormat Format { get; set; } = ImageFormat.Vibeshot;

    /// <summary>Quality for lossy WebP, 1-100. Only used when <see cref="Format"/> is <see cref="ImageFormat.Webp"/>.</summary>
    public int WebpQuality { get; set; } = 80;

    public bool CopyToClipboard { get; set; } = true;

    /// <summary>Open the gallery with the new file selected. Needs the library copy, so it implies <see cref="SaveToLibrary"/>.</summary>
    public bool OpenGallery { get; set; } = false;

    /// <summary>Open the capture in the editor (on the saved project when there is one).</summary>
    public bool OpenEditor { get; set; } = false;

    /// <summary>Show the small preview window when neither the gallery nor the editor opens.</summary>
    public bool ShowPreview { get; set; } = true;

    /// <summary>Every folder an automatic save writes to, library first. Empty when saving is off or asks each time.</summary>
    public IReadOnlyList<string> EnabledFolders(GallerySettings gallery)
    {
        var folders = new List<string>();
        if (!SaveAsFile || AskWhereToSave) return folders;
        if (SaveToLibrary || OpenGallery) folders.Add(gallery.ScreenshotsFolder);
        foreach (var destination in Destinations)
        {
            if (!destination.Enabled || string.IsNullOrWhiteSpace(destination.Path)) continue;
            if (folders.Any(f => SameFolder(f, destination.Path))) continue;
            folders.Add(destination.Path);
        }
        return folders;
    }

    /// <summary>The folder dialogs start in and the launcher's Open folder opens: the library copy, else the first custom destination.</summary>
    public string PrimaryFolder(GallerySettings gallery)
    {
        if (SaveToLibrary || OpenGallery) return gallery.ScreenshotsFolder;
        var first = Destinations.FirstOrDefault(d => d.Enabled && !string.IsNullOrWhiteSpace(d.Path));
        return first?.Path ?? gallery.ScreenshotsFolder;
    }

    public static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(a).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                System.IO.Path.GetFullPath(b).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>Named combinations offered by the settings page. Returns the switches each one sets.</summary>
    public static readonly (string Name, bool Save, bool Copy, bool Gallery, bool Editor)[] Presets =
    {
        ("Quick copy", false, true, false, false),
        ("Save to library", true, true, false, false),
        ("Save and edit", true, false, false, true),
        ("Save and review", true, false, true, false),
    };

    /// <summary>Applies a preset by name; unknown names leave the settings alone.</summary>
    public bool ApplyPreset(string name)
    {
        foreach (var preset in Presets)
        {
            if (!string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            SaveAsFile = preset.Save;
            if (preset.Save) { AskWhereToSave = false; SaveToLibrary = true; }
            CopyToClipboard = preset.Copy;
            OpenGallery = preset.Gallery;
            OpenEditor = preset.Editor;
            return true;
        }
        return false;
    }

    /// <summary>The preset whose switches match the current settings, or null for a custom mix.</summary>
    public string? MatchingPreset()
    {
        foreach (var preset in Presets)
        {
            if (SaveAsFile == preset.Save && CopyToClipboard == preset.Copy && OpenGallery == preset.Gallery && OpenEditor == preset.Editor
                && (!preset.Save || (SaveToLibrary && !AskWhereToSave)))
                return preset.Name;
        }
        return null;
    }
}
