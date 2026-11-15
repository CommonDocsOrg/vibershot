using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Vibershot.Editing.Video;
using Vibershot.Gallery;

namespace Vibershot.UI;

/// <summary>Asked before the gallery deletes media that video projects still link to.</summary>
internal static class LinkedDeleteDialog
{
    public enum Choice { Cancel, DeleteAll, DeleteUnlinked }

    /// <param name="linked">Each selected file a project uses, with the projects that use it.</param>
    /// <param name="unlinked">How many other selected files no project uses.</param>
    public static Choice Show(Window owner, IReadOnlyList<(string File, IReadOnlyList<string> Projects)> linked, int unlinked)
    {
        var result = Choice.Cancel;
        var window = Dialogs.Create(owner, "Vibershot Gallery", 480);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        string headline = linked.Count == 1
            ? $"{Path.GetFileName(linked[0].File)} is used by {Dialogs.Count(linked[0].Projects.Count, "a project", "projects")}"
            : $"{linked.Count} of these files are used by projects";
        panel.Children.Add(new TextBlock { Text = headline, FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Dialogs.Hint("Deleting a file a project uses leaves that project with missing media: its clips show nothing until the file is back. Deleted files go to the Recycle Bin, so they can be restored from there.", new Thickness(0, 6, 0, 12)));

        var list = new StackPanel();
        foreach (var (file, projects) in linked.Take(8))
        {
            list.Children.Add(new TextBlock { Text = Path.GetFileName(file), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file });
            list.Children.Add(Dialogs.Hint("Used by " + string.Join(", ", projects.Select(ProjectLinks.DisplayName)), new Thickness(0, 1, 0, 8), string.Join(Environment.NewLine, projects)));
        }
        if (linked.Count > 8) list.Children.Add(Dialogs.Hint($"…and {linked.Count - 8} more.", new Thickness(0, 0, 0, 8)));
        panel.Children.Add(list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, Padding = new Thickness(16, 9, 16, 9) };
        cancel.Click += (_, _) => window.Close();
        buttons.Children.Add(cancel);
        if (unlinked > 0)
        {
            var others = new Button { Content = unlinked == 1 ? "Delete only the other file" : $"Delete only the other {unlinked} files", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 9, 16, 9) };
            others.Click += (_, _) => { result = Choice.DeleteUnlinked; window.Close(); };
            buttons.Children.Add(others);
        }
        var all = new Button { Content = linked.Count + unlinked == 1 ? "Delete anyway" : "Delete all anyway", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 9, 16, 9) };
        all.Click += (_, _) => { result = Choice.DeleteAll; window.Close(); };
        buttons.Children.Add(all);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }
}

/// <summary>
/// Lists the media files a video project cannot find and lets the user point at each one, or at
/// a folder to search. Files that moved together are found together.
/// </summary>
internal static class MissingMediaDialog
{
    /// <summary>Returns the project with every located file relinked (the same instance when nothing was located).</summary>
    public static VideoProject Show(Window owner, VideoProject project)
    {
        var ids = VideoProjectFile.Missing(project).Select(source => source.Id).ToList();
        if (ids.Count == 0) return project;
        var original = project;
        string? lastFolder = null;

        var window = Dialogs.Create(owner, "Missing media", 560);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var headline = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(headline);
        panel.Children.Add(Dialogs.Hint("The files may have been moved, renamed or deleted. Locate each one, or choose a folder to search. When you locate one file, others that moved with it are found too. Clips from files that stay missing show nothing.", new Thickness(0, 6, 0, 14)));
        var list = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var note = Dialogs.Hint("", new Thickness(0, 8, 0, 0));
        panel.Children.Add(note);

        var buttons = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        var search = new Button { Content = "Search a folder…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 9, 16, 9), ToolTip = "Look through a folder and its subfolders for the missing files" };
        var done = new Button { IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18, 9, 18, 9), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        buttons.Children.Add(search);
        buttons.Children.Add(done);
        panel.Children.Add(buttons);

        void Rebuild()
        {
            int missing = VideoProjectFile.Missing(project).Count(source => ids.Contains(source.Id));
            headline.Text = missing == 0 ? "All media files found"
                : missing == 1 ? "1 media file could not be found" : $"{missing} media files could not be found";
            done.Content = missing == 0 ? "Done" : "Continue without them";
            search.IsEnabled = missing > 0;
            list.Children.Clear();
            foreach (var id in ids)
            {
                if (project.Source(id) is not { } source) continue;
                var was = original.Source(id) ?? source;
                bool found = File.Exists(source.Path);
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = was.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var where = Dialogs.Hint(found ? "Found: " + source.Path : "Was at " + was.Path, new Thickness(0, 1, 0, 0), found ? source.Path : was.Path);
                where.TextWrapping = TextWrapping.NoWrap;
                where.TextTrimming = TextTrimming.CharacterEllipsis;
                if (found) where.Foreground = (Brush)Application.Current.FindResource("Ok");
                text.Children.Add(where);
                row.Children.Add(text);
                var locate = new Button { Content = found ? "Change…" : "Locate…", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(locate, (found ? "Change " : "Locate ") + was.Name);
                Grid.SetColumn(locate, 1);
                locate.Click += (_, _) =>
                {
                    if (PickFile(window, was, lastFolder) is not { } picked) return;
                    lastFolder = Path.GetDirectoryName(picked);
                    int before = VideoProjectFile.Missing(project).Count;
                    project = VideoProjectFile.Relink(project, id, picked);
                    project = VideoProjectFile.RelinkAlongside(project, was.Path, picked);
                    int also = before - VideoProjectFile.Missing(project).Count - (found ? 0 : 1);
                    note.Text = also > 0 ? $"Also found {Dialogs.Count(also, "file", "files", spellOne: false)} that moved with it." : "";
                    Rebuild();
                };
                row.Children.Add(locate);
                list.Children.Add(row);
            }
        }

        search.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Folder to search for the missing files" };
            if (lastFolder != null && Directory.Exists(lastFolder)) dialog.InitialDirectory = lastFolder;
            if (dialog.ShowDialog(window) != true) return;
            lastFolder = dialog.FolderName;
            int before = VideoProjectFile.Missing(project).Count;
            Mouse.OverrideCursor = Cursors.Wait;
            try { project = VideoProjectFile.FindMissing(project, new[] { dialog.FolderName }); }
            finally { Mouse.OverrideCursor = null; }
            int found = before - VideoProjectFile.Missing(project).Count;
            note.Text = found == 0 ? "None of the missing files are in that folder. A file is matched by its name and size; use Locate to choose a different file."
                : $"Found {Dialogs.Count(found, "file", "files", spellOne: false)} in that folder.";
            Rebuild();
        };
        done.Click += (_, _) => window.Close();
        Rebuild();
        window.Content = panel;
        window.ShowDialog();
        return project;
    }

    /// <summary>
    /// File picker for one source's new location. A file whose size differs from the one the
    /// project was made with is only taken after the user confirms it. Null when cancelled.
    /// </summary>
    public static string? PickFile(Window owner, MediaSource source, string? startFolder = null)
    {
        string extension = Path.GetExtension(source.Name);
        string filter = extension.Length > 1 ? $"{extension.TrimStart('.').ToUpperInvariant()} files (*{extension})|*{extension}|All files (*.*)|*.*" : "All files (*.*)|*.*";
        var dialog = new OpenFileDialog { Title = $"Locate {source.Name}", Filter = filter, FileName = source.Name, CheckFileExists = true };
        try
        {
            string? folder = startFolder ?? Path.GetDirectoryName(source.Path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        }
        catch (Exception) { }
        if (dialog.ShowDialog(owner) != true) return null;
        long size = 0;
        try { size = new FileInfo(dialog.FileName).Length; } catch (Exception) { }
        if (source.FileSize > 0 && size > 0 && size != source.FileSize)
        {
            var answer = MessageBox.Show(owner,
                $"{Path.GetFileName(dialog.FileName)} is not the same size as the file this project was made with ({GalleryItem.FormatSize(size)} instead of {GalleryItem.FormatSize(source.FileSize)}), so it may be a different recording and clips may not line up.\n\nUse it anyway?",
                "Different file", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return null;
        }
        return dialog.FileName;
    }
}

internal static class Dialogs
{
    public static Window Create(Window owner, string title, double width)
    {
        var window = new Window
        {
            Owner = owner, Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Style = (Style)Application.Current.FindResource(typeof(Window)),
        };
        WindowAppearance.Apply(window);
        return window;
    }

    public static TextBlock Hint(string text, Thickness margin, string? toolTip = null) => new()
    {
        Text = text, Style = (Style)Application.Current.FindResource("Hint"), Margin = margin, TextWrapping = TextWrapping.Wrap, ToolTip = toolTip,
    };

    /// <summary>"a project" / "3 projects"; with <paramref name="spellOne"/> off, "1 file" / "3 files".</summary>
    public static string Count(int count, string one, string many, bool spellOne = true) =>
        count == 1 ? (spellOne ? one : "1 " + one) : $"{count} {many}";
}
