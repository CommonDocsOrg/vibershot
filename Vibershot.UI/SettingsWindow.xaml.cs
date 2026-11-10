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
        SelectFormat(config.Format);
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
        _config.Format = (FormatComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag is ImageFormat fmt
            ? fmt
            : ImageFormat.Png;

        try { _config.Save(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "Could not save settings: " + ex.Message, "Vibershot settings");
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
