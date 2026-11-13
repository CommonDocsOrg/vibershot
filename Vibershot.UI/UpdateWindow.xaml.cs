using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Vibershot.Core;

namespace Vibershot.UI;

/// <summary>
/// Offers a newer release: shows its notes, downloads the installer with a progress bar, verifies
/// it and hands over to the silent installer, which closes and relaunches the app. "Later" asks
/// again at the next daily check; "Skip this version" silences that one version.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly AppConfig _config;
    private readonly UpdateManifest _manifest;
    private readonly CancellationTokenSource _cancel = new();
    private bool _installing;

    public UpdateWindow(AppConfig config, UpdateManifest manifest)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _config = config;
        _manifest = manifest;
        string size = manifest.Size > 0 ? $" ({manifest.Size / 1024.0 / 1024.0:0.#} MB)" : "";
        VersionText.Text = $"Vibershot {AppVersion.Normalize(manifest.Version)} is available{size}. You have {AppVersion.Current}.";
        NotesText.Text = string.IsNullOrWhiteSpace(manifest.Notes) ? "No release notes were published for this version." : manifest.Notes.Trim();
        ReleaseLinkText.Visibility = string.IsNullOrWhiteSpace(manifest.ReleaseUrl) ? Visibility.Collapsed : Visibility.Visible;
        if (UpdateService.NeedsManualInstall(manifest, AppVersion.Current))
        {
            InstallButton.Content = "Download page";
            ShowError("This version cannot upgrade your copy in place. Download the installer from the release page and run it.");
        }
    }

    private void ReleaseLink_Click(object sender, RoutedEventArgs e) => OpenUrl(_manifest.ReleaseUrl);

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _config.Updates.SkippedVersion = AppVersion.Normalize(_manifest.Version);
        try { _config.Save(); } catch (Exception) { }
        DialogResult = false;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.NeedsManualInstall(_manifest, AppVersion.Current))
        {
            OpenUrl(_manifest.ReleaseUrl ?? _manifest.InstallerUrl);
            return;
        }
        if (_installing) return;
        _installing = true;
        InstallButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        ErrorHost.Content = null;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Downloading the installer…";
        ProgressBar.IsIndeterminate = true;
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                if (p.Total <= 0) return;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = Math.Min(100, p.Done * 100.0 / p.Total);
                ProgressText.Text = $"Downloading the installer… {p.Done / 1024.0 / 1024.0:0.#} of {p.Total / 1024.0 / 1024.0:0.#} MB";
            });
            string installer = await UpdateService.DownloadAsync(_manifest, progress, _cancel.Token);
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            ProgressText.Text = "Checksum verified. Vibershot will close, update and reopen in a moment.";
            await Task.Delay(600);
            UpdateService.Install(installer);
            // The installer waits for this process to end before it replaces the files.
            DialogResult = true;
            Close();
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ShowError(ex.Message);
            InstallButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            _installing = false;
        }
    }

    private void ShowError(string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = SettingsHelpers.Brush("WarningInk") };
        var box = new Border { Child = text };
        box.SetResourceReference(StyleProperty, "WarningBox");
        ErrorHost.Content = box;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel();
        base.OnClosed(e);
    }
}
