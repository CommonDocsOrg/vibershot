using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Editing;
using Vibershot.Editing.Video;
using Vibershot.Media;
using Vibershot.UI;

/// <summary>
/// Checks for fades in the video editor: the ramp itself, clamping through trims and splits, the
/// project file, the timeline handles, the ffmpeg arguments, and (when ffmpeg is present) real
/// exports read back frame by frame and a real sound mix read back sample by sample.
/// </summary>
static class VideoFadeChecks
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
    static bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;

    static MediaSource Source(string path, double seconds, bool audio = true) =>
        new() { Path = path, Duration = seconds, Width = 1920, Height = 1080, FrameRate = "30", HasAudio = audio };

    static Clip ClipOf(VideoProject project, Guid id) => TimelineOps.Find(project, id)!.Value.Clip;

    public static void Run()
    {
        Model();
        Arguments();
        Gestures();
        PreviewDrawing();
        Console.WriteLine("PASS: video editor fades (ramp values, clamping on set/trim/split, linked clips, project file with and without the fields, export and audio mix arguments, timeline handles, preview opacity).");

        var probe = FfmpegLocator.Probe();
        if (!probe.Ok) { Console.WriteLine("SKIP: ffmpeg not found; live fade checks not run."); return; }
        var runner = new FfmpegRunner();
        var encoder = EncoderProbe.ResolveAsync(runner, true).GetAwaiter().GetResult();
        Check(encoder is not null, "an H.264 encoder works");
        string dir = Path.Combine(AppContext.BaseDirectory, "fadechecks");
        Directory.CreateDirectory(dir);
        Live(runner, encoder!, dir);
    }

    static void Model()
    {
        // The ramp: nothing on the first frame, full after the fade in; full until the fade out,
        // which would reach nothing on the frame after the clip.
        var clip = new VideoClip { Start = 10, Duration = 100, FadeIn = 10, FadeOut = 20 };
        Check(TimelineOps.FadeAt(clip, 9) == 0 && TimelineOps.FadeAt(clip, 10) == 0 && Near(TimelineOps.FadeAt(clip, 11), 0.1) && Near(TimelineOps.FadeAt(clip, 15), 0.5) && Near(TimelineOps.FadeAt(clip, 19), 0.9) && TimelineOps.FadeAt(clip, 20) == 1, "fade in climbs a tenth a frame from nothing");
        Check(TimelineOps.FadeAt(clip, 50) == 1 && TimelineOps.FadeAt(clip, 90) == 1 && Near(TimelineOps.FadeAt(clip, 91), 0.95) && Near(TimelineOps.FadeAt(clip, 100), 0.5) && Near(TimelineOps.FadeAt(clip, 109), 0.05) && TimelineOps.FadeAt(clip, 110) == 0, "fade out falls a twentieth a frame to nothing after the last");
        var plain = new AudioClip { Start = 5, Duration = 10 };
        Check(TimelineOps.FadeAt(plain, 5) == 1 && TimelineOps.FadeAt(plain, 14) == 1 && TimelineOps.FadeAt(plain, 4) == 0 && TimelineOps.FadeAt(plain, 15) == 0, "a clip without fades is full for exactly its span");
        var meeting = new ImageClip { Start = 0, Duration = 10, FadeIn = 5, FadeOut = 5 };
        Check(Near(TimelineOps.FadeAt(meeting, 4), 0.8) && TimelineOps.FadeAt(meeting, 5) == 1 && Near(TimelineOps.FadeAt(meeting, 6), 0.8), "fades that meet peak on one frame");
        Check(TimelineOps.CanFade(clip) && TimelineOps.CanFade(plain) && TimelineOps.CanFade(new GraphicClip { Item = new RectItem() }) && !TimelineOps.CanFade(new CaptionClip()), "everything but a caption can fade");

        // Setting: whole frames, linked picture and sound together, each fade stopping at the other.
        var project = TimelineOps.ForSource(Source(@"C:\media\a.mp4", 10));
        Guid videoId = project.Tracks[0].Clips[0].Id, soundId = project.Tracks[1].Clips[0].Id;
        Check(project.AllClips.All(c => c.FadeIn == 0 && c.FadeOut == 0), "new clips do not fade");
        var faded = TimelineOps.SetFade(project, videoId, fadeIn: 30);
        Check(ClipOf(faded, videoId) is { FadeIn: 30, FadeOut: 0 } && ClipOf(faded, soundId) is { FadeIn: 30, FadeOut: 0 }, "a fade set on the picture reaches its linked sound");
        faded = TimelineOps.SetFade(faded, soundId, fadeOut: 100);
        Check(ClipOf(faded, videoId) is { FadeIn: 30, FadeOut: 100 } && ClipOf(faded, soundId) is { FadeIn: 30, FadeOut: 100 }, "a fade set on the sound reaches its linked picture");
        Check(ClipOf(TimelineOps.SetFade(faded, videoId, fadeIn: 5000), videoId) is { FadeIn: 200, FadeOut: 100 }, "a fade in stops where the fade out begins");
        Check(ClipOf(TimelineOps.SetFade(faded, videoId, fadeOut: 5000), videoId) is { FadeIn: 30, FadeOut: 270 }, "a fade out stops where the fade in ends");
        Check(ClipOf(TimelineOps.SetFade(faded, videoId, 5000, 5000), videoId) is { FadeIn: 300, FadeOut: 0 }, "set together, the fade in is fitted first");
        Check(ClipOf(TimelineOps.SetFade(faded, videoId, fadeIn: -4), videoId).FadeIn == 0, "a fade is never negative");
        Check(ReferenceEquals(TimelineOps.SetFade(faded, videoId, fadeIn: 30), faded) && ReferenceEquals(TimelineOps.SetFade(faded, Guid.NewGuid(), fadeIn: 3), faded), "setting the same fade, or an unknown clip, changes nothing");
        var unlinked = TimelineOps.SetFade(TimelineOps.Unlink(faded, videoId), videoId, fadeIn: 0);
        Check(ClipOf(unlinked, videoId).FadeIn == 0 && ClipOf(unlinked, soundId).FadeIn == 30, "detached sound keeps its own fade");
        var captioned = TimelineOps.AddCaption(project, null, 0, "Hi", out var captionId);
        Check(ReferenceEquals(TimelineOps.SetFade(captioned, captionId, fadeIn: 10), captioned), "captions do not fade");

        // Trimming keeps fades inside the clip: the fade in first, the fade out with what is left.
        faded = TimelineOps.SetFade(project, videoId, 100, 100);
        Check(ClipOf(TimelineOps.TrimEnd(faded, videoId, 250), videoId) is { Duration: 250, FadeIn: 100, FadeOut: 100 }, "a trim that leaves room keeps both fades");
        var shorter = TimelineOps.TrimEnd(faded, videoId, 150);
        Check(ClipOf(shorter, videoId) is { Duration: 150, FadeIn: 100, FadeOut: 50 } && ClipOf(shorter, soundId) is { Duration: 150, FadeIn: 100, FadeOut: 50 }, "right trim shortens the fade out, for picture and sound");
        Check(ClipOf(TimelineOps.TrimEnd(faded, videoId, 60), videoId) is { Duration: 60, FadeIn: 60, FadeOut: 0 }, "a clip shorter than its fade in is all fade in");
        var later = TimelineOps.TrimStart(faded, videoId, 180);
        Check(ClipOf(later, videoId) is { Start: 180, Duration: 120, FadeIn: 100, FadeOut: 20 } && ClipOf(later, soundId) is { FadeIn: 100, FadeOut: 20 }, "left trim clamps the same way");
        Check(ClipOf(TimelineOps.TrimEnd(shorter, videoId, 300), videoId) is { Duration: 300, FadeIn: 100, FadeOut: 50 }, "stretching back does not bring a clamped fade back");

        // Splitting: the left part keeps the fade in, the right part the fade out.
        var split = TimelineOps.Split(faded, 150);
        var left = split.Tracks[0].Clips[0]; var right = split.Tracks[0].Clips[1];
        Check(left is { Duration: 150, FadeIn: 100, FadeOut: 0 } && right is { Duration: 150, FadeIn: 0, FadeOut: 100 }, "a split shares the fades between the halves");
        Check(split.Tracks[1].Clips[0] is { FadeIn: 100, FadeOut: 0 } && split.Tracks[1].Clips[1] is { FadeIn: 0, FadeOut: 100 }, "linked sound is split the same way");
        var early = TimelineOps.Split(faded, 40);
        Check(early.Tracks[0].Clips[0] is { Duration: 40, FadeIn: 40, FadeOut: 0 } && early.Tracks[0].Clips[1] is { Duration: 260, FadeIn: 0, FadeOut: 100 }, "a split inside the fade in clamps the left part");
        var late = TimelineOps.Split(faded, 250);
        Check(late.Tracks[0].Clips[0] is { Duration: 250, FadeIn: 100, FadeOut: 0 } && late.Tracks[0].Clips[1] is { Duration: 50, FadeIn: 0, FadeOut: 50 }, "a split inside the fade out clamps the right part");

        // Stills and graphics fade too, and moving a clip takes its fades along.
        var logo = MediaSource.ForImage(@"C:\media\logo.png", 400, 200);
        var layered = TimelineOps.InsertOnTop(faded, logo, 30, out var logoId);
        layered = TimelineOps.InsertGraphic(layered, new RectItem { Bounds = new Rect(100, 100, 200, 100), Fill = Colors.Lime }, null, 30, out var boxId);
        layered = TimelineOps.SetFade(TimelineOps.SetFade(layered, logoId, 15, 500), boxId, fadeOut: 45);
        Check(ClipOf(layered, logoId) is ImageClip { Duration: 150, FadeIn: 15, FadeOut: 135 } && ClipOf(layered, boxId) is GraphicClip { FadeIn: 0, FadeOut: 45 }, "a still and a graphic take fades");
        var movedLogo = TimelineOps.Move(layered, logoId, TimelineOps.Find(layered, logoId)!.Value.Track.Id, 90)!;
        Check(ClipOf(movedLogo, logoId) is { Start: 90, FadeIn: 15, FadeOut: 135 } && Near(TimelineOps.FadeAt(ClipOf(movedLogo, logoId), 93), 0.2), "a moved clip keeps its fades");

        // Project file: fades come back; a project without them writes no fade fields at all, and
        // a file written before fades existed loads with none.
        string json = DocumentJson.Serialize(layered);
        var back = DocumentJson.Deserialize<VideoProject>(json)!;
        Check(ClipOf(back, videoId) is VideoClip { FadeIn: 100, FadeOut: 100 } && ClipOf(back, soundId) is AudioClip { FadeIn: 100, FadeOut: 100 } && ClipOf(back, logoId) is ImageClip { FadeIn: 15, FadeOut: 135 } && ClipOf(back, boxId) is GraphicClip { FadeIn: 0, FadeOut: 45 }, "fades survive the project file");
        string old = DocumentJson.Serialize(captioned);
        Check(!old.Contains("fade", StringComparison.OrdinalIgnoreCase), "a project without fades is written exactly as before");
        var fromOld = DocumentJson.Deserialize<VideoProject>(old)!;
        Check(fromOld.AllClips.Count() == 3 && fromOld.AllClips.All(c => c.FadeIn == 0 && c.FadeOut == 0) && DocumentJson.Serialize(fromOld) == old, "a file from before fades loads with none and saves unchanged");
        string folder = Path.Combine(AppContext.BaseDirectory, "fadechecks", "project");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "fades" + VideoProjectFile.Extension);
        VideoProjectFile.Save(path, layered);
        var loaded = VideoProjectFile.Load(path);
        Check(ClipOf(loaded, videoId) is { FadeIn: 100, FadeOut: 100 } && ClipOf(loaded, boxId) is { FadeOut: 45 }, "fades survive a saved .vibevideo");
        string oldPath = Path.Combine(folder, "old" + VideoProjectFile.Extension);
        File.WriteAllText(oldPath, old);
        Check(VideoProjectFile.Load(oldPath).AllClips.All(c => c.FadeIn == 0 && c.FadeOut == 0), "an old .vibevideo loads without fades");

        // The plan carries fades in seconds, for pictures, stills, sounds and (with a folder) graphics.
        var plan = TimelineOps.ToPlan(layered);
        Check(plan.Video.Count == 2 && Near(plan.Video[0].FadeIn, 100 / 30.0) && Near(plan.Video[0].FadeOut, 100 / 30.0) && plan.Video[1].IsImage && Near(plan.Video[1].FadeIn, 0.5) && Near(plan.Video[1].FadeOut, 4.5), "the plan gives picture fades in seconds");
        Check(plan.Audio.Count == 1 && Near(plan.Audio[0].FadeIn, 100 / 30.0) && Near(plan.Audio[0].FadeOut, 100 / 30.0), "the plan gives sound fades in seconds");
        var drawn = TimelineOps.ToPlan(layered, null, null, null, Path.Combine(folder, "pngs"));
        Check(drawn.Video.Count == 3 && drawn.Video[2].IsImage && drawn.Video[2].FadeIn == 0 && Near(drawn.Video[2].FadeOut, 1.5), "a graphic's fade reaches its PNG in the plan");
        var unfaded = TimelineOps.ToPlan(project);
        Check(unfaded.Video[0].FadeIn == 0 && unfaded.Video[0].FadeOut == 0 && unfaded.Audio[0].FadeIn == 0 && unfaded.Audio[0].FadeOut == 0, "no fades, none in the plan");
    }

    static void Arguments()
    {
        var plan = new ExportPlan
        {
            Width = 1280, Height = 720, FrameRate = FrameRate.Fps30, Duration = 4, Background = "0x000000",
            Video = new[]
            {
                new ExportVideoClip(@"C:\a.mp4", 1.5, 2, 0.5, 0, 0, 1280, 720) { FadeIn = 0.5, FadeOut = 1 },
                new ExportVideoClip(@"C:\b.png", 0, 1, 2.5, 900, 500, 320, 180) { Rotation = 90, Opacity = 0.5, IsImage = true, FadeOut = 0.25 },
                new ExportVideoClip(@"C:\c.png", 0, 1, 0, 10, 10, 100, 50) { IsImage = true, FadeIn = 1 / 30.0 },
            },
            Audio = new[] { new ExportAudioClip(@"C:\a.mp4", 1.5, 2, 0.5, 0.5) { FadeIn = 0.5, FadeOut = 1 }, new ExportAudioClip(@"C:\m.mp3", 0, 4, 0) { FadeOut = 2.5 } },
        };
        // The chain is put in an alpha format, shifted to its timeline position, and only then
        // faded, so the fade times are timeline times.
        string video = "color=c=0x000000:s=1280x720:r=30:d=4,format=yuv420p[bg]"
            + ";[0:v]fps=30,scale=1280:720:flags=lanczos,setsar=1,format=rgba,setpts=PTS-STARTPTS+0.5/TB,fade=t=in:st=0.5:d=0.5:alpha=1,fade=t=out:st=1.5:d=1:alpha=1[v0]"
            + ";[bg][v0]overlay=x=640-w/2:y=360-h/2:eof_action=pass:enable='gte(t,0.5)*lt(t,2.5)'[o0]"
            + ";[1:v]fps=30,scale=320:180:flags=lanczos,setsar=1,format=rgba,colorchannelmixer=aa=0.5,rotate=1.570796:ow=rotw(1.570796):oh=roth(1.570796):c=none,setpts=PTS-STARTPTS+2.5/TB,fade=t=out:st=3.25:d=0.25:alpha=1[v1]"
            + ";[o0][v1]overlay=x=1060-w/2:y=590-h/2:eof_action=pass:enable='gte(t,2.5)*lt(t,3.5)'[o1]"
            + ";[2:v]fps=30,scale=100:50:flags=lanczos,setsar=1,format=rgba,setpts=PTS-STARTPTS+0/TB,fade=t=in:st=0:d=0.033333:alpha=1[v2]"
            + ";[o1][v2]overlay=x=60-w/2:y=35-h/2:eof_action=pass:enable='gte(t,0)*lt(t,1)'[o2]"
            + ";[o2]format=yuv420p[vout]";
        string Sound(int first) =>
            $"[{first}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.5,afade=t=in:st=0:d=0.5,afade=t=out:st=1:d=1,adelay=500:all=1[a{first}]"
            + $";[{first + 1}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=1,afade=t=out:st=1.5:d=2.5,adelay=0:all=1[a{first + 1}]"
            + $";[a{first}][a{first + 1}]amix=inputs=2:normalize=0:duration=longest,apad,atrim=0:4[aout]";
        string[] built = ExportArguments.Build(plan, EncoderProbe.Hardware[0], 75, @"C:\out.mp4");
        string graph = built[Array.IndexOf(built, "-filter_complex") + 1];
        Check(graph == video + ";" + Sound(3), "export graph with fades:\n expected: " + video + ";" + Sound(3) + "\n actual:   " + graph);
        string[] mix = ExportArguments.BuildAudioMix(plan, @"C:\mix.wav");
        string mixGraph = mix[Array.IndexOf(mix, "-filter_complex") + 1];
        Check(mixGraph == Sound(0), "audio mix graph with fades:\n expected: " + Sound(0) + "\n actual:   " + mixGraph);
    }

    static void Gestures()
    {
        var a = Source(@"C:\media\a.mp4", 10); var b = Source(@"C:\media\b.mp4", 4, audio: false);
        var history = new History<VideoProject>(TimelineOps.AddCaption(TimelineOps.Append(TimelineOps.ForSource(a), b), null, 0, "Words", out var captionId));
        var view = new TimelineView { History = history };
        view.Measure(new Size(1440, 300)); view.Arrange(new Rect(0, 0, 1440, 300));
        view.SetZoom(100); view.ScrollX = 0;
        // 100 px per second at 30 fps. The caption lane is on top (y 24..58), V1 under it
        // (58..110, clips drawn 62..106) and A1 last (110..150, clips 114..146). Clip A covers
        // x 148..1148 and clip B 1148..1548.
        const double h = TimelineView.HeaderWidth, v1 = 90;
        var clipA = history.Current.Tracks[0].Clips[0]; var clipB = history.Current.Tracks[0].Clips[1];
        var soundA = history.Current.AllClips.OfType<AudioClip>().Single();
        Clip Now(Guid id) => ClipOf(history.Current, id);

        // Handles belong to selected clips that can fade.
        Check(view.FadeHandlePoint(clipA.Id, false) == null && view.FadeHandlePoint(clipA.Id, true) == null, "an unselected clip has no fade handles");
        view.PointerDown(new Point(h + 400, v1)); view.PointerUp();
        Check(view.Selection.SetEquals(new[] { clipA.Id, soundA.Id }), "click selects the clip and its sound");
        var fadeIn = view.FadeHandlePoint(clipA.Id, false)!.Value; var fadeOut = view.FadeHandlePoint(clipA.Id, true)!.Value;
        Check(fadeIn == new Point(h + TimelineView.FadeInset, 62 + TimelineView.FadeInset) && fadeOut == new Point(h + 1000 - TimelineView.FadeInset, 62 + TimelineView.FadeInset), $"handles rest just inside the top corners: {fadeIn} and {fadeOut}");
        Check(view.FadeHandlePoint(soundA.Id, false) == new Point(h + TimelineView.FadeInset, 114 + TimelineView.FadeInset), "the linked sound has handles too");
        Check(view.FadeHandlePoint(clipB.Id, false) == null, "the clip beside it has none");

        // Dragging the left handle right by 100 px is a 30-frame fade in, on picture and sound, in one undo step.
        int depth = history.UndoDepth;
        Check(view.PointerDown(fadeIn), "a press on the handle starts a drag");
        for (int dx = 10; dx <= 100; dx += 10) view.PointerMove(new Point(fadeIn.X + dx, fadeIn.Y + 3));
        view.PointerUp();
        Check(Now(clipA.Id) is { FadeIn: 30, FadeOut: 0, Start: 0, Duration: 300 } && Now(soundA.Id) is { FadeIn: 30, FadeOut: 0 }, $"fade in dragged to one second on both, got {Now(clipA.Id).FadeIn} and {Now(soundA.Id).FadeIn}");
        Check(history.UndoDepth == depth + 1, "the whole drag is one undo step");
        Check(view.FadeHandlePoint(clipA.Id, false) == new Point(h + 100, 62 + TimelineView.FadeInset), "the handle sits where the fade in ends");

        // Snapped to whole frames: 52 px is 15.6 frames, so 16.
        view.PointerDown(view.FadeHandlePoint(clipA.Id, true)!.Value); view.PointerMove(new Point(fadeOut.X - 30, fadeOut.Y)); view.PointerMove(new Point(fadeOut.X - 52, fadeOut.Y)); view.PointerUp();
        Check(Now(clipA.Id) is { FadeIn: 30, FadeOut: 16 } && Now(soundA.Id) is { FadeIn: 30, FadeOut: 16 }, "fade out dragged left lands on a whole frame: " + Now(clipA.Id).FadeOut);
        Check(history.UndoDepth == depth + 2, "a second drag is a second step");

        // The sound's own handle drives both as well; dragging far past the other fade stops at it.
        var soundOut = view.FadeHandlePoint(soundA.Id, true)!.Value;
        Check(Near(soundOut.X, h + 1000 - 16 * 100 / 30.0, 1e-6), "the sound's handle follows the shared fade");
        view.PointerDown(soundOut); view.PointerMove(new Point(soundOut.X - 400, soundOut.Y)); view.PointerMove(new Point(soundOut.X - 2000, soundOut.Y)); view.PointerUp();
        Check(Now(clipA.Id) is { FadeIn: 30, FadeOut: 270 } && Now(soundA.Id) is { FadeIn: 30, FadeOut: 270 }, "a fade out cannot pass the fade in: " + Now(clipA.Id).FadeOut);
        history.Undo();
        Check(Now(clipA.Id).FadeOut == 16 && Now(soundA.Id).FadeOut == 16, "undo takes back the whole drag");
        history.Undo(); history.Undo();
        Check(Now(clipA.Id) is { FadeIn: 0, FadeOut: 0 } && history.UndoDepth == depth, "three undos, three drags");
        history.Redo();

        // Dragging a handle back to the corner removes the fade; a small wobble does nothing.
        var at = view.FadeHandlePoint(clipA.Id, false)!.Value;
        view.PointerDown(at); view.PointerMove(new Point(at.X - 60, at.Y)); view.PointerMove(new Point(at.X - 500, at.Y)); view.PointerUp();
        Check(Now(clipA.Id).FadeIn == 0, "dragged back past the corner the fade is gone");
        int before = history.UndoDepth;
        at = view.FadeHandlePoint(clipA.Id, false)!.Value;
        view.PointerDown(at); view.PointerMove(new Point(at.X + 2, at.Y)); view.PointerUp();
        Check(Now(clipA.Id).FadeIn == 0 && history.UndoDepth == before, "a two-pixel wobble is not a drag");

        // Below the handle the same edge still trims, and the body still moves.
        view.PointerDown(new Point(h + 997, v1)); view.PointerMove(new Point(h + 950, v1)); view.PointerMove(new Point(h + 900, v1)); view.PointerUp();
        Check(Now(clipA.Id) is { Duration: 270, FadeOut: 0 }, "the edge under the handle still trims: " + Now(clipA.Id).Duration);
        history.Undo();

        // An unlinked second clip fades alone; trimming it afterwards clamps the fade.
        view.PointerDown(new Point(h + 1300, v1)); view.PointerUp();
        var handleB = view.FadeHandlePoint(clipB.Id, true)!.Value;
        view.PointerDown(handleB); view.PointerMove(new Point(handleB.X - 100, handleB.Y)); view.PointerMove(new Point(handleB.X - 300, handleB.Y)); view.PointerUp();
        Check(Now(clipB.Id) is { FadeIn: 0, FadeOut: 90 } && Now(clipA.Id).FadeOut == 0 && Now(soundA.Id).FadeOut == 0, "the second clip fades on its own: " + Now(clipB.Id).FadeOut);
        view.PointerDown(new Point(h + 1397, v1)); view.PointerMove(new Point(h + 1300, v1)); view.PointerMove(new Point(h + 1200, v1)); view.PointerUp();
        Check(Now(clipB.Id) is { Duration: 60, FadeOut: 60 }, "trimming a faded clip on the timeline clamps its fade: " + Now(clipB.Id).Duration + " / " + Now(clipB.Id).FadeOut);

        // Captions and locked lanes have no handles.
        view.PointerDown(new Point(h + 150, 40)); view.PointerUp();
        Check(view.Selection.SetEquals(new[] { captionId }) && view.FadeHandlePoint(captionId, false) == null, "a caption has no fade handles");
        view.PointerDown(new Point(h + 400, v1)); view.PointerUp();
        history.Push(TimelineOps.SetTrack(history.Current, history.Current.Tracks[0] with { Locked = true }));
        Check(view.FadeHandlePoint(clipA.Id, false) == null && view.FadeHandlePoint(soundA.Id, false) != null, "a locked lane's clips have no fade handles");
    }

    /// <summary>The preview draws a fading graphic fainter: rendered off screen at three frames and read back.</summary>
    static void PreviewDrawing()
    {
        var project = TimelineOps.Empty(320, 180) with { Background = Colors.Black };
        project = TimelineOps.InsertGraphic(project, new RectItem { Bounds = new Rect(0, 0, 320, 180), Fill = Colors.White }, null, 0, out var boxId);
        project = TimelineOps.SetFade(TimelineOps.TrimEnd(project, boxId, 40), boxId, 10, 20);
        // 352 x 212 leaves the 320 x 180 picture at (16, 16), one screen pixel per canvas pixel.
        var view = new VideoPreview();
        view.Measure(new Size(352, 212)); view.Arrange(new Rect(0, 0, 352, 212));
        int Level(long frame)
        {
            view.Show(project, frame, false);
            view.InvalidateVisual();
            view.Arrange(new Rect(0, 0, 352, 212)); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(352, 212, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(176, 106, 1, 1), pixel, 4, 0);
            return pixel[1];
        }
        int first = Level(0), half = Level(5), full = Level(15), fading = Level(30), last = Level(39);
        Check(first <= 2 && Math.Abs(half - 128) <= 3 && full >= 253 && Math.Abs(fading - 128) <= 3 && Math.Abs(last - 13) <= 3, $"the preview draws the ramp: {first}, {half}, {full}, {fading}, {last}");
    }

    /// <summary>Signed 16-bit samples of a WAV file's first channel, scaled to -1..1, and its sample rate.</summary>
    static (double[] Samples, int Rate) ReadWav(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Check(bytes.Length > 44 && bytes[0] == 'R' && bytes[8] == 'W', "a WAV file");
        int rate = 0, channels = 0, at = 12;
        while (at + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            long size = BitConverter.ToUInt32(bytes, at + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(bytes, at + 10); rate = BitConverter.ToInt32(bytes, at + 12); }
            if (id == "data")
            {
                // ffmpeg writing to a pipe-like target may leave the size open; take what is there.
                int length = (int)Math.Min(size, bytes.Length - at - 8), frames = length / (2 * channels);
                var samples = new double[frames];
                for (int i = 0; i < frames; i++) samples[i] = BitConverter.ToInt16(bytes, at + 8 + i * 2 * channels) / 32768.0;
                return (samples, rate);
            }
            at += 8 + (int)size + (int)(size & 1);
        }
        throw new Exception("FAIL: no data in " + path);
    }

    static void Live(FfmpegRunner runner, EncoderInfo encoder, string dir)
    {
        string white = Path.Combine(dir, "white.mp4"), red = Path.Combine(dir, "red.mp4"), blue = Path.Combine(dir, "blue.png");
        var options = new RecordingOptions { Region = new PixelRect(0, 0, 320, 180), FrameRate = FrameRate.Fps30, Quality = 90, Encoder = encoder, OutputPath = white };
        string[] Make(string color, bool audio, string path)
        {
            var args = new List<string>(FfmpegRunner.StandardPrefix) { "-f", "lavfi", "-i", $"color=c={color}:s=320x180:r=30:d=3" };
            if (audio) args.AddRange(new[] { "-f", "lavfi", "-i", "sine=frequency=440:duration=3" });
            args.AddRange(RecordingArguments.EncoderArguments(options));
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            if (audio) args.AddRange(new[] { "-c:a", "aac" });
            args.AddRange(new[] { "-f", "mp4", "-y", path });
            return args.ToArray();
        }
        var made = runner.RunAsync(Make("white", true, white)).GetAwaiter().GetResult(); Check(made.Succeeded, "white test clip: " + made.ErrorText());
        made = runner.RunAsync(Make("red", false, red)).GetAwaiter().GetResult(); Check(made.Succeeded, "red test clip: " + made.ErrorText());
        made = runner.RunAsync(new List<string>(FfmpegRunner.StandardPrefix) { "-f", "lavfi", "-i", "color=c=0x0000FF:s=40x40", "-frames:v", "1", "-y", blue }.ToArray()).GetAwaiter().GetResult();
        Check(made.Succeeded && File.Exists(blue), "blue test still: " + made.ErrorText());
        var whiteSource = MediaSource.From(Ffprobe.ProbeAsync(white).GetAwaiter().GetResult());
        var redSource = MediaSource.From(Ffprobe.ProbeAsync(red).GetAwaiter().GetResult());
        Check(whiteSource.HasAudio && !redSource.HasAudio && whiteSource.Width == 320, "probed test clips");

        // One: a white clip with a tone, starting a second into a black canvas, fading in over a
        // second and out over a second. Every frame's brightness must sit on the ramp FadeAt
        // gives, which is what the preview draws.
        var project = TimelineOps.ForSource(whiteSource) with { Background = Colors.Black };
        Guid whiteId = project.Tracks[0].Clips[0].Id;
        project = TimelineOps.Move(project, whiteId, project.Tracks[0].Id, 30)!;
        project = TimelineOps.SetFade(project, whiteId, 30, 30);
        Check(project.Duration == 120 && project.AllClips.All(c => c is { Start: 30, Duration: 90, FadeIn: 30, FadeOut: 30 }), "a four-second project: black, then the faded clip and its sound");
        string output = Path.Combine(dir, "fade.mp4"), wav = Path.Combine(dir, "fade.wav");
        var plan = TimelineOps.ToPlan(project);
        var result = runner.RunAsync(ExportArguments.Build(plan, encoder, 90, output)).GetAwaiter().GetResult();
        Check(result.Succeeded, "faded export: " + result.ErrorText());
        var levels = new List<int>();
        using (var reader = new FrameReader(runner, new FrameReaderOptions { Path = output, Width = 320, Height = 180, FrameRate = FrameRate.Fps30 }))
        {
            var frame = new byte[reader.FrameBytes];
            while (reader.ReadFrameAsync(frame).AsTask().GetAwaiter().GetResult())
            {
                int i = (90 * 320 + 160) * 4, corner = (10 * 320 + 10) * 4;
                Check(Math.Abs(frame[i] - frame[i + 1]) <= 4 && Math.Abs(frame[i + 2] - frame[i + 1]) <= 4 && Math.Abs(frame[corner + 1] - frame[i + 1]) <= 4, $"frame {levels.Count} is an even grey");
                levels.Add(frame[i + 1]);
            }
        }
        Check(levels.Count == 120, "faded export has exactly 120 frames, got " + levels.Count);
        var clip = ClipOf(project, whiteId);
        int black = levels[0], full = levels[75];
        Check(black <= 6 && full >= 249, $"black before the clip and white on the plateau: {black} and {full}");
        string seen = string.Join(" ", levels.Select((level, index) => $"{index}:{level}"));
        for (int f = 0; f < 120; f++)
        {
            double expected = black + (full - black) * TimelineOps.FadeAt(clip, f);
            Check(Math.Abs(levels[f] - expected) <= 4, $"frame {f} should be {expected:0.#} (fade {TimelineOps.FadeAt(clip, f):0.###}), got {levels[f]}\n {seen}");
        }
        // The named frames: the clip's first is still black, mid-fade is mid-grey, the plateau is
        // white from the frame the fade in ends to the frame the fade out begins, and the last
        // frame is a thirtieth of the way up.
        Check(levels[29] <= 6 && levels[30] <= 6 && Math.Abs(levels[45] - 128) <= 6 && levels[60] >= 249 && levels[90] >= 249 && Math.Abs(levels[105] - 128) <= 6 && Math.Abs(levels[119] - 8.5) <= 5 && levels[59] < levels[60] - 4 && levels[91] < levels[90] - 4,
            $"first frame, mid-fade, plateau and last frame: {levels[30]}, {levels[45]}, {levels[60]}..{levels[90]}, {levels[105]}, {levels[119]}");
        Console.WriteLine("PASS: live faded export (a white clip over black, every one of 120 frames on the fade ramp).");

        // The sound: silence for a second, a one-second ramp up, a second at full, a one-second ramp down.
        var mix = runner.RunAsync(ExportArguments.BuildAudioMix(plan, wav)).GetAwaiter().GetResult();
        Check(mix.Succeeded, "faded audio mix: " + mix.ErrorText());
        var (samples, rate) = ReadWav(wav);
        Check(rate == 48000 && Math.Abs(samples.Length - 4 * 48000) <= 480, $"the mix is four seconds at 48 kHz: {samples.Length} samples at {rate}");
        double Peak(double from, double to) { double peak = 0; for (int i = (int)(from * rate); i < Math.Min(samples.Length, (int)(to * rate)); i++) peak = Math.Max(peak, Math.Abs(samples[i])); return peak; }
        double loud = Peak(2.3, 2.7);
        string heard = $"silence {Peak(0.1, 0.9):0.####}, start {Peak(1.0, 1.02):0.####}, quarter {Peak(1.24, 1.26):0.####}, half {Peak(1.49, 1.51):0.####}, full {loud:0.####}, half {Peak(3.49, 3.51):0.####}, quarter {Peak(3.74, 3.76):0.####}, end {Peak(3.98, 4.0):0.####}";
        Check(loud > 0.05, "the tone is audible on the plateau: " + heard);
        Check(Peak(0.1, 0.9) < loud * 0.01 && Peak(1.0, 1.02) < loud * 0.06, "silent before the clip and at the start of the fade in: " + heard);
        Check(Near(Peak(1.24, 1.26) / loud, 0.25, 0.05) && Near(Peak(1.49, 1.51) / loud, 0.5, 0.05) && Near(Peak(1.74, 1.76) / loud, 0.75, 0.05), "the fade in climbs evenly: " + heard);
        Check(Near(Peak(2.02, 2.2) / loud, 1, 0.03) && Near(Peak(2.8, 2.98) / loud, 1, 0.03), "full between the fades: " + heard);
        Check(Near(Peak(3.24, 3.26) / loud, 0.75, 0.05) && Near(Peak(3.49, 3.51) / loud, 0.5, 0.05) && Near(Peak(3.74, 3.76) / loud, 0.25, 0.05) && Peak(3.98, 4.0) < loud * 0.06, "the fade out falls evenly to silence: " + heard);
        // The exported file's own sound follows the same ramp.
        string exportedWav = Path.Combine(dir, "fade-exported.wav");
        made = runner.RunAsync(new List<string>(FfmpegRunner.StandardPrefix) { "-i", output, "-vn", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "wav", "-y", exportedWav }.ToArray()).GetAwaiter().GetResult();
        Check(made.Succeeded, "sound from the export: " + made.ErrorText());
        (samples, rate) = ReadWav(exportedWav);
        double exportedLoud = Peak(2.3, 2.7);
        Check(exportedLoud > 0.05 && Peak(0.1, 0.9) < exportedLoud * 0.03 && Near(Peak(1.49, 1.51) / exportedLoud, 0.5, 0.08) && Near(Peak(3.49, 3.51) / exportedLoud, 0.5, 0.08),
            $"the export's sound fades too: silence {Peak(0.1, 0.9):0.####}, half {Peak(1.49, 1.51):0.####}, full {exportedLoud:0.####}, half {Peak(3.49, 3.51):0.####}");
        Console.WriteLine("PASS: live faded sound (preview mix and export: silence, even ramp up, plateau, even ramp down).");

        // Two: cross-fades over a red video. A trimmed white video in a corner box (so its source
        // and timeline times both differ from zero), a blue still fading out, and a lime graphic
        // fading in and out. Each sampled point must be the mix of red and its own colour that
        // FadeAt gives on that frame.
        var layered = TimelineOps.ForSource(redSource);
        layered = TimelineOps.InsertOnTop(layered, whiteSource, 0, out var boxedId, new Placement { X = 0, Y = 0, Width = 80, Height = 60 });
        layered = TimelineOps.Delete(TimelineOps.Unlink(layered, boxedId), layered.AllClips.OfType<AudioClip>().Select(c => c.Id));
        layered = TimelineOps.SetFade(TimelineOps.TrimStart(layered, boxedId, 15), boxedId, 15, 15);
        layered = TimelineOps.InsertOnTop(layered, MediaSource.ForImage(blue, 40, 40), 0, out var stillId, new Placement { X = 240, Y = 120, Width = 80, Height = 60 });
        layered = TimelineOps.SetFade(TimelineOps.TrimEnd(layered, stillId, 60), stillId, fadeOut: 30);
        layered = TimelineOps.InsertGraphic(layered, new RectItem { Bounds = new Rect(120, 60, 80, 60), Fill = Colors.Lime }, null, 30, out var limeId);
        layered = TimelineOps.SetFade(TimelineOps.TrimEnd(layered, limeId, 90), limeId, 20, 10);
        Check(layered.Duration == 90 && ClipOf(layered, boxedId) is { Start: 15, Duration: 75, FadeIn: 15, FadeOut: 15 } boxed && Near(boxed.SourceIn, 0.5)
            && ClipOf(layered, stillId) is { Start: 0, Duration: 60, FadeOut: 30 } && ClipOf(layered, limeId) is { Start: 30, Duration: 60, FadeIn: 20, FadeOut: 10 }, "three fading layers over three seconds of red");
        string crossOut = Path.Combine(dir, "cross.mp4"), pngs = Path.Combine(dir, "cross-pngs");
        if (Directory.Exists(pngs)) Directory.Delete(pngs, true);
        var crossPlan = TimelineOps.ToPlan(layered, null, null, null, pngs);
        Check(crossPlan.Video.Count == 4 && crossPlan.Video.Count(v => v.IsImage) == 2, "the plan has the video, the boxed video, the still and the graphic");
        result = runner.RunAsync(ExportArguments.Build(crossPlan, encoder, 90, crossOut)).GetAwaiter().GetResult();
        Check(result.Succeeded, "cross-fade export: " + result.ErrorText());
        var layers = new (Guid Id, int X, int Y, double R, double G, double B)[] { (boxedId, 40, 30, 255, 255, 255), (stillId, 280, 150, 0, 0, 255), (limeId, 160, 90, 0, 255, 0) };
        using (var reader = new FrameReader(runner, new FrameReaderOptions { Path = crossOut, Width = 320, Height = 180, FrameRate = FrameRate.Fps30 }))
        {
            var frame = new byte[reader.FrameBytes];
            int count = 0;
            (int R, int G, int B) At(int x, int y) { int i = (y * 320 + x) * 4; return (frame[i + 2], frame[i + 1], frame[i]); }
            var redSeen = (R: 255, G: 0, B: 0);
            while (reader.ReadFrameAsync(frame).AsTask().GetAwaiter().GetResult())
            {
                var plainRed = At(160, 20);
                Check(plainRed.R > 200 && plainRed.G < 40 && plainRed.B < 40 && Math.Abs(At(100, 150).R - plainRed.R) <= 4 && At(100, 150).G < 40, $"frame {count}: outside the boxes stays red, got {plainRed}");
                if (count == 0) redSeen = plainRed;
                foreach (var layer in layers)
                {
                    double fade = TimelineOps.FadeAt(ClipOf(layered, layer.Id), count);
                    var got = At(layer.X, layer.Y);
                    double r = redSeen.R + (layer.R - redSeen.R) * fade, g = redSeen.G + (layer.G - redSeen.G) * fade, b = redSeen.B + (layer.B - redSeen.B) * fade;
                    Check(Math.Abs(got.R - r) <= Tolerance && Math.Abs(got.G - g) <= Tolerance && Math.Abs(got.B - b) <= Tolerance, $"frame {count}: the layer at {layer.X},{layer.Y} should be ({r:0},{g:0},{b:0}) at fade {fade:0.###}, got {got}");
                }
                count++;
            }
            Check(count == 90, "cross-fade export has exactly 90 frames, got " + count);
        }
        Console.WriteLine("PASS: live cross-fades over a video (a trimmed, boxed video, a still and a graphic, each on its ramp for all 90 frames).");
    }

    /// <summary>How far a colour channel of a cross-faded pixel may sit from the straight mix, after chroma subsampling and encoding.</summary>
    const double Tolerance = 12;
}
