using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SkiaSharp;
using Vibershot.Core;

namespace Vibershot.UI;

/// <summary>
/// Export dialog for the editor: picks a flat format and quality, previews the decoded result
/// (so JPEG and lossy WebP show their real artefacts) with its byte size, then writes the file.
/// Encoding runs on a worker and is debounced; the source bitmap is frozen so it is safe there.
/// </summary>
public partial class ExportImageWindow : Window
{
    private const int DebounceMs = 150;
    private static readonly ImageFormat[] Formats = { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Webp, ImageFormat.WebpLossless };
    /// <summary>Remembered for the session only.</summary>
    private static bool _openFolderAfterExport;

    private readonly BitmapSource _source;
    private readonly string _stem;
    private readonly string? _folder;
    private CancellationTokenSource? _job;
    private byte[]? _encoded;
    private ImageFormat _encodedFormat;
    private int _encodedQuality;
    private bool _loaded;

    /// <summary>Where the file was written, or null when the dialog was cancelled.</summary>
    public string? ExportedPath { get; private set; }
    /// <summary>The format chosen in the dialog.</summary>
    public ImageFormat Format { get; private set; }
    private int Quality => (int)Math.Round(QualitySlider.Value);
    private static bool HasQuality(ImageFormat format) => format is ImageFormat.Jpeg or ImageFormat.Webp;

    /// <param name="flattened">Frozen render of the document; previewed as-is for lossless formats.</param>
    /// <param name="initialFolder">Where the save dialog starts; falls back to the OS default when null or missing.</param>
    public ExportImageWindow(BitmapSource flattened, ImageFormat initialFormat, int initialQuality, string suggestedStem, string? initialFolder)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _source = flattened;
        _stem = suggestedStem;
        _folder = initialFolder;
        Format = ImageEncoding.IsFlat(initialFormat) ? initialFormat : ImageFormat.Png;
        foreach (var format in Formats) FormatCombo.Items.Add(new ComboBoxItem { Content = ImageEncoding.Describe(format), Tag = format });
        FormatCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, Format));
        QualitySlider.Value = ImageEncoding.ClampWebpQuality(initialQuality);
        QualityValue.Text = Quality.ToString();
        OpenFolderCheck.IsChecked = _openFolderAfterExport;
        DimensionsValue.Text = $"{flattened.PixelWidth} × {flattened.PixelHeight} px";
        PreviewImage.Source = flattened;
        Closed += (_, _) => _job?.Cancel();
        Loaded += (_, _) => { _loaded = true; UpdateQualityPanel(); Schedule(); };
    }

    /// <summary>Short format name for messages: PNG, JPEG or WebP.</summary>
    public static string ShortName(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Webp or ImageFormat.WebpLossless => "WebP",
        _ => "PNG",
    };

    private void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FormatCombo.SelectedItem is ComboBoxItem { Tag: ImageFormat format }) Format = format;
        if (!_loaded) return;
        UpdateQualityPanel();
        Schedule();
    }

    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        QualityValue.Text = Quality.ToString();
        if (_loaded) Schedule();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _openFolderAfterExport = OpenFolderCheck.IsChecked == true;

    private void UpdateQualityPanel() => QualityPanel.Visibility = HasQuality(Format) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Debounced re-encode: cancels the previous job, waits briefly, encodes and decodes off the UI thread, then swaps the preview in.</summary>
    private async void Schedule()
    {
        _job?.Cancel();
        var job = _job = new CancellationTokenSource();
        var token = job.Token;
        var format = Format;
        int quality = Quality;
        SizeValue.Text = "Encoding…";
        try
        {
            await Task.Delay(DebounceMs, token);
            var bytes = await Task.Run(() => Encode(_source, format, quality), token);
            token.ThrowIfCancellationRequested();
            var preview = HasQuality(format) ? await Task.Run(() => Decode(bytes, format), token) : _source;
            if (token.IsCancellationRequested) return;
            _encoded = bytes; _encodedFormat = format; _encodedQuality = quality;
            PreviewImage.Source = preview;
            SizeValue.Text = FormatSize(bytes.Length);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _encoded = null;
            SizeValue.Text = "Could not encode";
            ToolTip = ex.Message;
        }
    }

    /// <summary>Encodes to memory; JPEG goes through WPF's encoder here because the shared encoder has no JPEG quality knob.</summary>
    private static byte[] Encode(BitmapSource image, ImageFormat format, int quality)
    {
        using var stream = new MemoryStream();
        if (format == ImageFormat.Jpeg)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
        }
        else ImageEncoding.Encode(image, format, quality, stream);
        return stream.ToArray();
    }

    /// <summary>Decodes an encoded image back to a frozen bitmap: Skia for WebP, WPF for the rest.</summary>
    private static BitmapSource Decode(byte[] bytes, ImageFormat format)
    {
        if (format is ImageFormat.Webp or ImageFormat.WebpLossless)
        {
            var bounds = SKBitmap.DecodeBounds(bytes);
            using var decoded = SKBitmap.Decode(bytes, new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("WebP decoding failed.");
            var result = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Pbgra32, null, decoded.Bytes, decoded.RowBytes);
            result.Freeze();
            return result;
        }
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var format = Format;
        int quality = Quality;
        string ext = ImageEncoding.Extension(format);
        var dialog = new SaveFileDialog
        {
            Filter = $"{ShortName(format)} image|*.{ext}|All files|*.*", DefaultExt = ext, AddExtension = true,
            FileName = $"{_stem}.{ext}", InitialDirectory = _folder != null && Directory.Exists(_folder) ? _folder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Reuse the preview's bytes when they match; otherwise encode now so the file never lags the settings.
            var bytes = _encoded != null && _encodedFormat == format && (_encodedQuality == quality || !HasQuality(format)) ? _encoded : Encode(_source, format, quality);
            File.WriteAllBytes(dialog.FileName, bytes);
            ExportedPath = dialog.FileName;
            if (OpenFolderCheck.IsChecked == true)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = false }); } catch (Exception) { }
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SizeValue.Text = "Export failed";
            ToolTip = ex.Message;
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
