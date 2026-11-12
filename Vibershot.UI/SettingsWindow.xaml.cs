using System.Windows;
using System.Windows.Forms;
using Vibershot.Core;

namespace Vibershot.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _config = config;

        FolderTextBox.Text = config.OutputFolder;
        AutosaveCheckbox.IsChecked = config.Autosave;
        ActionsCheckbox.IsChecked = config.ShowCaptureActions;
        SaveDialogCheckbox.IsChecked = config.ShowSaveDialog;
        ClipboardCheckbox.IsChecked = config.CopyToClipboard;
        WebpQualitySlider.Value = ImageEncoding.ClampWebpQuality(config.WebpQuality);
        SelectFormat(config.Format);
        UpdateWebpQualityVisibility();

        foreach (var rate in Vibershot.Media.FrameRate.Presets)
        {
            FrameRateComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"{rate} fps", Tag = rate });
        }
        SelectVideoFormat(config.Video.Format);
        SelectFrameRate(config.Video.FrameRate);
        VideoQualitySlider.Value = VideoSettings.ClampQuality(config.Video.Quality);
        HardwareEncoderCheckbox.IsChecked = config.Video.PreferHardwareEncoder;
        CursorCheckbox.IsChecked = config.Video.ShowCursor;
        UpdateMp4OptionsVisibility();

        StartupCheckbox.IsChecked = StartupRegistration.IsEnabled;
        PrintScreenCheckbox.IsChecked = config.PrintScreen != PrintScreenAction.Off;
        PrintScreenDesktopRadio.IsChecked = config.PrintScreen == PrintScreenAction.FullDesktop;
        PrintScreenToolRadio.IsChecked = !PrintScreenDesktopRadio.IsChecked;
        PrintScreenCheckbox_Changed(this, new RoutedEventArgs());
    }

    private void PrintScreenCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (PrintScreenOptions is not null)
        {
            PrintScreenOptions.IsEnabled = PrintScreenCheckbox.IsChecked == true;
        }
    }

    private ImageFormat SelectedFormat =>
        (FormatComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag is ImageFormat fmt ? fmt : ImageFormat.Png;

    private void FormatComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateWebpQualityVisibility();

    private void UpdateWebpQualityVisibility()
    {
        if (WebpQualityPanel is not null)
        {
            WebpQualityPanel.Visibility = SelectedFormat == ImageFormat.Webp ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void WebpQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WebpQualityLabel is not null)
        {
            WebpQualityLabel.Text = $"{(int)e.NewValue}%";
        }
    }

    private void SelectFormat(ImageFormat format)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in FormatComboBox.Items)
        {
            if (item.Tag is ImageFormat tag && tag == format)
            {
                FormatComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private VideoFormat SelectedVideoFormat =>
        (VideoFormatComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag is VideoFormat fmt ? fmt : VideoFormat.Mp4;

    private Vibershot.Media.FrameRate SelectedFrameRate =>
        (FrameRateComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag is Vibershot.Media.FrameRate rate ? rate : Vibershot.Media.FrameRate.Fps30;

    private void VideoFormatComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateMp4OptionsVisibility();

    private void UpdateMp4OptionsVisibility()
    {
        if (Mp4OptionsPanel is not null)
        {
            Mp4OptionsPanel.Visibility = SelectedVideoFormat == VideoFormat.Mp4 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void VideoQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VideoQualityLabel is not null)
        {
            VideoQualityLabel.Text = $"{(int)e.NewValue}%";
        }
    }

    private void SelectVideoFormat(VideoFormat format)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in VideoFormatComboBox.Items)
        {
            if (item.Tag is VideoFormat tag && tag == format)
            {
                VideoFormatComboBox.SelectedItem = item;
                return;
            }
        }
        VideoFormatComboBox.SelectedIndex = 0;
    }

    private void SelectFrameRate(string setting)
    {
        var wanted = Vibershot.Media.FrameRate.TryParse(setting, out var parsed) ? parsed : Vibershot.Media.FrameRate.Fps30;
        foreach (System.Windows.Controls.ComboBoxItem item in FrameRateComboBox.Items)
        {
            if (item.Tag is Vibershot.Media.FrameRate tag && tag == wanted)
            {
                FrameRateComboBox.SelectedItem = item;
                return;
            }
        }
        // A custom rate from a hand-edited config stays available as its own entry.
        var custom = new System.Windows.Controls.ComboBoxItem { Content = $"{wanted} fps", Tag = wanted };
        FrameRateComboBox.Items.Add(custom);
        FrameRateComboBox.SelectedItem = custom;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FolderBrowserDialog
        {
            Description = "Select screenshot output folder",
            SelectedPath = _config.OutputFolder,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            FolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderTextBox.Text) || !Path.IsPathFullyQualified(FolderTextBox.Text))
        {
            System.Windows.MessageBox.Show(this, "Choose an absolute output folder path.", "Vibershot settings");
            return;
        }
        _config.OutputFolder = FolderTextBox.Text;
        _config.Autosave = AutosaveCheckbox.IsChecked ?? true;
        _config.ShowCaptureActions = ActionsCheckbox.IsChecked == true;
        _config.ShowSaveDialog = SaveDialogCheckbox.IsChecked == true;
        _config.CopyToClipboard = ClipboardCheckbox.IsChecked ?? true;
        _config.Format = SelectedFormat;
        _config.WebpQuality = ImageEncoding.ClampWebpQuality((int)WebpQualitySlider.Value);
        _config.Video.Format = SelectedVideoFormat;
        _config.Video.FrameRate = SelectedFrameRate.ToString();
        _config.Video.Quality = VideoSettings.ClampQuality((int)VideoQualitySlider.Value);
        _config.Video.PreferHardwareEncoder = HardwareEncoderCheckbox.IsChecked != false;
        _config.Video.ShowCursor = CursorCheckbox.IsChecked != false;
        _config.PrintScreen = PrintScreenCheckbox.IsChecked != true ? PrintScreenAction.Off
            : PrintScreenDesktopRadio.IsChecked == true ? PrintScreenAction.FullDesktop
            : PrintScreenAction.CaptureTool;

        try { _config.Save(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "Could not save settings: " + ex.Message, "Vibershot settings");
            return;
        }
        try { StartupRegistration.SetEnabled(StartupCheckbox.IsChecked == true); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "Could not update Start with Windows: " + ex.Message, "Vibershot settings");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
