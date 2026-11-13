using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Vibershot.Core;

namespace Vibershot.UI;

/// <summary>Pages of the settings window, in navigation order.</summary>
public enum SettingsPage
{
    Hotkeys,
    AfterCapture,
    Gallery,
    Editor,
    Video,
    General,
}

/// <summary>
/// The settings dialog. Every value is copied into local state on open and only written back to
/// the config on Save, so Cancel leaves the config object untouched.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly List<SaveDestination> _destinations;
    private readonly List<string> _extraFolders;
    private readonly Dictionary<HotkeyAction, string> _hotkeys = new();
    private readonly List<HotkeyRow> _hotkeyRows = new();
    private readonly List<ToggleButton> _presetChips = new();
    private string _libraryRoot;
    private bool _loading = true;
    private bool _syncingActions;

    public SettingsWindow(AppConfig config) : this(config, SettingsPage.AfterCapture) { }

    public SettingsWindow(AppConfig config, SettingsPage page)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _config = config;
        _destinations = config.AfterCapture.Destinations.Select(d => new SaveDestination { Path = d.Path, Enabled = d.Enabled }).ToList();
        _extraFolders = new List<string>(config.Gallery.ExtraFolders);
        foreach (var action in HotkeySettings.Actions) _hotkeys[action] = config.Hotkeys[action];
        _libraryRoot = config.Gallery.LibraryRoot;

        LoadAfterCapturePage();
        LoadHotkeysPage();
        LoadGalleryPage();
        LoadEditorPage();
        LoadVideoPage();
        LoadGeneralPage();
        _loading = false;

        RefreshAfterCapture();
        PrintScreen_Changed(this, new RoutedEventArgs());
        Cleanup_Changed(this, new RoutedEventArgs());
        Page = page;
    }

    /// <summary>The page currently shown; setting it switches pages.</summary>
    public SettingsPage Page
    {
        get => NavHotkeys.IsChecked == true ? SettingsPage.Hotkeys
            : NavGallery.IsChecked == true ? SettingsPage.Gallery
            : NavEditor.IsChecked == true ? SettingsPage.Editor
            : NavVideo.IsChecked == true ? SettingsPage.Video
            : NavGeneral.IsChecked == true ? SettingsPage.General
            : SettingsPage.AfterCapture;
        set
        {
            var nav = value switch
            {
                SettingsPage.Hotkeys => NavHotkeys,
                SettingsPage.Gallery => NavGallery,
                SettingsPage.Editor => NavEditor,
                SettingsPage.Video => NavVideo,
                SettingsPage.General => NavGeneral,
                _ => NavAfterCapture,
            };
            nav.IsChecked = true;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton nav || nav.Tag is not string tag || PageAfterCapture is null) return;
        StopRecording();
        PageAfterCapture.Visibility = tag == "AfterCapture" ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageGallery.Visibility = tag == "Gallery" ? Visibility.Visible : Visibility.Collapsed;
        PageEditor.Visibility = tag == "Editor" ? Visibility.Visible : Visibility.Collapsed;
        PageVideo.Visibility = tag == "Video" ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
    }

    // ---------------------------------------------------------------- After capture

    private void LoadAfterCapturePage()
    {
        foreach (var preset in AfterCaptureSettings.Presets)
        {
            var chip = new ToggleButton { Content = preset.Name, Tag = preset.Name, Margin = new Thickness(0, 0, 8, 0) };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, "Preset " + preset.Name);
            chip.Click += PresetChip_Click;
            _presetChips.Add(chip);
            PresetChips.Children.Add(chip);
        }
        // The editable project first, then the flat formats in the order of the enum.
        foreach (var format in new[] { ImageFormat.Vibeshot, ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.WebpLossless, ImageFormat.Webp })
            FormatComboBox.Items.Add(new ComboBoxItem { Content = ImageEncoding.Describe(format), Tag = format });
        ApplyAfterCapture(_config.AfterCapture);
        RebuildDestinationRows();
    }

    /// <summary>Pushes a settings object into the page controls.</summary>
    private void ApplyAfterCapture(AfterCaptureSettings ac)
    {
        bool wasLoading = _loading;
        _loading = true;
        SaveFileSwitch.IsChecked = ac.SaveAsFile;
        SaveAskRadio.IsChecked = ac.AskWhereToSave;
        SaveAutoRadio.IsChecked = !ac.AskWhereToSave;
        LibraryCheckbox.IsChecked = ac.SaveToLibrary || ac.OpenGallery;
        ClipboardSwitch.IsChecked = ac.CopyToClipboard;
        GallerySwitch.IsChecked = ac.OpenGallery;
        EditorSwitch.IsChecked = ac.OpenEditor;
        PreviewCheckbox.IsChecked = ac.ShowPreview;
        WebpQualitySlider.Value = ImageEncoding.ClampWebpQuality(ac.WebpQuality);
        SelectTag(FormatComboBox, ac.Format);
        _loading = wasLoading;
    }

    /// <summary>The page controls read back as a settings object (destinations shared with the window's copy).</summary>
    private AfterCaptureSettings CurrentAfterCapture() => new()
    {
        SaveAsFile = SaveFileSwitch.IsChecked == true,
        AskWhereToSave = SaveAskRadio.IsChecked == true,
        SaveToLibrary = LibraryCheckbox.IsChecked == true,
        Destinations = _destinations,
        Format = SelectedFormat,
        WebpQuality = ImageEncoding.ClampWebpQuality((int)WebpQualitySlider.Value),
        CopyToClipboard = ClipboardSwitch.IsChecked == true,
        OpenGallery = GallerySwitch.IsChecked == true,
        OpenEditor = EditorSwitch.IsChecked == true,
        ShowPreview = PreviewCheckbox.IsChecked == true,
    };

    private ImageFormat SelectedFormat =>
        (FormatComboBox.SelectedItem as ComboBoxItem)?.Tag is ImageFormat fmt ? fmt : ImageFormat.Vibeshot;

    private void PresetChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton chip || chip.Tag is not string name) return;
        var ac = CurrentAfterCapture();
        ac.ApplyPreset(name);
        ApplyAfterCapture(ac);
        RefreshAfterCapture();
    }

    private void AfterCapture_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshAfterCapture();
    }

    /// <summary>Shows or hides dependent options, forces the library row and updates chips and the summary.</summary>
    private void RefreshAfterCapture()
    {
        if (_loading || SummaryText is null) return;
        bool saving = SaveFileSwitch.IsChecked == true;
        bool gallery = GallerySwitch.IsChecked == true;
        SaveOptions.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        DestinationBox.IsEnabled = SaveAutoRadio.IsChecked == true;
        if (gallery) { _loading = true; LibraryCheckbox.IsChecked = true; _loading = false; }
        LibraryCheckbox.IsEnabled = !gallery;
        LibraryForcedHint.Visibility = gallery && saving ? Visibility.Visible : Visibility.Collapsed;
        WebpQualityPanel.Visibility = SelectedFormat == ImageFormat.Webp ? Visibility.Visible : Visibility.Collapsed;

        var matching = CurrentAfterCapture().MatchingPreset();
        foreach (var chip in _presetChips) chip.IsChecked = matching is not null && (string)chip.Tag == matching;
        SummaryText.Text = ComposeSummary();
    }

    private void WebpQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WebpQualityLabel is not null) WebpQualityLabel.Text = $"{(int)e.NewValue}%";
    }

    private void RebuildDestinationRows()
    {
        LibraryPathText.Text = Path.Combine(_libraryRoot, "Screenshots");
        CustomDestinationRows.Children.Clear();
        foreach (var destination in _destinations)
        {
            var captured = destination;
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new CheckBox { IsChecked = destination.Enabled, VerticalAlignment = VerticalAlignment.Center };
            box.SetResourceReference(StyleProperty, "TickCheckBox");
            AutomationProperties.SetName(box, "Save to " + destination.Path);
            box.Checked += (_, _) => { captured.Enabled = true; RefreshAfterCapture(); };
            box.Unchecked += (_, _) => { captured.Enabled = false; RefreshAfterCapture(); };
            var path = SettingsHelpers.MonoText(destination.Path);
            path.Margin = new Thickness(-4, 0, 10, 0);
            Grid.SetColumn(path, 1);
            var remove = SettingsHelpers.LinkButton("Remove", "Remove " + destination.Path, (_, _) => { _destinations.Remove(captured); RebuildDestinationRows(); RefreshAfterCapture(); });
            Grid.SetColumn(remove, 2);
            row.Children.Add(box);
            row.Children.Add(path);
            row.Children.Add(remove);
            CustomDestinationRows.Children.Add(row);
        }
    }

    private void AddDestination_Click(object sender, RoutedEventArgs e)
    {
        var folder = SettingsHelpers.PickFolder("Choose a folder that receives a copy of every screenshot", _destinations.LastOrDefault()?.Path);
        if (folder is null) return;
        if (AfterCaptureSettings.SameFolder(folder, Path.Combine(_libraryRoot, "Screenshots")))
        {
            _loading = true; LibraryCheckbox.IsChecked = true; _loading = false;
        }
        else if (!_destinations.Any(d => AfterCaptureSettings.SameFolder(d.Path, folder)))
        {
            _destinations.Add(new SaveDestination { Path = folder, Enabled = true });
        }
        RebuildDestinationRows();
        RefreshAfterCapture();
    }

    private void ChangeLibrary_Click(object sender, RoutedEventArgs e) => Page = SettingsPage.Gallery;

    /// <summary>Reads the whole flow back as one short paragraph.</summary>
    private string ComposeSummary()
    {
        var ac = CurrentAfterCapture();
        var text = new StringBuilder("Your setup: ");
        bool printScreenTool = PrintScreenSwitch.IsChecked == true && PsDesktopRadio.IsChecked != true;
        var regionHotkey = HotkeyGesture.TryParse(_hotkeys[HotkeyAction.CaptureRegion], out var g) ? g : HotkeyGesture.Empty;
        text.Append(printScreenTool ? "press Print Screen" : regionHotkey.IsValid ? $"press {regionHotkey}" : "click Capture region");
        text.Append(", pick a region or window");
        if (AskActionsCheckbox.IsChecked == true) text.Append(", choose Screenshot");
        text.Append(". ");

        var opens = new List<string>();
        if (ac.OpenEditor) opens.Add("the editor");
        if (ac.OpenGallery) opens.Add("the gallery");
        string then = opens.Count > 0 ? ", then opens in " + string.Join(" and ", opens) : "";

        bool kept = false;
        if (ac.SaveAsFile)
        {
            kept = true;
            string format = FormatName(ac.Format);
            if (ac.AskWhereToSave)
            {
                text.Append($"You are asked for a name and folder each time and the shot is saved as {format}{then}. ");
            }
            else
            {
                var places = new List<string>();
                if (ac.SaveToLibrary) places.Add("to the library");
                places.AddRange(_destinations.Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.Path)).Select(d => "to " + d.Path));
                text.Append(places.Count > 0
                    ? $"The shot is saved as {format} {string.Join(" and ", places)}{then}. "
                    : $"Saving is on but no folder is checked, so nothing is written{then.Replace(", then", "; the shot still")}. ");
            }
        }
        else if (opens.Count > 0)
        {
            kept = true;
            text.Append($"The shot opens in {string.Join(" and ", opens)} without being saved. ");
        }

        if (ac.CopyToClipboard) { kept = true; text.Append("It is also copied to the clipboard. "); }
        else if (kept) text.Append("Nothing goes to the clipboard. ");

        if (!kept)
            text.Append(ac.ShowPreview ? "The capture is not kept: it only shows in the small preview. " : "The capture is not kept and nothing is shown. ");
        else if (opens.Count == 0)
            text.Append(ac.ShowPreview ? "The small preview appears for a moment." : "No preview is shown.");
        return text.ToString().TrimEnd();
    }

    private static string FormatName(ImageFormat format) => format switch
    {
        ImageFormat.Vibeshot => "a Vibershot project",
        ImageFormat.Png => "PNG",
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Webp => "WebP",
        ImageFormat.WebpLossless => "lossless WebP",
        _ => format.ToString(),
    };

    // ---------------------------------------------------------------- Hotkeys

    /// <summary>One shortcut row: the label, the keycaps or recorder, the buttons and an error line.</summary>
    private sealed class HotkeyRow
    {
        public HotkeyAction Action;
        public Border Frame = new();
        public ContentControl Caps = new();
        public TextBlock Hint = new();
        public Button Change = new();
        public Button Clear = new();
        public HotkeyRecorder Recorder = new();
    }

    private void LoadHotkeysPage()
    {
        PrintScreenSwitch.IsChecked = _config.PrintScreen != PrintScreenAction.Off;
        PsDesktopRadio.IsChecked = _config.PrintScreen == PrintScreenAction.FullDesktop;
        PsAskRadio.IsChecked = _config.PrintScreen != PrintScreenAction.FullDesktop && _config.ShowCaptureActions;
        PsShotRadio.IsChecked = _config.PrintScreen != PrintScreenAction.FullDesktop && !_config.ShowCaptureActions;
        if (SettingsHelpers.PrintScreenBlocked) PrintScreenWarningHost.Content = SettingsHelpers.BuildPrintScreenWarning();

        foreach (var action in HotkeySettings.Actions)
        {
            var row = BuildHotkeyRow(action);
            _hotkeyRows.Add(row);
            HotkeyRows.Children.Add(row.Frame);
            ShowGesture(row);
        }
    }

    private HotkeyRow BuildHotkeyRow(HotkeyAction action)
    {
        var row = new HotkeyRow { Action = action };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = HotkeySettings.Label(action), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        row.Caps.Focusable = false;
        row.Caps.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(row.Caps, 1);
        grid.Children.Add(row.Caps);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        row.Change = SettingsHelpers.LinkButton("Change", "Change shortcut for " + HotkeySettings.Label(action), (_, _) => StartRecording(row));
        row.Clear = SettingsHelpers.LinkButton("Clear", "Clear shortcut for " + HotkeySettings.Label(action), (_, _) => { StopRecording(); _hotkeys[row.Action] = ""; ShowGesture(row); RefreshAfterCapture(); });
        row.Clear.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(row.Change);
        buttons.Children.Add(row.Clear);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        row.Hint.Foreground = SettingsHelpers.Brush("Danger");
        row.Hint.FontSize = 11.5;
        row.Hint.Margin = new Thickness(0, 4, 0, 0);
        row.Hint.Visibility = Visibility.Collapsed;
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(row.Hint);
        row.Frame.Child = stack;
        row.Frame.Padding = new Thickness(10, 8, 10, 8);
        row.Frame.CornerRadius = new CornerRadius(8);
        row.Frame.Margin = new Thickness(-10, 0, -10, 2);
        row.Frame.Background = Brushes.Transparent;

        row.Recorder.Captured += gesture => AcceptGesture(row, gesture);
        row.Recorder.Cancelled += () => ShowGesture(row);
        row.Recorder.Rejected += message => ShowRowHint(row, message);
        return row;
    }

    private HotkeyGesture PendingGesture(HotkeyAction action) =>
        HotkeyGesture.TryParse(_hotkeys[action], out var gesture) ? gesture : HotkeyGesture.Empty;

    /// <summary>Draws the pending gesture as keycaps and leaves recording state.</summary>
    private void ShowGesture(HotkeyRow row)
    {
        var gesture = PendingGesture(row.Action);
        row.Caps.Content = Keycaps.Build(gesture);
        row.Change.Content = gesture.IsEmpty ? "Set" : "Change";
        row.Clear.Visibility = gesture.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        row.Frame.Background = Brushes.Transparent;
    }

    private void ShowRowHint(HotkeyRow row, string? message)
    {
        row.Hint.Text = message ?? "";
        row.Hint.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartRecording(HotkeyRow row)
    {
        StopRecording();
        ShowRowHint(row, null);
        row.Frame.Background = SettingsHelpers.Brush("AccentSoft");
        row.Caps.Content = row.Recorder;
        row.Recorder.Start();
    }

    /// <summary>Ends any recording in progress without changing the pending value.</summary>
    private void StopRecording()
    {
        foreach (var row in _hotkeyRows)
        {
            if (!row.Recorder.IsRecording) continue;
            row.Recorder.Stop();
            ShowGesture(row);
        }
    }

    private void AcceptGesture(HotkeyRow row, HotkeyGesture gesture)
    {
        var pending = new HotkeySettings();
        foreach (var action in HotkeySettings.Actions) pending[action] = _hotkeys[action];
        var conflict = pending.ConflictFor(row.Action, gesture);
        if (conflict is HotkeyAction other)
        {
            ShowGesture(row);
            ShowRowHint(row, "Already used by " + HotkeySettings.Label(other));
            return;
        }
        // The app's own registered shortcuts fail the probe, so anything already saved counts as free.
        bool ours = HotkeySettings.Actions.Any(a => _config.Hotkeys.Gesture(a).ToString() == gesture.ToString());
        if (!ours && !HotkeyManager.IsAvailable(this, gesture))
        {
            ShowGesture(row);
            ShowRowHint(row, "Taken by another app. Pick a different combination.");
            return;
        }
        _hotkeys[row.Action] = gesture.ToString();
        ShowRowHint(row, null);
        ShowGesture(row);
        foreach (var otherRow in _hotkeyRows) if (otherRow != row && otherRow.Hint.Text.StartsWith("Already used", StringComparison.Ordinal)) ShowRowHint(otherRow, null);
        RefreshAfterCapture();
    }

    private void PrintScreen_Changed(object sender, RoutedEventArgs e)
    {
        if (PrintScreenOptions is null || AskActionsCheckbox is null) return;
        PrintScreenOptions.IsEnabled = PrintScreenSwitch.IsChecked == true;
        if (_syncingActions) return;
        // The two region-tool radios are the same setting as "Ask what to capture" under General.
        if (PsAskRadio.IsChecked == true || PsShotRadio.IsChecked == true)
        {
            _syncingActions = true;
            AskActionsCheckbox.IsChecked = PsAskRadio.IsChecked == true;
            _syncingActions = false;
        }
        RefreshAfterCapture();
    }

    private void AskActions_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingActions || PsAskRadio is null) return;
        _syncingActions = true;
        if (PsDesktopRadio.IsChecked != true)
        {
            if (AskActionsCheckbox.IsChecked == true) PsAskRadio.IsChecked = true; else PsShotRadio.IsChecked = true;
        }
        _syncingActions = false;
        RefreshAfterCapture();
    }

    // ---------------------------------------------------------------- Gallery

    private void LoadGalleryPage()
    {
        ShowLibraryRoot();
        RebuildWatchRows();
        var cleanup = _config.Gallery.Cleanup;
        CleanupSwitch.IsChecked = cleanup.Enabled;
        CleanShotsCheckbox.IsChecked = cleanup.Screenshots;
        CleanShotsDays.Text = CleanupSettings.ClampDays(cleanup.ScreenshotDays).ToString(CultureInfo.InvariantCulture);
        CleanVideosCheckbox.IsChecked = cleanup.Recordings;
        CleanVideosDays.Text = CleanupSettings.ClampDays(cleanup.RecordingDays).ToString(CultureInfo.InvariantCulture);
        CleanRecoveryCheckbox.IsChecked = cleanup.Recovery;
        CleanRecoveryDays.Text = CleanupSettings.ClampDays(cleanup.RecoveryDays).ToString(CultureInfo.InvariantCulture);
        ThumbnailSizeSlider.Value = Math.Clamp(_config.Gallery.ThumbnailSize, 120, 360);
        ThumbnailSizeLabel.Text = $"{(int)ThumbnailSizeSlider.Value} px";
    }

    private void ShowLibraryRoot()
    {
        LibraryRootTextBox.Text = _libraryRoot;
        string name;
        try { name = Path.GetFileName(_libraryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch { name = ""; }
        LibraryNameText.Text = (string.IsNullOrEmpty(name) ? _libraryRoot : name) + "\\";
        var hint = "Move keeps the folder layout. Existing files are moved with it.";
        if (SettingsHelpers.IsInOneDrive(_libraryRoot)) hint += " " + SettingsHelpers.OneDriveSentence;
        LibraryHint.Text = hint;
        if (LibraryPathText is not null) LibraryPathText.Text = Path.Combine(_libraryRoot, "Screenshots");
    }

    private void MoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        var folder = SettingsHelpers.PickFolder("Choose where the Vibershot library lives", _libraryRoot);
        if (folder is null) return;
        _libraryRoot = folder;
        ShowLibraryRoot();
        RefreshAfterCapture();
    }

    private void RebuildWatchRows()
    {
        WatchRows.Children.Clear();
        if (_extraFolders.Count == 0)
        {
            WatchRows.Children.Add(SettingsHelpers.Hint("No extra folders yet."));
            return;
        }
        foreach (var folder in _extraFolders)
        {
            var captured = folder;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(SettingsHelpers.MonoText(folder));
            var remove = SettingsHelpers.LinkButton("Remove", "Stop watching " + folder, (_, _) => { _extraFolders.Remove(captured); RebuildWatchRows(); });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            WatchRows.Children.Add(row);
        }
    }

    private void AddWatch_Click(object sender, RoutedEventArgs e)
    {
        var folder = SettingsHelpers.PickFolder("Choose a folder the gallery should also show", _extraFolders.LastOrDefault());
        if (folder is null) return;
        var probe = new GallerySettings { LibraryRoot = _libraryRoot, ExtraFolders = _extraFolders };
        if (!probe.AddExtraFolder(folder))
        {
            MessageBox.Show(this, "That folder is already shown: it is listed or part of the library.", "Vibershot settings");
            return;
        }
        RebuildWatchRows();
    }

    private void Cleanup_Changed(object sender, RoutedEventArgs e)
    {
        if (CleanupOptions is not null) CleanupOptions.IsEnabled = CleanupSwitch.IsChecked == true;
    }

    private void ThumbnailSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThumbnailSizeLabel is not null) ThumbnailSizeLabel.Text = $"{(int)e.NewValue} px";
    }

    /// <summary>Moves the library's own subfolders to a new root; failures are collected, not thrown.</summary>
    private static List<string> MoveLibrary(string oldRoot, string newRoot)
    {
        var failures = new List<string>();
        foreach (var sub in new[] { "Screenshots", "Recordings", "Projects" })
        {
            var source = Path.Combine(oldRoot, sub);
            if (!Directory.Exists(source)) continue;
            var target = Path.Combine(newRoot, sub);
            try
            {
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, file);
                    var destination = Path.Combine(target, relative);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Move(file, destination, overwrite: false);
                    }
                    catch (Exception ex) { failures.Add($"{file}: {ex.Message}"); }
                }
                if (!Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Any()) Directory.Delete(source, recursive: true);
            }
            catch (Exception ex) { failures.Add($"{source}: {ex.Message}"); }
        }
        return failures;
    }

    // ---------------------------------------------------------------- Editor

    private void LoadEditorPage()
    {
        SelectCanvasBackground(_config.Editor.CanvasBackground);
        StrokeColorTextBox.Text = _config.Editor.DefaultStrokeColor;
        StrokeWidthSlider.Value = Math.Clamp(_config.Editor.DefaultStrokeWidth, 1, 24);
        StrokeWidthLabel.Text = $"{(int)StrokeWidthSlider.Value} px";
        FontFamilyTextBox.Text = _config.Editor.DefaultFontFamily;
        FontSizeTextBox.Text = _config.Editor.DefaultFontSize.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void SelectCanvasBackground(string setting)
    {
        uint argb = EditorSettings.ParseArgb(setting) ?? 0xFFFFFFFF;
        string normalized = argb == 0x00000000 ? EditorSettings.Transparent : argb == 0xFFFFFFFF ? EditorSettings.White : argb == 0xFF000000 ? EditorSettings.Black : "custom";
        foreach (ComboBoxItem item in CanvasBackgroundComboBox.Items)
        {
            if (item.Tag is string tag && tag == normalized) { CanvasBackgroundComboBox.SelectedItem = item; break; }
        }
        if (CanvasBackgroundComboBox.SelectedItem is null) CanvasBackgroundComboBox.SelectedIndex = 0;
        CanvasColorTextBox.Text = normalized == "custom" ? FormatArgb(argb) : "";
        UpdateCanvasColorVisibility();
    }

    private static string FormatArgb(uint argb) => (argb >> 24) == 0xFF ? $"#{argb & 0xFFFFFF:X6}" : $"#{argb:X8}";

    private string SelectedCanvasBackground
    {
        get
        {
            var tag = (CanvasBackgroundComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? EditorSettings.White;
            return tag == "custom" ? CanvasColorTextBox.Text.Trim() : tag;
        }
    }

    private void CanvasBackgroundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCanvasColorVisibility();

    private void UpdateCanvasColorVisibility()
    {
        if (CanvasColorPanel is null) return;
        bool custom = (CanvasBackgroundComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "custom";
        CanvasColorPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom && string.IsNullOrWhiteSpace(CanvasColorTextBox.Text)) CanvasColorTextBox.Text = "#F3F4F6";
    }

    private void StrokeWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StrokeWidthLabel is not null) StrokeWidthLabel.Text = $"{(int)e.NewValue} px";
    }

    // ---------------------------------------------------------------- Video

    private void LoadVideoPage()
    {
        foreach (var rate in Vibershot.Media.FrameRate.Presets)
            FrameRateComboBox.Items.Add(new ComboBoxItem { Content = $"{rate} fps", Tag = rate });
        SelectTag(VideoFormatComboBox, _config.Video.Format);
        if (VideoFormatComboBox.SelectedItem is null) VideoFormatComboBox.SelectedIndex = 0;
        SelectFrameRate(_config.Video.FrameRate);
        VideoQualitySlider.Value = VideoSettings.ClampQuality(_config.Video.Quality);
        VideoQualityLabel.Text = $"{(int)VideoQualitySlider.Value}%";
        HardwareEncoderCheckbox.IsChecked = _config.Video.PreferHardwareEncoder;
        CursorCheckbox.IsChecked = _config.Video.ShowCursor;
        UpdateMp4OptionsVisibility();
    }

    private VideoFormat SelectedVideoFormat =>
        (VideoFormatComboBox.SelectedItem as ComboBoxItem)?.Tag is VideoFormat fmt ? fmt : VideoFormat.Mp4;

    private Vibershot.Media.FrameRate SelectedFrameRate =>
        (FrameRateComboBox.SelectedItem as ComboBoxItem)?.Tag is Vibershot.Media.FrameRate rate ? rate : Vibershot.Media.FrameRate.Fps30;

    private void VideoFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMp4OptionsVisibility();

    private void UpdateMp4OptionsVisibility()
    {
        if (Mp4OptionsPanel is not null) Mp4OptionsPanel.Visibility = SelectedVideoFormat == VideoFormat.Mp4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VideoQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VideoQualityLabel is not null) VideoQualityLabel.Text = $"{(int)e.NewValue}%";
    }

    private void SelectFrameRate(string setting)
    {
        var wanted = Vibershot.Media.FrameRate.TryParse(setting, out var parsed) ? parsed : Vibershot.Media.FrameRate.Fps30;
        foreach (ComboBoxItem item in FrameRateComboBox.Items)
        {
            if (item.Tag is Vibershot.Media.FrameRate tag && tag == wanted) { FrameRateComboBox.SelectedItem = item; return; }
        }
        // A custom rate from a hand-edited config stays available as its own entry.
        var custom = new ComboBoxItem { Content = $"{wanted} fps", Tag = wanted };
        FrameRateComboBox.Items.Add(custom);
        FrameRateComboBox.SelectedItem = custom;
    }

    // ---------------------------------------------------------------- General

    private void LoadGeneralPage()
    {
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
        AskActionsCheckbox.IsChecked = _config.ShowCaptureActions;
        ConfigPathText.Text = SettingsHelpers.ConfigPath;
        ConfigPathText.ToolTip = SettingsHelpers.ConfigPath;
        UpdateSwitch.IsChecked = _config.Updates.CheckAutomatically;
        VersionText.Text = "Vibershot " + AppVersion.Current;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) await app.CheckForUpdatesAsync(this, manual: true);
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e) =>
        SettingsHelpers.OpenFolder(Path.GetDirectoryName(SettingsHelpers.ConfigPath) ?? SettingsHelpers.ConfigPath);

    // ---------------------------------------------------------------- Save / cancel

    /// <summary>Selects the combo item whose Tag equals <paramref name="value"/>.</summary>
    private static void SelectTag<T>(ComboBox combo, T value) where T : struct
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is T tag && tag.Equals(value)) { combo.SelectedItem = item; return; }
        }
    }

    private bool Fail(SettingsPage page, string message)
    {
        Page = page;
        MessageBox.Show(this, message, "Vibershot settings");
        return false;
    }

    private static bool TryDays(TextBox box, out int days) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out days) && days >= 1 && days <= 3650;

    /// <summary>Checks every page; the first problem switches to its page and explains.</summary>
    private bool Validate()
    {
        if (!SettingsHelpers.IsAbsolutePath(_libraryRoot)) return Fail(SettingsPage.Gallery, "Choose an absolute folder for the library.");
        foreach (var destination in _destinations)
            if (!SettingsHelpers.IsAbsolutePath(destination.Path)) return Fail(SettingsPage.AfterCapture, $"'{destination.Path}' is not an absolute folder path.");
        if (EditorSettings.ParseArgb(SelectedCanvasBackground) is null) return Fail(SettingsPage.Editor, "Enter the canvas colour as #RRGGBB or #AARRGGBB.");
        if (EditorSettings.ParseArgb(StrokeColorTextBox.Text) is null) return Fail(SettingsPage.Editor, "Enter the stroke colour as #RRGGBB or #AARRGGBB.");
        if (string.IsNullOrWhiteSpace(FontFamilyTextBox.Text)) return Fail(SettingsPage.Editor, "Enter a font family for new text.");
        if (!double.TryParse(FontSizeTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize) || fontSize < 4 || fontSize > 400)
            return Fail(SettingsPage.Editor, "Enter a font size between 4 and 400.");
        if (!TryDays(CleanShotsDays, out _) || !TryDays(CleanVideosDays, out _) || !TryDays(CleanRecoveryDays, out _))
            return Fail(SettingsPage.Gallery, "Clean-up ages must be whole numbers of days from 1 to 3650.");
        foreach (var action in HotkeySettings.Actions)
        {
            var gesture = PendingGesture(action);
            if (!gesture.IsEmpty && !gesture.IsValid) return Fail(SettingsPage.Hotkeys, $"The shortcut for {HotkeySettings.Label(action)} needs a modifier key.");
        }
        return true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        if (!Validate()) return;

        _config.ShowCaptureActions = AskActionsCheckbox.IsChecked == true;
        _config.PrintScreen = PrintScreenSwitch.IsChecked != true ? PrintScreenAction.Off
            : PsDesktopRadio.IsChecked == true ? PrintScreenAction.FullDesktop
            : PrintScreenAction.CaptureTool;

        var after = CurrentAfterCapture();
        after.Destinations = _destinations.Select(d => new SaveDestination { Path = d.Path, Enabled = d.Enabled }).ToList();
        _config.AfterCapture = after;

        foreach (var action in HotkeySettings.Actions) _config.Hotkeys[action] = _hotkeys[action];

        var gallery = _config.Gallery;
        var oldRoot = gallery.LibraryRoot;
        bool rootChanged = !AfterCaptureSettings.SameFolder(oldRoot, _libraryRoot);
        gallery.LibraryRoot = _libraryRoot;
        gallery.ExtraFolders = new List<string>(_extraFolders);
        gallery.ThumbnailSize = (int)ThumbnailSizeSlider.Value;
        gallery.Cleanup.Enabled = CleanupSwitch.IsChecked == true;
        gallery.Cleanup.Screenshots = CleanShotsCheckbox.IsChecked == true;
        gallery.Cleanup.Recordings = CleanVideosCheckbox.IsChecked == true;
        gallery.Cleanup.Recovery = CleanRecoveryCheckbox.IsChecked == true;
        if (TryDays(CleanShotsDays, out var days)) gallery.Cleanup.ScreenshotDays = days;
        if (TryDays(CleanVideosDays, out days)) gallery.Cleanup.RecordingDays = days;
        if (TryDays(CleanRecoveryDays, out days)) gallery.Cleanup.RecoveryDays = days;

        _config.Editor.CanvasBackground = SelectedCanvasBackground;
        _config.Editor.DefaultStrokeColor = StrokeColorTextBox.Text.Trim();
        _config.Editor.DefaultStrokeWidth = (int)StrokeWidthSlider.Value;
        _config.Editor.DefaultFontFamily = FontFamilyTextBox.Text.Trim();
        _config.Editor.DefaultFontSize = double.Parse(FontSizeTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

        _config.Video.Format = SelectedVideoFormat;
        _config.Video.FrameRate = SelectedFrameRate.ToString();
        _config.Video.Quality = VideoSettings.ClampQuality((int)VideoQualitySlider.Value);
        _config.Video.PreferHardwareEncoder = HardwareEncoderCheckbox.IsChecked != false;
        _config.Video.ShowCursor = CursorCheckbox.IsChecked != false;
        _config.Updates.CheckAutomatically = UpdateSwitch.IsChecked == true;

        if (rootChanged && Directory.Exists(oldRoot))
        {
            var failures = MoveLibrary(oldRoot, _libraryRoot);
            if (failures.Count > 0)
                MessageBox.Show(this, $"{failures.Count} item(s) could not be moved to the new library and stay in {oldRoot}:\n\n{string.Join("\n", failures.Take(8))}", "Vibershot settings");
        }

        try { _config.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings: " + ex.Message, "Vibershot settings");
            return;
        }
        try { StartupRegistration.SetEnabled(StartupSwitch.IsChecked == true); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not update Start with Windows: " + ex.Message, "Vibershot settings");
        }
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        DialogResult = false;
        Close();
    }
}
