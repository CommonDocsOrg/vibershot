using System.Collections.Generic;
using Vibershot.Core;

namespace Vibershot.UI;

/// <summary>
/// The first-run page: where the library lives and whether Print Screen is taken over. Closing
/// it any way marks setup complete so it never shows again; the primary button also applies the
/// two choices and returns true so the app re-applies its settings.
/// </summary>
public partial class FirstRunWindow : Window
{
    private readonly AppConfig _config;
    private bool _finished;

    public FirstRunWindow(AppConfig config)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _config = config;
        LoadFromConfig();
        if (config.InstalledByInstaller)
        {
            // The installer already asked where the library goes and whether to start with Windows.
            LibraryCard.Visibility = Visibility.Collapsed;
            StartupPanel.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "One quick choice. Everything can be changed later in Settings.";
            Height = 520;
        }
    }

    private void LoadFromConfig()
    {
        var defaultRoot = GallerySettings.DefaultLibraryRoot();
        DefaultPathText.Text = defaultRoot;
        var hint = "Next to the Windows Screenshots folder.";
        if (SettingsHelpers.IsInOneDrive(defaultRoot)) hint += " " + SettingsHelpers.OneDriveSentence;
        PicturesHint.Text = hint;

        bool usesDefault = AfterCaptureSettings.SameFolder(_config.Gallery.LibraryRoot, defaultRoot);
        PicturesRadio.IsChecked = usesDefault;
        ElsewhereRadio.IsChecked = !usesDefault;
        if (!usesDefault) CustomPathBox.Text = _config.Gallery.LibraryRoot;
        Library_Changed(this, new RoutedEventArgs());

        PsYesRadio.IsChecked = _config.PrintScreen != PrintScreenAction.Off;
        PsNoRadio.IsChecked = _config.PrintScreen == PrintScreenAction.Off;
        HotkeyCaps.Content = Keycaps.Build(_config.Hotkeys.Gesture(HotkeyAction.CaptureRegion));
        SettingsHelpers.RefreshPrintScreenWarning(PrintScreenWarningHost);

        AfterCaptureText.Text = DescribeAfterCapture(_config.AfterCapture);
    }

    /// <summary>One sentence for the after-capture card; the brief's wording for the defaults, a short list otherwise.</summary>
    private static string DescribeAfterCapture(AfterCaptureSettings ac)
    {
        if (ac.SaveAsFile && !ac.AskWhereToSave && ac.SaveToLibrary && ac.Format == ImageFormat.Vibeshot && ac.CopyToClipboard && !ac.OpenGallery && !ac.OpenEditor && ac.ShowPreview)
            return "Saved to your library as an editable Vibershot project and copied to the clipboard, with a small preview.";
        var parts = new List<string>();
        if (ac.SaveAsFile) parts.Add(ac.AskWhereToSave ? "you are asked where to save it" : "saved" + (ac.SaveToLibrary ? " to your library" : " to your folders"));
        if (ac.CopyToClipboard) parts.Add("copied to the clipboard");
        if (ac.OpenGallery) parts.Add("shown in the gallery");
        if (ac.OpenEditor) parts.Add("opened in the editor");
        if (parts.Count == 0) return ac.ShowPreview ? "Only shown in the small preview; nothing is kept." : "Nothing is kept or shown.";
        var text = string.Join(", ", parts);
        text = char.ToUpperInvariant(text[0]) + text[1..];
        if (!ac.OpenGallery && !ac.OpenEditor && ac.ShowPreview) text += ", with a small preview";
        return text + ".";
    }

    private void Library_Changed(object sender, RoutedEventArgs e)
    {
        if (CustomPathPanel is not null) CustomPathPanel.IsEnabled = ElsewhereRadio.IsChecked == true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folder = SettingsHelpers.PickFolder("Choose where the Vibershot library lives", CustomPathBox.Text);
        if (folder is null) return;
        CustomPathBox.Text = folder;
        ElsewhereRadio.IsChecked = true;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_config, SettingsPage.AfterCapture) { Owner = this }.ShowDialog();
        // Settings may have moved the library or changed Print Screen; show what is now saved.
        LoadFromConfig();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!_config.InstalledByInstaller)
        {
            var root = ElsewhereRadio.IsChecked == true ? CustomPathBox.Text.Trim() : GallerySettings.DefaultLibraryRoot();
            if (!SettingsHelpers.IsAbsolutePath(root))
            {
                MessageBox.Show(this, "Choose an absolute folder for the library, or pick Pictures.", "Welcome to Vibershot");
                return;
            }
            _config.Gallery.LibraryRoot = root;
        }
        if (PsNoRadio.IsChecked == true) _config.PrintScreen = PrintScreenAction.Off;
        else if (_config.PrintScreen == PrintScreenAction.Off) _config.PrintScreen = PrintScreenAction.CaptureTool;

        Finish();
        if (!_config.InstalledByInstaller && StartupCheckbox.IsChecked == true)
        {
            try { StartupRegistration.SetEnabled(true); } catch (Exception) { }
        }
        DialogResult = true;
        Close();
    }

    /// <summary>Marks setup done and saves; closing with the X counts as accepting the defaults.</summary>
    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _config.SetupCompleted = true;
        try { _config.Save(); } catch (Exception) { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Finish();
        base.OnClosing(e);
    }
}
