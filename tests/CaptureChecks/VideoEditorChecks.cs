using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vibershot.Editing.Video;
using Vibershot.Media;
using Vibershot.UI;
using System.Windows;
using System.Windows.Input;
using Vibershot.Editing;

/// <summary>Checks for the video editor: timeline edits, the project file, ffmpeg arguments and a real layered export.</summary>
static class VideoEditorChecks
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
    static void Same(string[] expected, string[] actual, string what)
    {
        if (!expected.SequenceEqual(actual)) throw new Exception($"FAIL: {what}\n expected: {string.Join(" ", expected)}\n actual:   {string.Join(" ", actual)}");
    }

    static MediaSource Source(string path, double seconds, bool audio = true, int width = 1920, int height = 1080) =>
        new() { Path = path, Duration = seconds, Width = width, Height = height, FrameRate = "30", HasAudio = audio };

    public static void Run()
    {
        // A project takes its canvas and rate from the first clip; 8K is capped at 3840 wide.
        var a = Source(@"C:\media\a.mp4", 10);
        var project = TimelineOps.ForSource(a);
        Check(project.Width == 1920 && project.Height == 1080 && project.FrameRate == "30" && project.Tracks.Count == 2, "project sized from its first clip");
        Check(TimelineOps.ForSource(Source(@"C:\big.mp4", 1, false, 7680, 4320)).Width == 3840, "canvas capped at 3840");
        var v1 = project.Tracks[0]; var a1 = project.Tracks[1];
        var video = v1.Clips[0]; var sound = a1.Clips[0];
        Check(v1.Kind == TrackKind.Visual && a1.Kind == TrackKind.Audio && video is VideoClip && sound is AudioClip, "picture and sound land on their own tracks");
        Check(video.Duration == 300 && video.Start == 0 && video.LinkId != null && video.LinkId == sound.LinkId && project.Duration == 300, "ten seconds is 300 frames, linked");
        Check(Math.Abs(project.Seconds(45) - 1.5) < 1e-9 && project.Frames(1.5) == 45, "frame and second conversion");

        // Appending goes to the end of V1; a silent clip brings no audio clip.
        var b = Source(@"C:\media\b.mp4", 4, audio: false);
        project = TimelineOps.Append(project, b);
        Check(project.Tracks[0].Clips.Count == 2 && project.Tracks[0].Clips[1].Start == 300 && project.Tracks[1].Clips.Count == 1 && project.Sources.Count == 2, "append after the last clip");
        var second = project.Tracks[0].Clips[1];

        // Moving: overlap is refused, a second layer takes the clip, linked sound follows.
        Check(TimelineOps.Move(project, second.Id, v1.Id, 100) == null, "overlapping move refused");
        Check(TimelineOps.NearestFree(project, second.Id, v1.Id, 100) == 300, "nearest free slot is flush after the first clip");
        project = TimelineOps.AddTrack(project, TrackKind.Visual, out var v2Id);
        Check(project.Tracks.Count == 3 && project.Tracks[1].Id == v2Id && project.Tracks[1].Name == "V2" && project.Tracks[2].Kind == TrackKind.Audio, "new visual track sits above V1 and before audio");
        var layered = TimelineOps.Move(project, second.Id, v2Id, 100);
        Check(layered != null && layered.Tracks[1].Clips.Single().Start == 100 && layered.Tracks[0].Clips.Count == 1, "clip moved onto the upper layer");
        Check(TimelineOps.Move(project, second.Id, a1.Id, 0) == null, "video cannot move to an audio track");
        var shifted = TimelineOps.Move(layered!, video.Id, v1.Id, 30)!;
        Check(shifted.Tracks[0].Clips[0].Start == 30 && shifted.Tracks[2].Clips[0].Start == 30, "linked sound moves with its picture");
        Check(TimelineOps.VisibleAt(layered!, 150).Select(c => c.Id).SequenceEqual(new[] { video.Id, second.Id }) && TimelineOps.VisibleAt(layered!, 50).Count == 1, "layers listed bottom first");

        // Trimming: clamped to the source, neighbours and one frame; linked sound trims too.
        var trimmed = TimelineOps.TrimStart(layered!, video.Id, 60);
        var tv = trimmed.Tracks[0].Clips[0]; var ta = trimmed.Tracks[2].Clips[0];
        Check(tv.Start == 60 && tv.Duration == 240 && Math.Abs(tv.SourceIn - 2) < 1e-9 && ta.Start == 60 && ta.Duration == 240 && Math.Abs(ta.SourceIn - 2) < 1e-9, "left trim moves start and source in together");
        Check(TimelineOps.TrimStart(trimmed, video.Id, -500).Tracks[0].Clips[0].Start == 0, "left trim cannot pass the start of the source");
        Check(TimelineOps.TrimStart(trimmed, video.Id, 5000).Tracks[0].Clips[0].Duration == 1, "left trim keeps one frame");
        Check(TimelineOps.TrimEnd(trimmed, video.Id, 9000).Tracks[0].Clips[0].End == 300, "right trim cannot pass the end of the source");
        Check(TimelineOps.TrimEnd(trimmed, video.Id, 120).Tracks[0].Clips[0].Duration == 60 && TimelineOps.TrimEnd(trimmed, video.Id, 120).Tracks[2].Clips[0].Duration == 60, "right trim shortens picture and sound");
        Check(TimelineOps.TrimEnd(project, video.Id, 400).Tracks[0].Clips[0].End == 300, "right trim stops at the next clip");

        // Splitting cuts picture and sound, re-linking the right halves to each other.
        var split = TimelineOps.Split(project, 90);
        var left = split.Tracks[0].Clips[0]; var right = split.Tracks[0].Clips[1]; var rightSound = split.Tracks[2].Clips[1];
        Check(split.Tracks[0].Clips.Count == 3 && left.Duration == 90 && right.Start == 90 && right.Duration == 210 && Math.Abs(right.SourceIn - 3) < 1e-9, "split at frame 90");
        Check(left.Id == video.Id && right.Id != video.Id && right.LinkId != left.LinkId && right.LinkId == rightSound.LinkId && split.Tracks[2].Clips[0].LinkId == left.LinkId, "halves keep their own links");
        Check(ReferenceEquals(TimelineOps.Split(project, 0), project) && ReferenceEquals(TimelineOps.Split(project, 5000), project), "split outside any clip changes nothing");
        Check(TimelineOps.Split(project, 320, new[] { video.Id }).Tracks[0].Clips.Count == 2, "split limited to chosen clips");

        // Delete removes linked partners; ripple closes the gap on each track.
        var deleted = TimelineOps.Delete(split, new[] { left.Id });
        Check(deleted.Tracks[0].Clips.Count == 2 && deleted.Tracks[2].Clips.Count == 1 && deleted.Tracks[0].Clips[0].Start == 90, "delete leaves a gap");
        var rippled = TimelineOps.Delete(split, new[] { left.Id }, ripple: true);
        Check(rippled.Tracks[0].Clips[0].Start == 0 && rippled.Tracks[0].Clips[1].Start == 210 && rippled.Tracks[2].Clips[0].Start == 0 && rippled.Duration == 330, "ripple delete closes the gap");
        var unlinked = TimelineOps.Unlink(project, video.Id);
        Check(unlinked.AllClips.All(c => c.LinkId == null) && TimelineOps.Delete(unlinked, new[] { video.Id }).Tracks[2].Clips.Count == 1, "detached sound survives deleting its picture");
        Check(TimelineOps.SnapPoints(project).SequenceEqual(new long[] { 0, 300, 420 }), "snap points are clip edges");
        Check(TimelineOps.RemoveTrack(project, v2Id).Tracks.Count == 2 && TimelineOps.RemoveTrack(project, a1.Id).Tracks.Count == 3, "the last audio track stays");

        // Project file: round trip, and a moved folder is found again through the relative path.
        string dir = Path.Combine(AppContext.BaseDirectory, "videochecks", "one"); Directory.CreateDirectory(dir);
        string clipPath = Path.Combine(dir, "clip.mp4"); File.WriteAllText(clipPath, "x");
        var saved = TimelineOps.ForSource(Source(clipPath, 3));
        saved = TimelineOps.Update(saved, saved.Tracks[1].Clips[0].Id, c => ((AudioClip)c) with { Volume = 0.5, Muted = true });
        string projectPath = Path.Combine(dir, "edit" + VideoProjectFile.Extension);
        VideoProjectFile.Save(projectPath, saved);
        var loaded = VideoProjectFile.Load(projectPath);
        Check(loaded.Tracks[0].Clips[0] is VideoClip && loaded.Tracks[1].Clips[0] is AudioClip { Volume: 0.5, Muted: true } && loaded.Sources[0].RelativePath == "clip.mp4" && loaded.Tracks[0].Clips[0].Id == saved.Tracks[0].Clips[0].Id, "project round trip keeps clip kinds and ids");
        Check(VideoProjectFile.Missing(loaded).Count == 0, "sources found");
        string movedDir = Path.Combine(AppContext.BaseDirectory, "videochecks", "two");
        if (Directory.Exists(movedDir)) Directory.Delete(movedDir, true);
        Directory.Move(dir, movedDir);
        var relocated = VideoProjectFile.Load(Path.Combine(movedDir, "edit" + VideoProjectFile.Extension));
        Check(relocated.Sources[0].Path == Path.Combine(movedDir, "clip.mp4") && VideoProjectFile.Missing(relocated).Count == 0, "moved project finds media by relative path");
        File.Delete(Path.Combine(movedDir, "clip.mp4"));
        var broken = VideoProjectFile.Load(Path.Combine(movedDir, "edit" + VideoProjectFile.Extension));
        Check(VideoProjectFile.Missing(broken).Count == 1 && VideoProjectFile.Relink(broken, broken.Sources[0].Id, @"C:\new.mp4").Sources[0].Path == @"C:\new.mp4", "missing media reported and relinkable");
        Directory.Delete(movedDir, true);

        // Exact ffmpeg arguments.
        Same(new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", "7.5", "-i", @"C:\v.mp4", "-an", "-sn", "-vf", "fps=30000/1001,scale=960:540:flags=fast_bilinear", "-pix_fmt", "bgra", "-f", "rawvideo", "-" },
            FrameReader.Arguments(new FrameReaderOptions { Path = @"C:\v.mp4", Start = 7.5, Width = 960, Height = 540, FrameRate = FrameRate.Ntsc30 }), "frame reader arguments");
        Same(new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-hwaccel", "d3d11va", "-ss", "0", "-i", @"C:\v.mp4", "-an", "-sn", "-vf", "fps=60,scale=320:180:flags=fast_bilinear", "-frames:v", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "-" },
            FrameReader.Arguments(new FrameReaderOptions { Path = @"C:\v.mp4", Width = 320, Height = 180, FrameRate = FrameRate.Fps60, HardwareDecode = true, MaxFrames = 1 }), "single-frame GPU reader arguments");
        Check(FrameReader.Fit(3840, 2160, 1280, 1280) == (1280, 720) && FrameReader.Fit(1080, 1920, 1280, 720) == (404, 720) && FrameReader.Fit(640, 480, 320, 180) == (240, 180), "fit keeps aspect and even sizes");
        var uhd = new MediaInfo(@"C:\uhd.mp4", TimeSpan.FromSeconds(10), 3840, 2160, FrameRate.Fps60, "h264", "aac", true, 0, 1, "mp4");
        Same(new[] { "-hide_banner", "-nostats", "-progress", "pipe:1", "-i", @"C:\uhd.mp4", "-vf", "scale=-2:720:flags=bilinear", "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "28", "-b:v", "0", "-g", "15", "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart", "-f", "mp4", "-y", @"C:\p.mp4" },
            ProxyCache.Arguments(uhd, EncoderProbe.Hardware[0], @"C:\p.mp4"), "proxy arguments: 720 lines, 15-frame GOP");
        var plan = new ExportPlan
        {
            Width = 1280, Height = 720, FrameRate = FrameRate.Fps30, Duration = 4, Background = "0x000000",
            Video = new[]
            {
                new ExportVideoClip(@"C:\a.mp4", 1.5, 2, 0, 0, 0, 1280, 720),
                new ExportVideoClip(@"C:\b.png", 0, 1, 2.5, 900, 500, 320, 180) { Rotation = 90, Opacity = 0.5, CropLeft = 0.25, IsImage = true },
            },
            Audio = new[] { new ExportAudioClip(@"C:\a.mp4", 1.5, 2, 0, 0.5) },
        };
        string graph = "color=c=0x000000:s=1280x720:r=30:d=4,format=yuv420p[bg]"
            + ";[0:v]fps=30,scale=1280:720:flags=lanczos,setsar=1,setpts=PTS-STARTPTS+0/TB[v0];[bg][v0]overlay=x=640-w/2:y=360-h/2:eof_action=pass:enable='gte(t,0)*lt(t,2)'[o0]"
            + ";[1:v]fps=30,crop=iw*0.75:ih*1:iw*0.25:ih*0,scale=320:180:flags=lanczos,setsar=1,format=rgba,colorchannelmixer=aa=0.5,rotate=1.570796:ow=rotw(1.570796):oh=roth(1.570796):c=none,setpts=PTS-STARTPTS+2.5/TB[v1]"
            + ";[o0][v1]overlay=x=1060-w/2:y=590-h/2:eof_action=pass:enable='gte(t,2.5)*lt(t,3.5)'[o1]"
            + ";[o1]format=yuv420p[vout]"
            + ";[2:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.5,adelay=0:all=1[a2];[a2]amix=inputs=1:normalize=0:duration=longest,apad,atrim=0:4[aout]";
        Same(new[] { "-hide_banner", "-nostats", "-progress", "pipe:1", "-ss", "1.5", "-t", "2", "-i", @"C:\a.mp4", "-loop", "1", "-framerate", "30", "-t", "1", "-i", @"C:\b.png", "-ss", "1.5", "-t", "2", "-vn", "-i", @"C:\a.mp4",
                "-filter_complex", graph, "-map", "[vout]", "-map", "[aout]", "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-g", "30",
                "-r", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-t", "4", "-movflags", "+faststart", "-f", "mp4", "-y", @"C:\out.mp4" },
            ExportArguments.Build(plan, EncoderProbe.Hardware[0], 75, @"C:\out.mp4"), "export arguments: a full-canvas video and a cropped, faded, turned still");
        Check(!ExportArguments.Build(plan, EncoderProbe.Hardware[0], 75, @"C:\out.mp4", includeAudio: false).Contains("[aout]"), "export without audio maps no audio");
        Same(new[] { "-hide_banner", "-nostats", "-progress", "pipe:1", "-ss", "1.5", "-t", "2", "-vn", "-i", @"C:\a.mp4", "-filter_complex",
                "[0:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.5,adelay=0:all=1[a0];[a0]amix=inputs=1:normalize=0:duration=longest,apad,atrim=0:4[aout]",
                "-map", "[aout]", "-c:a", "pcm_s16le", "-f", "wav", "-y", @"C:\mix.wav" },
            ExportArguments.BuildAudioMix(plan, @"C:\mix.wav"), "audio mix arguments");

        // The plan built from a project: hidden and muted things are left out, layers stay in order.
        var planned = TimelineOps.ToPlan(layered!);
        Check(planned.Video.Count == 2 && planned.Video[0].Path == a.Path && planned.Video[1].Path == b.Path && Math.Abs(planned.Video[1].Start - 100 / 30.0) < 1e-9 && planned.Audio.Count == 1 && Math.Abs(planned.Duration - 10) < 1e-9, "plan from a layered project");
        var hidden = TimelineOps.SetTrack(layered!, layered!.Tracks[1] with { Hidden = true });
        hidden = TimelineOps.SetTrack(hidden, hidden.Tracks[2] with { Muted = true });
        Check(TimelineOps.ToPlan(hidden).Video.Count == 1 && TimelineOps.ToPlan(hidden).Audio.Count == 0, "hidden and muted tracks left out");
        Check(TimelineOps.ToPlan(layered, s => s.Path + ".proxy").Video[0].Path.EndsWith(".proxy") && TimelineOps.ToPlan(layered, s => s.Path + ".proxy").Audio[0].Path == a.Path, "proxy swap applies to pictures only");
        TimelineGestures();
        Console.WriteLine("PASS: video editor timeline edits and mouse gestures, project file and relink, frame reader/proxy/export/audio mix arguments.");
        Overlays();
        PreviewGestures();
        Console.WriteLine("PASS: video editor overlays (placement, stills, on-top insert, audio-only, scaled plans, waveform peaks) and preview handles (pick, move, snap, resize, turn, crop).");
    }

    /// <summary>
    /// Opens the video editor window on its own, outside the tray app and its single-instance
    /// pipe, so it can be looked at while another Vibershot is running:
    /// <c>CaptureChecks --editor file [canvasX,canvasY]</c>. The optional point is clicked in the
    /// preview once the window has settled, to select the picture there.
    /// </summary>
    public static void ShowEditor(string path, string? pick)
    {
        App.Hosted = true;
        var app = new App();
        app.InitializeComponent();
        // Application.Run would install this; without it, awaits in the window resume on pool threads.
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var window = VideoEditorWindow.Open(app.Config, path);
        window.ShowActivated = false;
        window.Closed += (_, _) => app.Shutdown();
        if (pick != null && window.FindName("Preview") is VideoPreview preview)
        {
            var parts = pick.Split(',').Select(part => double.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, _) => { timer.Stop(); preview.PointerDown(preview.CanvasToScreen(new Point(parts[0], parts[1]))); preview.PointerUp(); };
            timer.Start();
        }
        app.Run(window);
    }


    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

    /// <summary>Stage 2 model work: where pictures sit, stills, layering on top, sound on its own.</summary>
    static void Overlays()
    {
        var a = Source(@"C:\media\a.mp4", 10);
        var project = TimelineOps.ForSource(a);
        var clip = (VideoClip)project.Tracks[0].Clips[0];

        // Default placement fits and centres; Fill covers the canvas and crops the overhang.
        var fit = TimelineOps.Fit(project, 640, 480);
        Check(fit.Width == 1440 && fit.Height == 1080 && fit.X == 240 && fit.Y == 0 && !fit.IsCropped, "4:3 fitted into 16:9 is pillarboxed");
        var fill = TimelineOps.Fill(project, 640, 480);
        Check(fill.Width == 1920 && fill.Height == 1080 && fill.CropLeft == 0 && Near(fill.CropTop, 0.125) && Near(fill.CropBottom, 0.125), "4:3 filling 16:9 loses an eighth top and bottom");
        Check(Near(fill.FullBounds.Height, 1440) && Near(fill.FullBounds.Y, -180), "the whole picture of a filled clip overhangs the canvas");
        Check(clip.Placement == null && TimelineOps.PlacementOf(project, clip).Bounds == new Rect(0, 0, 1920, 1080), "an unplaced clip fills its matching canvas");
        var natural = TimelineOps.Natural(project, 400, 200);
        Check(natural.Bounds == new Rect(760, 440, 400, 200) && TimelineOps.Natural(project, 4000, 2000).Width == 1920, "a small picture keeps its size, a large one is fitted");

        // Placing a clip, and a plan for a half-size export scales the boxes with the canvas.
        var placed = TimelineOps.SetPlacement(project, clip.Id, new Placement { X = 100, Y = 50, Width = 960, Height = 540, Rotation = 30, Opacity = 0.8, CropRight = 0.1 });
        var half = TimelineOps.ToPlan(placed, null, 960, 540).Video.Single();
        Check(half.X == 50 && half.Y == 25 && half.Width == 480 && half.Height == 270 && half.Rotation == 30 && half.Opacity == 0.8 && half.CropRight == 0.1 && !half.IsImage, "plan scales a placement to the output size");
        var full = TimelineOps.ToPlan(project).Video.Single();
        Check(full.X == 0 && full.Y == 0 && full.Width == 1920 && full.Height == 1080 && full.Rotation == 0 && full.Opacity == 1, "plan gives an unplaced clip its fitted box");
        Check(TimelineOps.ToPlan(TimelineOps.SetPlacement(project, clip.Id, fit with { Opacity = 0 })).Video.Count == 0, "a fully transparent clip is left out");
        Check(TimelineOps.SetPlacement(placed, clip.Id, null).Tracks[0].Clips[0] is VideoClip { Placement: null }, "placement can be reset");

        // Stills: five seconds at their own size, any length, never a source offset.
        var logo = MediaSource.ForImage(@"C:\media\logo.png", 400, 200);
        Check(logo.IsImage && logo.HasPicture && !logo.HasVideo && !logo.HasAudio, "an image source is a picture without video or sound");
        var withLogo = TimelineOps.InsertOnTop(project, logo, 30, out var logoId);
        var found = TimelineOps.Find(withLogo, logoId)!.Value;
        Check(found.Clip is ImageClip { Start: 30, Duration: 150 } still && still.Placement!.Bounds == new Rect(760, 440, 400, 200) && found.Track.Name == "V2", "a still lands on a new top layer for five seconds at its own size");
        Check(TimelineOps.VisibleAt(withLogo, 60).Select(c => c.Id).SequenceEqual(new[] { clip.Id, logoId }) && TimelineOps.VisibleAt(withLogo, 200).Count == 1, "the still shows over the video only during its span");
        var longer = TimelineOps.TrimEnd(withLogo, logoId, 900);
        Check(TimelineOps.Find(longer, logoId)!.Value.Clip.End == 900, "a still can be stretched past any source length");
        var earlier = TimelineOps.TrimStart(longer, logoId, 0);
        Check(TimelineOps.Find(earlier, logoId)!.Value.Clip is { Start: 0, Duration: 900, SourceIn: 0 }, "a still can be stretched left and has no source offset");
        var cut = TimelineOps.Split(earlier, 300, new[] { logoId });
        Check(cut.Tracks[1].Clips.Count == 2 && cut.Tracks[1].Clips.All(c => c is ImageClip { SourceIn: 0 }), "a split still is two stills");
        var again = TimelineOps.InsertOnTop(withLogo, logo, 60, out var secondLogo);
        Check(TimelineOps.Find(again, secondLogo)!.Value.Track.Name == "V3" && again.Sources.Count == 2, "a second overlapping still goes one layer higher, sharing the source");
        Check(TimelineOps.Find(TimelineOps.InsertOnTop(withLogo, logo, 600, out var later), later)!.Value.Track.Name == "V1", "past the end of everything the lowest layer is free");
        var stillFirst = TimelineOps.ForSource(logo);
        Check(stillFirst.Width == 1920 && stillFirst.Tracks[0].Clips.Single() is ImageClip, "a project started from a still gets the default canvas");

        // The project file keeps stills and placements.
        var json = DocumentJson.Serialize(TimelineOps.SetPlacement(withLogo, clip.Id, new Placement { X = 1, Y = 2, Width = 300, Height = 200, Rotation = 45, Opacity = 0.5, CropTop = 0.2 }));
        var back = DocumentJson.Deserialize<VideoProject>(json)!;
        Check(back.Tracks[0].Clips[0] is VideoClip { Placement: { X: 1, Y: 2, Width: 300, Height: 200, Rotation: 45, Opacity: 0.5, CropTop: 0.2 } }
            && back.Tracks[1].Clips[0] is ImageClip { Placement.Width: 400 } && back.Sources[1].IsImage, "placements and stills survive the project file");
        var planned = TimelineOps.ToPlan(back, s => s.Path + ".proxy");
        Check(planned.Video.Count == 2 && planned.Video[1].IsImage && planned.Video[1].Path == logo.Path && planned.Video[0].Path.EndsWith(".proxy"), "a still is never swapped for a proxy");

        // Sound on its own: unlinked, on a free audio lane.
        var soundOnly = TimelineOps.InsertAudioOnly(project, a, 0, out var soundId);
        var lane = TimelineOps.Find(soundOnly, soundId)!.Value;
        Check(lane.Clip is AudioClip { LinkId: null, Duration: 300 } && lane.Track.Name == "A2" && soundOnly.Tracks[0].Clips.Count == 1, "audio-only insert adds an unlinked clip on a new lane");
        Check(ReferenceEquals(TimelineOps.InsertAudioOnly(project, logo, 0, out _), project), "a still has no sound to add");

        // Waveform: argument string and peak lookup.
        Same(new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", @"C:\v.mp4", "-vn", "-sn", "-ac", "1", "-ar", "8000", "-f", "s16le", "-" }, WaveformCache.Arguments(@"C:\v.mp4"), "waveform arguments");
        var wave = new Waveform(0.01, new byte[] { 0, 51, 255, 102, 0 });
        Check(Near(wave.PeakBetween(0, 0.005), 0) && Near(wave.PeakBetween(0.011, 0.012), 0.2) && Near(wave.PeakBetween(0, 0.04), 1) && Near(wave.PeakBetween(0.035, 9), 0.4) && new Waveform(0.01, Array.Empty<byte>()).PeakBetween(0, 1) == 0, "waveform peak lookup");

        // Handle geometry shared with the image editor.
        var box = new Rect(100, 100, 200, 100);
        Check(HandleMath.Resize(box, BoxEdges.Right | BoxEdges.Bottom, new Vector(100, 0), true) == new Rect(100, 100, 300, 150), "corner resize keeps the shape");
        Check(HandleMath.Resize(box, BoxEdges.Left, new Vector(50, 30), false) == new Rect(150, 100, 150, 100), "side resize moves one edge");
        Check(HandleMath.Resize(box, BoxEdges.Right, new Vector(-300, 0), false) == new Rect(0, 100, 100, 100), "dragging through the opposite edge flips, never negative");
        var turned = HandleMath.ResizeRotated(box, 90, BoxEdges.Right, new Point(200, 200), new Point(200, 260), false);
        Check(Near(turned.Width, 260, 1e-6) && Near(turned.Height, 100, 1e-6) && Near(turned.X + turned.Width / 2, 200, 1e-6) && Near(turned.Y + turned.Height / 2, 180, 1e-6), "a box turned 90° grows along its own axis and keeps the far edge still: " + turned);
        Check(Near(HandleMath.AngleTo(new Point(0, 0), new Point(0, -10), false), 0) && Near(HandleMath.AngleTo(new Point(0, 0), new Point(10, 0), false), 90) && Near(HandleMath.AngleTo(new Point(0, 0), new Point(-10, 0), false), -90)
            && Near(HandleMath.AngleTo(new Point(0, 0), new Point(10, -9), true), 45) && Near(HandleMath.AngleTo(new Point(0, 0), new Point(0, 10), false), 180), "pointer angle is clockwise from straight up");
    }

    /// <summary>Drives the preview's handles through its pointer methods: the same code the mouse reaches.</summary>
    static void PreviewGestures()
    {
        var a = Source(@"C:\media\a.mp4", 10);
        var logo = MediaSource.ForImage(@"C:\media\logo.png", 400, 200);
        var history = new History<VideoProject>(TimelineOps.InsertOnTop(TimelineOps.ForSource(a), logo, 0, out var logoId));
        var videoId = history.Current.Tracks[0].Clips[0].Id;
        // 992 x 572 leaves a 960 x 540 picture at (16, 16): one screen pixel is two canvas pixels.
        var view = new VideoPreview { History = history };
        view.Measure(new Size(992, 572)); view.Arrange(new Rect(0, 0, 992, 572));
        void Refresh() => view.Show(history.Current, 0, false);
        Point At(double canvasX, double canvasY) => view.CanvasToScreen(new Point(canvasX, canvasY));
        Placement Logo() => ((ImageClip)TimelineOps.Find(history.Current, logoId)!.Value.Clip).Placement!;
        Refresh();
        Check(At(0, 0) == new Point(16, 16) && At(1920, 1080) == new Point(976, 556) && view.ScreenToCanvas(new Point(496, 286)) == new Point(960, 540), "canvas and screen coordinates");
        Guid? picked = Guid.Empty; int picks = 0; view.ClipPicked += id => { picked = id; picks++; };

        // A click takes the top picture under the pointer; the video shows through beside it.
        Check(view.PointerDown(At(960, 540)), "press on the still starts a move"); view.PointerUp();
        Check(picked == logoId && view.Selected == logoId && picks == 1, "the still on top is picked");
        view.PointerDown(At(200, 200)); view.PointerUp();
        Check(picked == videoId && view.Selected == videoId, "beside the still the video is picked");
        view.PointerDown(new Point(4, 4));
        Check(picked == null && view.Selected == null, "a click off the canvas clears the pick");

        // Move: one undo step, whole pixels, and it snaps back to the canvas centre.
        view.PointerDown(At(960, 540)); int depth = history.UndoDepth;
        for (int dx = 20; dx <= 300; dx += 20) view.PointerMove(At(960 + dx, 540 + 100));
        view.PointerUp(); Refresh();
        Check(Logo().Bounds == new Rect(1060, 540, 400, 200) && history.UndoDepth == depth + 1, "dragging the still moves it in one step: " + Logo().Bounds);
        view.PointerDown(At(1260, 640)); view.PointerMove(At(1100, 600)); view.PointerMove(At(967, 536)); view.PointerUp(); Refresh();
        Check(Logo().Bounds == new Rect(760, 440, 400, 200), "near the middle the still snaps to the canvas centre: " + Logo().Bounds);
        view.PointerDown(At(960, 540)); view.PointerMove(At(900, 500)); view.PointerMove(At(210, 107)); view.PointerUp(); Refresh();
        Check(Logo().X == 0 && Logo().Y == 0, "near the corner the still snaps to the canvas edges: " + Logo().Bounds);
        view.PointerDown(At(200, 100)); view.PointerMove(At(300, 150)); view.PointerMove(At(207, 104), ModifierKeys.Alt); view.PointerUp(); Refresh();
        Check(Logo().X == 7 && Logo().Y == 4, "Alt turns snapping off: " + Logo().Bounds);
        view.PointerDown(At(207, 104)); view.PointerMove(At(300, 150)); view.PointerMove(At(507, 204), ModifierKeys.Shift); view.PointerUp(); Refresh();
        Check(Logo().X == 307 && Logo().Y == 4, "Shift keeps a move on one axis: " + Logo().Bounds);
        history.Push(TimelineOps.SetPlacement(history.Current, logoId, new Placement { X = 760, Y = 440, Width = 400, Height = 200 })); Refresh();

        // Resize from the corner keeps the shape; Shift frees it; a side handle stretches.
        var corner = view.HandlePoint(BoxEdges.Right | BoxEdges.Bottom)!.Value;
        Check(corner == At(1160, 640), "bottom right handle sits on the corner");
        view.PointerDown(corner); view.PointerMove(At(1200, 650)); view.PointerMove(At(1260, 640)); view.PointerUp(); Refresh();
        Check(Logo().Bounds == new Rect(760, 440, 500, 250), "corner drag keeps 2:1: " + Logo().Bounds);
        view.PointerDown(view.HandlePoint(BoxEdges.Right | BoxEdges.Bottom)!.Value); view.PointerMove(At(1300, 700), ModifierKeys.Shift); view.PointerMove(At(1260, 740), ModifierKeys.Shift); view.PointerUp(); Refresh();
        Check(Logo().Bounds == new Rect(760, 440, 500, 300), "Shift frees the shape: " + Logo().Bounds);
        view.PointerDown(view.HandlePoint(BoxEdges.Top)!.Value); view.PointerMove(At(1010, 460)); view.PointerMove(At(1010, 490)); view.PointerUp(); Refresh();
        Check(Logo().Bounds == new Rect(760, 490, 500, 250), "top handle moves only the top edge: " + Logo().Bounds);

        // Crop: the edges move inside the picture, which stays where it is.
        view.CropMode = true;
        Check(view.HandlePoint(BoxEdges.None) == null, "no rotate handle while cropping");
        view.PointerDown(view.HandlePoint(BoxEdges.Left)!.Value); view.PointerMove(At(800, 615)); view.PointerMove(At(860, 615)); view.PointerUp(); Refresh();
        var cropped = Logo();
        Check(cropped.Bounds == new Rect(860, 490, 400, 250) && Near(cropped.CropLeft, 0.2) && Near(cropped.FullBounds.X, 760, 1e-6) && Near(cropped.FullBounds.Width, 500, 1e-6), "cropping a fifth off the left: " + cropped.Bounds + " " + cropped.CropLeft);
        view.PointerDown(view.HandlePoint(BoxEdges.Left)!.Value); view.PointerMove(At(800, 615)); view.PointerMove(At(500, 615)); view.PointerUp(); Refresh();
        Check(Logo().Bounds == new Rect(760, 490, 500, 250) && Logo().CropLeft == 0, "dragging back out stops at the picture's own edge: " + Logo().Bounds);
        Check(!view.PointerDown(At(1000, 600)), "the picture does not move while cropping");
        view.CropMode = false;

        // Rotate: the handle above the top edge turns the picture about its centre.
        var knob = view.HandlePoint(BoxEdges.None)!.Value;
        Check(Near(knob.X, At(1010, 0).X) && Near(knob.Y, At(0, 490).Y - VideoPreview.RotateHandleDistance), "rotate handle floats above the top edge");
        var centre = At(1010, 615);
        view.PointerDown(knob); view.PointerMove(new Point(centre.X + 80, centre.Y - 80)); view.PointerMove(new Point(centre.X + 120, centre.Y + 1)); view.PointerUp(); Refresh();
        Check(Logo().Rotation == 90 && Logo().Bounds == new Rect(760, 490, 500, 250), "turning to the right settles on 90°: " + Logo().Rotation);
        // Turned a quarter, the 500 x 250 box covers 250 x 500 on screen: a point above its centre is inside, one to the side is not.
        view.PointerDown(At(1010, 400)); view.PointerUp();
        Check(view.Selected == logoId, "hit test follows the rotation");
        view.PointerDown(At(800, 615)); view.PointerUp();
        Check(view.Selected == videoId, "beside the turned still the video is picked");

        // A locked layer shows its outline but cannot be dragged.
        var locked = history.Current.Tracks[1] with { Locked = true };
        history.Push(TimelineOps.SetTrack(history.Current, locked)); Refresh();
        Check(!view.PointerDown(At(1010, 615)) && view.Selected == logoId && view.HandlePoint(BoxEdges.Left) != null, "a locked layer can be picked but not moved");
    }

    /// <summary>Drives the timeline control through its pointer methods: the same code the mouse reaches.</summary>
    static void TimelineGestures()
    {
        var a = Source(@"C:\media\a.mp4", 10); var b = Source(@"C:\media\b.mp4", 4, audio: false);
        var history = new History<VideoProject>(TimelineOps.Append(TimelineOps.ForSource(a), b));
        var view = new TimelineView { History = history };
        view.Measure(new Size(1440, 300)); view.Arrange(new Rect(0, 0, 1440, 300));
        view.SetZoom(100); view.ScrollX = 0;
        // 100 px per second at 30 fps: clip A covers x 148..1148, clip B 1148..1548. V1 is the lane at y 24..76, A1 at 76..116.
        const double h = TimelineView.HeaderWidth; const double v1 = 50, a1 = 96;
        var clipA = history.Current.Tracks[0].Clips[0]; var clipB = history.Current.Tracks[0].Clips[1];
        long moved = -1; view.PlayheadMoved += frame => moved = frame;

        // Ruler click parks the playhead; it snaps to a clip edge when close.
        Check(view.PointerDown(new Point(h + 250, 10)), "ruler press starts a scrub"); view.PointerUp();
        Check(moved == 75 && view.Playhead == 75, "ruler click at 2.5 s is frame 75, got " + moved);
        view.PointerDown(new Point(h + 1003, 10)); view.PointerUp();
        Check(view.Playhead == 300, "playhead snaps to the cut between clips, got " + view.Playhead);
        view.PointerDown(new Point(h + 250, 10)); view.PointerUp();

        // Click selects a clip with its linked sound; a drag moves both and is one undo step.
        view.PointerDown(new Point(h + 400, v1)); view.PointerUp();
        Check(view.Selection.Count == 2 && view.Selection.Contains(clipA.Id), "click selects the clip and its linked audio");
        int depth = history.UndoDepth;
        view.PointerDown(new Point(h + 1200, v1));
        for (int x = 1210; x <= 1400; x += 10) view.PointerMove(new Point(h + x, v1));
        view.PointerUp();
        var movedB = TimelineOps.Find(history.Current, clipB.Id)!.Value.Clip;
        Check(movedB.Start == 360 && history.UndoDepth == depth + 1, $"dragging B 200 px right moves it 60 frames in one step, got {movedB.Start}");
        history.Undo();
        Check(TimelineOps.Find(history.Current, clipB.Id)!.Value.Clip.Start == 300, "undo puts the clip back");

        // Dragging onto A is refused: B lands flush after it instead of overlapping.
        view.PointerDown(new Point(h + 1200, v1)); view.PointerMove(new Point(h + 900, v1)); view.PointerMove(new Point(h + 700, v1)); view.PointerUp();
        Check(TimelineOps.Find(history.Current, clipB.Id)!.Value.Clip.Start == 300 && history.Current.Tracks[0].Clips.Count == 2, "a clip cannot be dropped on top of another on the same layer");

        // Dragging above the top video lane makes a new layer and puts the clip there.
        view.PointerDown(new Point(h + 1200, v1)); view.PointerMove(new Point(h + 1100, 30)); view.PointerMove(new Point(h + 700, 8)); view.PointerUp();
        var layered = history.Current;
        var onTop = TimelineOps.Find(layered, clipB.Id)!.Value;
        Check(layered.Tracks.Count(t => t.Kind == TrackKind.Visual) == 2 && onTop.Track.Name == "V2" && onTop.Clip.Start == 150, $"dragged to a new upper layer at 5 s, got {onTop.Track.Name} {onTop.Clip.Start}");
        // V2 is now the top lane (y 24..76) and V1 sits under it (76..128): drag B back down onto V1 after A.
        view.PointerDown(new Point(h + 600, 50)); view.PointerMove(new Point(h + 900, 90)); view.PointerMove(new Point(h + 1104, 100)); view.PointerUp();
        var back = TimelineOps.Find(history.Current, clipB.Id)!.Value;
        Check(back.Track.Name == "V1" && back.Clip.Start == 300, $"dragged back down to V1, snapped after A, got {back.Track.Name} {back.Clip.Start}");

        // Trim A from its right edge: picture and linked sound shorten together.
        view.PointerDown(new Point(h + 997, 100)); view.PointerMove(new Point(h + 900, 100)); view.PointerMove(new Point(h + 800, 100)); view.PointerUp();
        var trimmedA = TimelineOps.Find(history.Current, clipA.Id)!.Value.Clip;
        var soundA = history.Current.AllClips.OfType<AudioClip>().Single();
        Check(trimmedA.Duration == 240 && soundA.Duration == 240, $"right trim to 8 s, got {trimmedA.Duration} and {soundA.Duration}");
        // Trim A from its left edge by one second.
        view.PointerDown(new Point(h + 3, 100)); view.PointerMove(new Point(h + 50, 100)); view.PointerMove(new Point(h + 100, 100)); view.PointerUp();
        trimmedA = TimelineOps.Find(history.Current, clipA.Id)!.Value.Clip;
        Check(trimmedA.Start == 30 && trimmedA.Duration == 210 && Math.Abs(trimmedA.SourceIn - 1) < 1e-9, $"left trim by 1 s, got start {trimmedA.Start} length {trimmedA.Duration}");

        // Razor: one click cuts the clip (and its sound) under the pointer.
        view.Tool = TimelineTool.Razor;
        Check(!view.PointerDown(new Point(h + 500, 100)), "a razor click is not a drag");
        Check(history.Current.Tracks[0].Clips.Count == 3 && history.Current.Tracks[0].Clips[1].Start == 150 && history.Current.AllClips.OfType<AudioClip>().Count() == 2, "razor cut at 5 s splits picture and sound");
        view.Tool = TimelineTool.Select;

        // Ctrl+click adds to the selection; the header toggle hides a layer.
        view.PointerDown(new Point(h + 200, 100)); view.PointerUp();
        view.PointerDown(new Point(h + 1100, 100), ModifierKeys.Control);
        Check(view.Selection.Count == 3, "ctrl+click extends the selection, got " + view.Selection.Count);
        view.PointerDown(new Point(h - 22, 50));
        Check(history.Current.Tracks.First(t => t.Name == "V2").Hidden, "eye toggle hides the layer");
    }

    /// <summary>Renders a real two-layer project and checks the result frame by frame.</summary>
    public static void RunLive(FfmpegRunner runner, EncoderInfo encoder, string dir)
    {
        string red = Path.Combine(dir, "red.mp4"), blue = Path.Combine(dir, "blue.mp4"), output = Path.Combine(dir, "layered.mp4"), wav = Path.Combine(dir, "mix.wav");
        var options = new RecordingOptions { Region = new PixelRect(0, 0, 320, 180), FrameRate = FrameRate.Fps30, Quality = 90, Encoder = encoder, OutputPath = red };
        string[] Make(string color, string size, string seconds, bool audio, string path)
        {
            var args = new List<string>(FfmpegRunner.StandardPrefix) { "-f", "lavfi", "-i", $"color=c={color}:s={size}:r=30:d={seconds}" };
            if (audio) args.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}" });
            args.AddRange(RecordingArguments.EncoderArguments(options));
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            if (audio) args.AddRange(new[] { "-c:a", "aac" });
            args.AddRange(new[] { "-f", "mp4", "-y", path });
            return args.ToArray();
        }
        var made = runner.RunAsync(Make("red", "320x180", "3", true, red)).GetAwaiter().GetResult(); Check(made.Succeeded, "red test clip: " + made.ErrorText());
        made = runner.RunAsync(Make("blue", "160x120", "2", false, blue)).GetAwaiter().GetResult(); Check(made.Succeeded, "blue test clip: " + made.ErrorText());

        // Red fills V1 for three seconds; blue (4:3, so pillarboxed) sits on V2 from frame 30 to 60.
        var redSource = MediaSource.From(Ffprobe.ProbeAsync(red).GetAwaiter().GetResult());
        var blueSource = MediaSource.From(Ffprobe.ProbeAsync(blue).GetAwaiter().GetResult());
        Check(redSource.HasAudio && !blueSource.HasAudio && redSource.Width == 320 && blueSource.Height == 120, "probed test clips");
        var project = TimelineOps.ForSource(redSource);
        project = TimelineOps.AddTrack(project, TrackKind.Visual, out var v2);
        project = TimelineOps.Insert(project, blueSource, v2, 30, out var blueClip);
        Check(TimelineOps.Find(project, blueClip)!.Value.Track.Id == v2, "blue placed on the upper layer");
        project = TimelineOps.TrimEnd(project, blueClip, 60);
        Check(project.Duration == 90 && project.Width == 320 && project.Height == 180, "three-second 320x180 project");

        var plan = TimelineOps.ToPlan(project);
        var result = runner.RunAsync(ExportArguments.Build(plan, encoder, 90, output)).GetAwaiter().GetResult();
        Check(result.Succeeded, "layered export: " + result.ErrorText());
        var info = Ffprobe.ProbeAsync(output).GetAwaiter().GetResult();
        Check(info.Width == 320 && info.Height == 180 && info.FrameRate == FrameRate.Fps30 && info.HasAudio && Math.Abs(info.Duration.TotalSeconds - 3) < 0.08, "export is 3 s of 320x180 at 30 fps with sound: " + info);

        // Read all 90 frames back and check the layers switch on the exact frames.
        using (var reader = new FrameReader(runner, new FrameReaderOptions { Path = output, Width = 320, Height = 180, FrameRate = FrameRate.Fps30 }))
        {
            var frame = new byte[reader.FrameBytes];
            int count = 0;
            bool Red(int x) { int i = (90 * 320 + x) * 4; return frame[i + 2] > 170 && frame[i] < 90; }
            bool Blue(int x) { int i = (90 * 320 + x) * 4; return frame[i] > 170 && frame[i + 2] < 90; }
            while (reader.ReadFrameAsync(frame).AsTask().GetAwaiter().GetResult())
            {
                bool overlay = count >= 30 && count < 60;
                Check(overlay ? Blue(160) : Red(160), $"frame {count} centre is {(overlay ? "blue" : "red")}");
                Check(Red(10) && Red(310), $"frame {count} edges stay red beside the pillarboxed overlay");
                count++;
            }
            Check(count == 90, "export has exactly 90 frames, got " + count);
        }

        // The audio-only mix used for preview playback.
        var mix = runner.RunAsync(ExportArguments.BuildAudioMix(plan, wav)).GetAwaiter().GetResult();
        Check(mix.Succeeded, "audio mix: " + mix.ErrorText());
        var mixInfo = Ffprobe.ProbeAsync(wav).GetAwaiter().GetResult();
        Check(mixInfo.HasAudio && !mixInfo.HasVideo && Math.Abs(mixInfo.Duration.TotalSeconds - 3) < 0.05, "mix is 3 s of audio: " + mixInfo);

        // A proxy builds once and is found again by path.
        var cache = new ProxyCache(runner, Path.Combine(dir, "proxies"));
        if (Directory.Exists(cache.Folder)) Directory.Delete(cache.Folder, true);
        Check(cache.TryGet(red) == null, "no proxy before building");
        var proxy = cache.GetOrCreateAsync(Ffprobe.ProbeAsync(red).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        Check(proxy != null && cache.TryGet(red) == proxy && Ffprobe.ProbeAsync(proxy).GetAwaiter().GetResult() is { Height: 180, HasAudio: false }, "proxy built and cached");
        Console.WriteLine("PASS: live layered export (frame-exact overlay, 90 frames, sound), audio mix, proxy cache.");

        // Picture in picture: blue in the top-left corner for the middle second, a green still in
        // the bottom-right corner throughout, both at explicit boxes over the red video.
        string green = Path.Combine(dir, "green.png"), placedOut = Path.Combine(dir, "placed.mp4"), fancyOut = Path.Combine(dir, "fancy.mp4");
        made = runner.RunAsync(new List<string>(FfmpegRunner.StandardPrefix) { "-f", "lavfi", "-i", "color=c=0x00FF00:s=40x40", "-frames:v", "1", "-y", green }.ToArray()).GetAwaiter().GetResult();
        Check(made.Succeeded && File.Exists(green), "green test still: " + made.ErrorText());
        var stillSource = MediaSource.ForImage(green, 40, 40);
        var pip = TimelineOps.SetPlacement(project, blueClip, new Placement { X = 0, Y = 0, Width = 80, Height = 60 });
        pip = TimelineOps.InsertOnTop(pip, stillSource, 0, out var stillClip, new Placement { X = 240, Y = 120, Width = 80, Height = 60 });
        pip = TimelineOps.TrimEnd(pip, stillClip, 90);
        Check(pip.Duration == 90 && TimelineOps.Find(pip, stillClip)!.Value.Clip.Duration == 90, "the still is shortened to the three-second project");
        result = runner.RunAsync(ExportArguments.Build(TimelineOps.ToPlan(pip), encoder, 90, placedOut)).GetAwaiter().GetResult();
        Check(result.Succeeded, "picture-in-picture export: " + result.ErrorText());
        using (var reader = new FrameReader(runner, new FrameReaderOptions { Path = placedOut, Width = 320, Height = 180, FrameRate = FrameRate.Fps30 }))
        {
            var frame = new byte[reader.FrameBytes];
            int count = 0;
            string Colour(int x, int y) { int i = (y * 320 + x) * 4; byte b = frame[i], g = frame[i + 1], r = frame[i + 2]; return r > 170 && g < 90 && b < 90 ? "red" : b > 170 && r < 90 && g < 90 ? "blue" : g > 170 && r < 90 && b < 90 ? "green" : $"({r},{g},{b})"; }
            while (reader.ReadFrameAsync(frame).AsTask().GetAwaiter().GetResult())
            {
                bool overlay = count >= 30 && count < 60;
                string corner = Colour(40, 30), outside = Colour(90, 30), below = Colour(40, 70), middle = Colour(160, 90), still = Colour(280, 150), beside = Colour(230, 150);
                Check(corner == (overlay ? "blue" : "red"), $"frame {count}: top-left box is {corner}");
                Check(outside == "red" && below == "red" && middle == "red" && beside == "red", $"frame {count}: outside the boxes stays red, got {outside} {below} {middle} {beside}");
                Check(still == "green", $"frame {count}: the still's box is {still}");
                count++;
            }
            Check(count == 90, "picture-in-picture export has exactly 90 frames, got " + count);
        }

        // Turned, faded and cropped: ffmpeg accepts the graph, the length is unchanged, and the
        // half-transparent blue tints the red under it at the box's centre.
        var fancy = TimelineOps.SetPlacement(pip, blueClip, new Placement { X = 110, Y = 40, Width = 100, Height = 100, Rotation = 45, Opacity = 0.5, CropLeft = 0.1, CropRight = 0.1 });
        result = runner.RunAsync(ExportArguments.Build(TimelineOps.ToPlan(fancy, null, 640, 360), encoder, 90, fancyOut)).GetAwaiter().GetResult();
        Check(result.Succeeded, "turned, faded, cropped export at twice the size: " + result.ErrorText());
        var fancyInfo = Ffprobe.ProbeAsync(fancyOut).GetAwaiter().GetResult();
        Check(fancyInfo.Width == 640 && fancyInfo.Height == 360 && Math.Abs(fancyInfo.Duration.TotalSeconds - 3) < 0.08, "scaled export is 640x360 and 3 s: " + fancyInfo);
        using (var reader = new FrameReader(runner, new FrameReaderOptions { Path = fancyOut, Start = 1.5, Width = 320, Height = 180, FrameRate = FrameRate.Fps30, MaxFrames = 1 }))
        {
            var frame = new byte[reader.FrameBytes];
            Check(reader.ReadFrameAsync(frame).AsTask().GetAwaiter().GetResult(), "a frame from the scaled export");
            (int R, int B) At(int x, int y) { int i = (y * 320 + x) * 4; return (frame[i + 2], frame[i]); }
            var mixed = At(160, 90); var tip = At(160, 28); var clear = At(105, 35); var stillBox = At(280, 150);
            Check(mixed.R is > 80 and < 190 && mixed.B is > 80 and < 190, $"half-transparent blue over red is purple at the centre, got {mixed}");
            Check(tip.B > 60 && clear.R > 170 && clear.B < 90, $"the turned box reaches its top point and leaves its old corner clear, got {tip} and {clear}");
            Check(stillBox.R < 90 && stillBox.B < 90, $"the still scales with the output, got {stillBox}");
        }

        // Waveform: three seconds of sine is 300 loud buckets, cached on the second ask.
        var waves = new WaveformCache(runner, Path.Combine(dir, "waves"));
        if (Directory.Exists(waves.Folder)) Directory.Delete(waves.Folder, true);
        var wave = waves.GetOrCreateAsync(red).GetAwaiter().GetResult();
        Check(wave != null && Math.Abs(wave.Peaks.Length - 300) <= 12 && wave.Peaks.Skip(5).Take(280).All(peak => peak > 20) && Math.Abs(wave.BucketSeconds - 0.01) < 1e-9, "sine waveform: " + (wave == null ? "none" : wave.Peaks.Length + " buckets, lowest " + wave.Peaks.Skip(5).Take(280).Min()));
        Check(Directory.GetFiles(waves.Folder, "*.peaks").Length == 1 && waves.GetOrCreateAsync(red).GetAwaiter().GetResult()!.Peaks.SequenceEqual(wave!.Peaks), "waveform cached on disk");
        Check(waves.GetOrCreateAsync(blue).GetAwaiter().GetResult() == null, "a silent file has no waveform");
        // The gallery's ffmpeg thumbnail: a red frame no larger than the asked edge.
        var thumb = new FfmpegVideoThumbnailer().FromFfmpeg(red, 160);
        var thumbPixel = new byte[4];
        thumb?.CopyPixels(new Int32Rect(80, 45, 1, 1), thumbPixel, 4, 0);
        Check(thumb is { PixelWidth: 160, PixelHeight: 90 } && thumbPixel[2] > 170 && thumbPixel[0] < 90 && new FfmpegVideoThumbnailer().FromFfmpeg(green + ".missing", 160) == null, "ffmpeg gallery thumbnail");
        Console.WriteLine("PASS: live picture-in-picture export (placed video and still, frame by frame), turned/faded/cropped export at another size, waveforms, gallery thumbnail.");
    }
}
