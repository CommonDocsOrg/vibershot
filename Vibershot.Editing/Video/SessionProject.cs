using System.Collections.Immutable;
using Vibershot.Media;

namespace Vibershot.Editing.Video;

/// <summary>
/// Turns a finished recording session into a video editor project: the screen on the bottom
/// layer, the camera above it where its bubble sat, and each sound on its own lane. Nothing is
/// cut or re-encoded; every clip simply starts as far into its file as that file started before
/// the screen recording did, which is what lines the tracks up.
/// </summary>
public static class SessionProject
{
    public const string ProjectFileName = "recording" + VideoProjectFile.Extension;

    /// <summary>
    /// Builds the project. The sources are the session's files as probed by the caller (this
    /// function touches no files); a null source leaves that track out.
    /// </summary>
    public static VideoProject Build(SessionManifest manifest, MediaSource screen, MediaSource? camera = null, MediaSource? systemAudio = null, MediaSource? microphone = null)
    {
        int width = screen.Width > 0 ? screen.Width : Math.Max(2, manifest.Width), height = screen.Height > 0 ? screen.Height : Math.Max(2, manifest.Height);
        double fit = Math.Min(1, (double)VideoProject.MaxDimension / Math.Max(width, height));
        var project = new VideoProject
        {
            Width = FrameReader.Even((int)Math.Round(width * fit)),
            Height = FrameReader.Even((int)Math.Round(height * fit)),
            FrameRate = FrameRate.TryParse(manifest.FrameRate, out _) ? manifest.FrameRate : screen.FrameRate ?? "30",
        };
        var sources = ImmutableList.Create(screen);
        var screenTrack = new Track { Name = "Screen", Kind = TrackKind.Visual };
        var cameraTrack = new Track { Name = "Camera", Kind = TrackKind.Visual };
        var microphoneTrack = new Track { Name = "Microphone", Kind = TrackKind.Audio };
        var systemTrack = new Track { Name = "System audio", Kind = TrackKind.Audio };
        if (camera != null && manifest.Camera != null) sources = sources.Add(camera); else camera = null;
        if (microphone != null && manifest.Microphone != null) sources = sources.Add(microphone); else microphone = null;
        if (systemAudio != null && manifest.SystemAudio != null) sources = sources.Add(systemAudio); else systemAudio = null;

        // A recording made without stretch timing is one stretch whose companions start with it.
        var stretches = manifest.Stretches.Count > 0 ? manifest.Stretches : new[] { new ScreenStretch(0, double.NaN, screen.Duration) };
        Placement? cameraPlacement = camera == null ? null : CameraPlacement(project, manifest, camera);
        ClipFrame? cameraFrame = manifest.CameraBox?.Shape switch
        {
            "Rectangle" => null,
            "Rounded" => new ClipFrame { Shape = FrameShape.Rounded },
            _ => new ClipFrame { Shape = FrameShape.Ellipse },
        };
        long totalFrames = Math.Max(1, project.Frames(screen.Duration));
        long cursor = 0;
        foreach (var stretch in stretches)
        {
            long start = Math.Max(cursor, project.Frames(stretch.FileStart));
            long duration = Math.Min(project.Frames(stretch.Duration), totalFrames - start);
            if (duration < 1) continue;
            cursor = start + duration;
            bool companions = camera != null || microphone != null || systemAudio != null;
            Guid? link = companions ? Guid.NewGuid() : null;
            screenTrack = screenTrack with { Clips = screenTrack.Clips.Add(new VideoClip { SourceId = screen.Id, Start = start, Duration = duration, SourceIn = project.Seconds(start), LinkId = link }) };

            if (camera != null && Align(project, camera, manifest.Camera!, stretch, start, duration) is var (cameraStart, cameraLength, cameraIn))
                cameraTrack = cameraTrack with { Clips = cameraTrack.Clips.Add(new VideoClip { SourceId = camera.Id, Start = cameraStart, Duration = cameraLength, SourceIn = cameraIn, LinkId = link, Placement = cameraPlacement, Frame = cameraFrame }) };
            if (microphone != null && Align(project, microphone, manifest.Microphone!, stretch, start, duration) is var (micStart, micLength, micIn))
                microphoneTrack = microphoneTrack with { Clips = microphoneTrack.Clips.Add(new AudioClip { SourceId = microphone.Id, Start = micStart, Duration = micLength, SourceIn = micIn, LinkId = link }) };
            if (systemAudio != null && Align(project, systemAudio, manifest.SystemAudio!, stretch, start, duration) is var (systemStart, systemLength, systemIn))
                systemTrack = systemTrack with { Clips = systemTrack.Clips.Add(new AudioClip { SourceId = systemAudio.Id, Start = systemStart, Duration = systemLength, SourceIn = systemIn, LinkId = link }) };
        }

        // Visual tracks are listed bottom layer first, so the camera goes after the screen.
        var tracks = ImmutableList.Create(screenTrack);
        if (camera != null) tracks = tracks.Add(cameraTrack);
        if (microphone != null) tracks = tracks.Add(microphoneTrack);
        if (systemAudio != null) tracks = tracks.Add(systemTrack);
        if (microphone == null && systemAudio == null) tracks = tracks.Add(new Track { Name = "A1", Kind = TrackKind.Audio });
        return project with { Sources = sources, Tracks = tracks };
    }

    /// <summary>
    /// Where a companion file's clip goes for one stretch: it starts as far into its file as the
    /// stretch began after the file did. A file that began late starts its clip late instead; a
    /// file that ends early ends its clip early. Null when nothing of it falls inside the stretch.
    /// </summary>
    private static (long Start, long Duration, double SourceIn)? Align(VideoProject project, MediaSource source, SessionTrack track, ScreenStretch stretch, long start, long duration)
    {
        double sourceIn = double.IsNaN(stretch.WallStart) ? 0 : stretch.WallStart - track.Start;
        if (sourceIn < 0)
        {
            long late = project.Frames(-sourceIn);
            start += late; duration -= late; sourceIn = 0;
        }
        long available = (long)Math.Floor((source.Duration - sourceIn) * project.Rate.Value + 1e-6);
        duration = Math.Min(duration, available);
        return duration < 1 ? null : (start, duration, sourceIn);
    }

    /// <summary>
    /// The camera's box on the canvas: where the bubble sat over the recorded region, or the
    /// bottom right corner when it sat elsewhere. The picture is cropped evenly to fill the box.
    /// </summary>
    public static Placement CameraPlacement(VideoProject project, SessionManifest manifest, MediaSource camera)
    {
        double scaleX = manifest.Width > 0 ? (double)project.Width / manifest.Width : 1, scaleY = manifest.Height > 0 ? (double)project.Height / manifest.Height : 1;
        double size = Math.Round(Math.Min(project.Width, project.Height) * 0.26), margin = Math.Round(Math.Min(project.Width, project.Height) * 0.04);
        var box = new System.Windows.Rect(project.Width - size - margin, project.Height - size - margin, size, size);
        if (manifest.CameraBox is { Width: > 1, Height: > 1 } bubble)
        {
            var asked = new System.Windows.Rect(bubble.X * scaleX, bubble.Y * scaleY, bubble.Width * scaleX, bubble.Height * scaleY);
            var canvas = new System.Windows.Rect(0, 0, project.Width, project.Height);
            var seen = System.Windows.Rect.Intersect(asked, canvas);
            // Mostly over the recorded region: keep it there. Otherwise keep its size and tuck it into the corner.
            if (!seen.IsEmpty && seen.Width * seen.Height >= asked.Width * asked.Height * 0.5) box = asked;
            else if (asked.Width <= project.Width && asked.Height <= project.Height) box = new System.Windows.Rect(project.Width - asked.Width - margin, project.Height - asked.Height - margin, asked.Width, asked.Height);
        }
        double sourceAspect = camera.Width > 0 && camera.Height > 0 ? (double)camera.Width / camera.Height : 16.0 / 9, boxAspect = box.Width / box.Height;
        double cropX = sourceAspect > boxAspect ? (1 - boxAspect / sourceAspect) / 2 : 0, cropY = sourceAspect < boxAspect ? (1 - sourceAspect / boxAspect) / 2 : 0;
        return new Placement
        {
            X = Math.Round(box.X), Y = Math.Round(box.Y), Width = Math.Round(box.Width), Height = Math.Round(box.Height),
            CropLeft = cropX, CropRight = cropX, CropTop = cropY, CropBottom = cropY,
            FlipH = manifest.CameraBox?.Mirror ?? false,
        };
    }
}
