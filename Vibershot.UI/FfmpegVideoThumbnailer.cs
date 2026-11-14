using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Gallery;
using Vibershot.Media;

namespace Vibershot.UI;

/// <summary>
/// Gallery previews for videos: the frame Explorer shows when Windows has a thumbnail handler for
/// the format, otherwise a frame decoded by the bundled ffmpeg (MKV and WebM on a stock Windows),
/// and the film tile when neither can read the file.
/// </summary>
public sealed class FfmpegVideoThumbnailer : IVideoThumbnailer
{
    private readonly FilmTileThumbnailer _fallback = new();
    private readonly Lazy<FfmpegRunner?> _runner = new(() =>
    {
        try { return new FfmpegRunner(); }
        catch (FfmpegNotFoundException) { return null; }
    });

    public BitmapSource? CreateThumbnail(string path, int longestEdge) =>
        ShellVideoThumbnailer.FromShell(path, longestEdge) ?? FromFfmpeg(path, longestEdge) ?? _fallback.CreateThumbnail(path, longestEdge);

    /// <summary>One frame from a second in (or the middle of a shorter clip), scaled to fit the edge. Null when ffmpeg is missing or cannot decode the file.</summary>
    public BitmapSource? FromFfmpeg(string path, int longestEdge)
    {
        if (_runner.Value is not { } runner) return null;
        try
        {
            var info = Ffprobe.ProbeAsync(path).GetAwaiter().GetResult();
            if (!info.HasVideo || info.Width <= 0 || info.Height <= 0) return null;
            bool sideways = info.Rotation is 90 or 270;
            var (width, height) = FrameReader.Fit(sideways ? info.Height : info.Width, sideways ? info.Width : info.Height, longestEdge, longestEdge);
            using var reader = new FrameReader(runner, new FrameReaderOptions
            {
                Path = path, Start = Math.Min(1, info.Duration.TotalSeconds / 2), Width = width, Height = height, FrameRate = info.FrameRate ?? FrameRate.Fps30, MaxFrames = 1,
            });
            var pixels = new byte[reader.FrameBytes];
            if (!reader.ReadFrameAsync(pixels).AsTask().GetAwaiter().GetResult()) return null;
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception) { return null; }
    }
}
