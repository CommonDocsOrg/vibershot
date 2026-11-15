using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Vibershot.Editing;
using Vibershot.Editing.Video;
using Vibershot.Media;
using Vibershot.Media.Audio;
using Vibershot.Media.Camera;

/// <summary>
/// Checks for recording sound and a webcam beside the screen: device list parsing, the ffmpeg
/// arguments, how a session becomes an editor project, frame shapes in the export, and (with
/// <c>--record-live</c>, at the desk) a real recording of every track.
/// </summary>
static class RecordingChecks
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

    public static void Run()
    {
        Devices();
        Arguments();
        Project();
        Console.WriteLine("PASS: recording (camera list parsing, camera/frame-clock/AAC arguments, session to project alignment, frame mask and mirror in the export graph, project file).");

        var probe = FfmpegLocator.Probe();
        if (!probe.Ok) { Console.WriteLine("SKIP: ffmpeg not found; live frame mask check not run."); return; }
        MaskedExport();
        Console.WriteLine("PASS: live export of a circle-framed clip (centre shows the clip, corners show the layer below).");
    }

    static void Devices()
    {
        // A real listing, with the noise camera drivers write into the same stream.
        string[] lines =
        {
            "[in#0 @ 0000025301e31140] \"USB Video Device\" (video)",
            "[in#0 @ 0000025301e31140]   Alternative name \"@device_pnp_\\\\?\\usb#vid_1532&pid_0e03&mi_00#9&2a0522c7&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\\global\"",
            "I2026-11-14 00:06:24.278517 (55172) [INFO] [VCAMDS] ffmpeg.exe",
            "[in#0 @ 0000025301e31140] \"Camera (NVIDIA Broadcast)\" (video)",
            "[in#0 @ 0000025301e31140]   Alternative name \"@device_sw_{860BB310-5D01-11D0-BD3B-00A0C911CE86}\\{7BBFF097-B3FB-4B26-B685-7A998DE7CEAC}\"",
            "[in#0 @ 0000025301e31140] \"OBS Virtual Camera\" (none)",
            "[in#0 @ 0000025301e31140]   Alternative name \"@device_sw_{860BB310-5D01-11D0-BD3B-00A0C911CE86}\\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}\"",
            "[in#0 @ 0000025301e31140] \"Microphone (2- Razer Kiyo)\" (audio)",
            "[in#0 @ 0000025301e31140]   Alternative name \"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{305D4194-573A-41B2-B159-8084465C7A08}\"",
            "Error opening input file dummy.",
        };
        var cameras = CameraDevices.Parse(lines);
        Check(cameras.Count == 2, "only video devices are cameras");
        Check(cameras[0].Name == "USB Video Device" && cameras[0].AlternativeName!.StartsWith("@device_pnp_") && cameras[0].InputName == cameras[0].AlternativeName, "a camera carries its alternative name");
        Check(cameras[1].Name == "Camera (NVIDIA Broadcast)" && cameras[1].AlternativeName!.Contains("7BBFF097"), "the alternative name belongs to the device above it, not to a later one");
        Check(new CameraDeviceInfo("Cam", null).InputName == "Cam", "a camera without an alternative name is opened by name");
    }

    static void Arguments()
    {
        var encoder = new EncoderInfo("libopenh264", "OpenH264", false);
        var camera = new CameraDeviceInfo("Cam", "@alt");
        string[] asked = CameraSession.Arguments(camera, "out.mp4", encoder, askForMode: true), plain = CameraSession.Arguments(camera, "out.mp4", encoder, askForMode: false);
        string joined = string.Join(" ", asked);
        Check(joined.Contains("-f dshow -video_size 1280x720 -framerate 30 -vcodec mjpeg -rtbufsize 256M -i video=@alt"), "the camera is asked for 720p30 MJPEG by its alternative name");
        Check(!string.Join(" ", plain).Contains("-video_size") && string.Join(" ", plain).Contains("-i video=@alt"), "the fallback takes the camera's default mode");
        Check(joined.Contains("[0:v]fps=30,split[rec][pre];[pre]scale=480:270") && joined.Contains("-map [rec]") && joined.Contains("-f mp4 -y out.mp4") && joined.EndsWith("-map [preview] -pix_fmt bgra -f rawvideo -flush_packets 1 pipe:1"), "one constant-rate stream feeds the file and the preview pipe");

        var options = new RecordingOptions { Region = new PixelRect(0, 0, 640, 480), Encoder = encoder, OutputPath = "screen.mp4" };
        string[] without = RecordingArguments.Gdigrab(options), with = RecordingArguments.Gdigrab(options with { FrameClockPath = @"\\.\pipe\clock" });
        Check(!without.Contains("-filter_complex"), "a recording without a frame clock keeps its plain arguments");
        string clocked = string.Join(" ", with);
        Check(clocked.Contains("-filter_complex " + RecordingArguments.FrameClockGraph + " -map [rec] -c:v libopenh264") && clocked.EndsWith(@"-y screen.mp4 -map [clock] -r 30 -f rawvideo -flush_packets 1 -y \\.\pipe\clock"), "the frame clock is a second output fed by a split");

        // Settings: what a fresh install records, the summary line, and the two new shortcuts.
        var fresh = new Vibershot.Core.VideoSettings();
        Check(fresh.RecordSystemAudio && !fresh.RecordMicrophone && !fresh.RecordCamera && !fresh.StartImmediately && fresh.MicrophoneVolume == 100 && fresh.CameraShape == "Circle" && fresh.CameraMirror, "a fresh install records system audio only and shows the choices first");
        Check(fresh.Summary() == "Screen + system audio" && new Vibershot.Core.VideoSettings { RecordSystemAudio = false, RecordMicrophone = true, RecordCamera = true }.Summary() == "Screen + microphone + camera" && new Vibershot.Core.VideoSettings { Format = Vibershot.Core.VideoFormat.Avi }.Summary() == "Screen only (AVI)", "the summary names what will be recorded");
        var keys = new Vibershot.Core.HotkeySettings();
        Check(keys.PauseRecording == "" && keys.MuteMicrophone == "" && Vibershot.Core.HotkeySettings.Actions.Contains(Vibershot.Core.HotkeyAction.PauseRecording) && Vibershot.Core.HotkeySettings.Actions.Contains(Vibershot.Core.HotkeyAction.MuteMicrophone), "pause and mute shortcuts exist and start unset");
        keys[Vibershot.Core.HotkeyAction.PauseRecording] = "Ctrl+Alt+P"; keys[Vibershot.Core.HotkeyAction.MuteMicrophone] = "Ctrl+Alt+M";
        Check(keys.PauseRecording == "Ctrl+Alt+P" && keys.Gesture(Vibershot.Core.HotkeyAction.MuteMicrophone).IsValid && keys.ConflictFor(Vibershot.Core.HotkeyAction.RecordVideo, Vibershot.Core.HotkeyGesture.Parse("Ctrl+Alt+P")) == Vibershot.Core.HotkeyAction.PauseRecording, "the new shortcuts are stored and take part in conflict checks");
        var saved = Vibershot.Core.AppConfig.FromJson(new Vibershot.Core.AppConfig { Video = new Vibershot.Core.VideoSettings { RecordCamera = true, CameraName = "Cam", StartImmediately = true, MicrophoneVolume = 150 } }.ToJson())!;
        Check(saved.Video is { RecordCamera: true, CameraName: "Cam", StartImmediately: true, MicrophoneVolume: 150 }, "the recording choices survive the config file");
        Check(Vibershot.Core.AppConfig.FromJson("{\"Video\":{\"Quality\":60}}")!.Video is { RecordSystemAudio: true, RecordMicrophone: false, StartImmediately: false, Quality: 60 }, "an older config gets the defaults");

        string aac = string.Join(" ", AudioTrackRecorder.ToAacArguments("in.wav", "out.m4a"));
        Check(aac.Contains("-i in.wav -vn -af aresample=48000 -ac 2 -c:a aac -b:a 192k") && aac.EndsWith("-y out.m4a"), "a captured WAV becomes 48 kHz stereo AAC");
        Check(string.Join(" ", AudioTrackRecorder.ToAacArguments("in.wav", "out.m4a", 48000, 48010)).Contains("asetrate=48010,aresample=48000"), "a sound card running fast is stretched back onto the clock");
        Check(!string.Join(" ", AudioTrackRecorder.ToAacArguments("in.wav", "out.m4a", 48000, 48000.5)).Contains("asetrate"), "a rate within measuring noise is left alone");

        // The export graph: mirror, then the mask merged in as transparency before the overlay.
        var plan = new ExportPlan
        {
            Width = 640, Height = 360, FrameRate = FrameRate.Fps30, Duration = 2,
            Video = new[]
            {
                new ExportVideoClip("screen.mp4", 0, 2, 0, 0, 0, 640, 360),
                new ExportVideoClip("camera.mp4", 1.5, 2, 0, 500, 220, 120, 120) { MaskPath = "mask.png", FlipH = true },
                new ExportVideoClip("logo.png", 0, 2, 0, 10, 10, 50, 50) { IsImage = true },
            },
        };
        string[] export = ExportArguments.Build(plan, encoder, 75, "out.mp4");
        string graph = export[Array.IndexOf(export, "-filter_complex") + 1];
        Check(graph.Contains("[1:v]fps=30,scale=120:120:flags=lanczos,setsar=1,hflip,format=rgba[c1];[2:v]scale=120:120,format=gray[m1];[c1][m1]alphamerge,setpts="), "the mask is the input after its clip and is merged before the clip is timed");
        Check(graph.Contains("[o0][v1]overlay=") && graph.Contains("[3:v]fps=30,scale=50:50") && graph.Contains("[o1][v3]overlay="), "a clip after a masked one reads the input after the mask");
        Check(string.Join(" ", export).Contains("-i camera.mp4 -loop 1 -framerate 30 -t 2 -i mask.png -loop 1 -framerate 30 -t 2 -i logo.png"), "the mask input is looped for the clip's length");
    }

    static MediaSource Source(string path, double seconds, int width = 0, int height = 0, bool audio = false) =>
        new() { Path = path, Duration = seconds, Width = width, Height = height, FrameRate = "30", HasVideo = width > 0, HasAudio = audio };

    static void Project()
    {
        var manifest = new SessionManifest
        {
            Width = 1280, Height = 720, FrameRate = "30", Screen = "screen.mp4",
            // Two stretches with a pause between them: 4 s, then 3 s recorded 10 s later.
            Stretches = new[] { new ScreenStretch(0, 100, 4), new ScreenStretch(4, 114, 3) },
            Camera = new SessionTrack("camera.mp4", 97),            // camera opened 3 s before recording
            Microphone = new SessionTrack("mic.m4a", 99.5),
            SystemAudio = new SessionTrack("system.m4a", 100.25),   // began a quarter second late
            CameraBox = new SessionCameraBox(1000, 440, 240, 240, "Circle", true),
        };
        var screen = Source("screen.mp4", 7, 1280, 720);
        var camera = Source("camera.mp4", 21, 1280, 720);
        var mic = Source("mic.m4a", 18, audio: true);
        var system = Source("system.m4a", 17, audio: true);
        var project = SessionProject.Build(manifest, screen, camera, system, mic);

        Check(project.Width == 1280 && project.Height == 720 && project.FrameRate == "30", "the canvas is the recorded region at the recording's rate");
        Check(project.Tracks.Select(track => track.Name).SequenceEqual(new[] { "Screen", "Camera", "Microphone", "System audio" }), "screen below camera, then the two sound lanes");
        var screenClips = project.Tracks[0].Clips; var cameraClips = project.Tracks[1].Clips; var micClips = project.Tracks[2].Clips; var systemClips = project.Tracks[3].Clips;
        Check(screenClips.Count == 2 && screenClips[0].Start == 0 && screenClips[0].Duration == 120 && screenClips[1].Start == 120 && screenClips[1].Duration == 90 && Near(screenClips[1].SourceIn, 4), "one screen clip per stretch, end to end");
        Check(Near(cameraClips[0].SourceIn, 3) && Near(cameraClips[1].SourceIn, 17) && cameraClips[1].Start == 120, "the camera clip starts as far into its file as the stretch began after the camera did");
        Check(Near(micClips[0].SourceIn, 0.5) && Near(micClips[1].SourceIn, 14.5), "the microphone is lined up the same way");
        // System audio began 0.25 s after the first stretch: its clip starts 0.25 s (8 frames) late instead.
        Check(systemClips[0].Start == 8 && systemClips[0].Duration == 112 && Near(systemClips[0].SourceIn, 0) && Near(systemClips[1].SourceIn, 13.75), "a track that began late starts its clip late");
        Check(new[] { screenClips[0], cameraClips[0], micClips[0], systemClips[0] }.Select(clip => clip.LinkId).Distinct().Count() == 1 && screenClips[0].LinkId != null && screenClips[0].LinkId != screenClips[1].LinkId, "the clips of one stretch are linked, stretches are not linked to each other");
        Check(TimelineOps.WithLinked(project, new[] { screenClips[0].Id }).Count == 4, "moving the screen clip takes its camera and both sounds along");

        var cameraClip = (VideoClip)cameraClips[0];
        var placement = cameraClip.Placement!;
        Check(cameraClip.Frame is { Shape: FrameShape.Ellipse } && placement.FlipH, "the camera is a mirrored circle");
        Check(placement.X == 1000 && placement.Y == 440 && placement.Width == 240 && placement.Height == 240, "the camera sits where its bubble sat over the region");
        Check(Near(placement.CropLeft, (1 - 9.0 / 16) / 2) && Near(placement.CropRight, placement.CropLeft) && placement.CropTop == 0, "a 16:9 camera is cropped evenly to fill the square");

        // A bubble parked outside the region goes to the bottom right corner at its own size.
        var outside = SessionProject.Build(manifest with { CameraBox = new SessionCameraBox(2000, 100, 200, 200, "Rounded", false) }, screen, camera);
        var parked = (VideoClip)outside.Tracks[1].Clips[0];
        Check(parked.Placement!.Width == 200 && parked.Placement.X + 200 < 1280 && parked.Placement.X > 1000 && parked.Placement.Y > 450 && parked.Frame!.Shape == FrameShape.Rounded && !parked.Placement.FlipH, "a bubble outside the region is tucked into the corner");
        Check(outside.Tracks.Count == 3 && outside.Tracks[2].Kind == TrackKind.Audio && outside.Tracks[2].Clips.Count == 0, "a recording without sound still has an empty audio lane");

        // A clip too short to hold a whole stretch ends early instead of running past its file.
        var shortMic = SessionProject.Build(manifest, screen, null, null, Source("mic.m4a", 2.5, audio: true));
        Check(shortMic.Tracks[1].Clips.Count == 1 && shortMic.Tracks[1].Clips[0].Duration == 60, "a file that ends early ends its clip early and skips later stretches");

        // The frame and the mirror survive the project file; a plain project does not mention them.
        string json = DocumentJson.Serialize(project);
        var loaded = DocumentJson.Deserialize<VideoProject>(json)!;
        var loadedCamera = (VideoClip)loaded.Tracks[1].Clips[0];
        Check(loadedCamera.Frame is { Shape: FrameShape.Ellipse } && loadedCamera.Placement!.FlipH, "frame and mirror are saved");
        string plain = DocumentJson.Serialize(TimelineOps.ForSource(screen));
        Check(!plain.Contains("frame\"", StringComparison.OrdinalIgnoreCase) || !plain.Contains("\"Frame\""), "a clip without a frame leaves the field out");
        Check(!plain.Contains("flipH", StringComparison.OrdinalIgnoreCase), "a clip that is not mirrored leaves the field out");

        // Giving a wide picture a circle squares its box about the centre and crops it to fill.
        var wide = TimelineOps.ForSource(screen);
        var wideId = wide.Tracks[0].Clips[0].Id;
        var round = (VideoClip)TimelineOps.Find(TimelineOps.SetFrame(wide, wideId, new ClipFrame()), wideId)!.Value.Clip;
        Check(round.Frame!.Shape == FrameShape.Ellipse && round.Placement is { Width: 720, Height: 720, X: 280, Y: 0 } && Near(round.Placement.CropLeft, (1 - 9.0 / 16) / 2), "a circle on a wide picture makes a centred, evenly cropped square");
        Check(((VideoClip)TimelineOps.Find(TimelineOps.SetFrame(wide, wideId, null), wideId)!.Value.Clip).Frame == null, "the frame can be taken off again");

        // The plan carries the mask and the mirror only when there is somewhere to draw the mask.
        Check(TimelineOps.ToPlan(project).Video.All(clip => clip.MaskPath == null), "a plan made without a graphics folder has no masks");
        Check(FrameMask.Outline(null, new Rect(0, 0, 10, 10)) is System.Windows.Media.RectangleGeometry { RadiusX: 0 }, "no frame is the plain box");
        Check(FrameMask.Outline(new ClipFrame { Shape = FrameShape.Rounded, Radius = 0.25 }, new Rect(0, 0, 100, 40)) is System.Windows.Media.RectangleGeometry { RadiusX: 10 }, "a rounded frame's radius is a fraction of the shorter side");
    }

    /// <summary>Exports a red circle-framed clip over a blue one and reads the picture back.</summary>
    static void MaskedExport()
    {
        var runner = new FfmpegRunner();
        var encoder = EncoderProbe.ResolveAsync(runner, true).GetAwaiter().GetResult() ?? throw new Exception("FAIL: an H.264 encoder works");
        string folder = Path.Combine(Path.GetTempPath(), "vibershot-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string blue = Path.Combine(folder, "blue.mp4"), red = Path.Combine(folder, "red.mp4"), output = Path.Combine(folder, "out.mp4");
            foreach (var (path, colour) in new[] { (blue, "blue"), (red, "red") })
                Check(runner.RunAsync(new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"color=c={colour}:s=320x240:r=30:d=1", "-c:v", encoder.Name, "-pix_fmt", "yuv420p", "-y", path }).GetAwaiter().GetResult().Succeeded, "made " + colour);
            var below = Source(blue, 1, 320, 240); var above = Source(red, 1, 320, 240);
            var project = TimelineOps.ForSource(below);
            project = TimelineOps.InsertOnTop(project, above, 0, out var clipId, new Placement { X = 60, Y = 20, Width = 200, Height = 200, CropLeft = 0.125, CropRight = 0.125 });
            project = TimelineOps.Update(project, clipId, clip => ((VideoClip)clip) with { Frame = new ClipFrame { Shape = FrameShape.Ellipse } });
            var plan = TimelineOps.ToPlan(project, graphicsFolder: Path.Combine(folder, "graphics"));
            Check(plan.Video.Count(clip => clip.MaskPath != null && File.Exists(clip.MaskPath)) == 1, "the framed clip got a mask picture");
            var result = runner.RunAsync(ExportArguments.Build(plan, encoder, 90, output, includeAudio: false)).GetAwaiter().GetResult();
            Check(result.Succeeded, "the masked export ran: " + result.ErrorText(3));
            using var reader = new FrameReader(runner, new FrameReaderOptions { Path = output, Start = 0.5, Width = 320, Height = 240, FrameRate = FrameRate.Fps30, MaxFrames = 1 });
            byte[] frame = new byte[reader.FrameBytes];
            Check(reader.ReadFrameAsync(frame).AsTask().GetAwaiter().GetResult(), "read a frame back");
            (int B, int G, int R) At(int x, int y) { int i = (y * 320 + x) * 4; return (frame[i], frame[i + 1], frame[i + 2]); }
            var centre = At(160, 120); var corner = At(66, 26); var outsideBox = At(20, 120); var edge = At(160, 26);
            Check(centre.R > 180 && centre.B < 80, $"the middle of the circle shows the clip ({centre})");
            Check(corner.B > 180 && corner.R < 80, $"the corner of the box is cut away to the layer below ({corner})");
            Check(outsideBox.B > 180 && outsideBox.R < 80, "outside the box is the layer below");
            Check(edge.R > 180, $"the top of the circle still shows the clip ({edge})");
        }
        finally { try { Directory.Delete(folder, true); } catch (Exception) { } }
    }

    /// <summary>
    /// At the desk: records a corner of the screen for a few seconds with system sound, the
    /// default microphone and (when named) a camera, then prints what came out.
    /// </summary>
    public static void RecordLive(string? cameraName, double seconds = 6)
    {
        var runner = new FfmpegRunner();
        var encoder = EncoderProbe.ResolveAsync(runner, true).GetAwaiter().GetResult() ?? throw new Exception("no encoder");
        string folder = Path.Combine(Path.GetTempPath(), "vibershot-live-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(folder);
        using var rig = new CaptureRig(runner, folder);
        rig.UseSystemAudio(true);
        rig.UseMicrophone(true);
        var cameras = CameraDevices.ListAsync(runner).GetAwaiter().GetResult();
        Console.WriteLine("Cameras: " + string.Join(", ", cameras.Select(c => c.Name)));
        Console.WriteLine("Microphones: " + string.Join(", ", AudioDevices.Microphones().Select(m => m.Name + (m.IsDefault ? " (default)" : ""))));
        if (cameraName != null)
        {
            rig.UseCamera(cameras.First(c => c.Name == cameraName), encoder);
            Console.WriteLine("Camera started: " + rig.Camera!.Started.GetAwaiter().GetResult() + " " + rig.Camera.Error);
        }
        var screen = new RecorderSession(new RecorderSettings { Region = new PixelRect(0, 0, 640, 480), RecordingsFolder = folder, FrameClock = true, FinalFileName = "screen.mp4" }, runner);
        rig.BeginRecording(); // Sound first: the screen's first frames must not be silent.
        screen.StartAsync().GetAwaiter().GetResult();
        Thread.Sleep((int)(seconds * 500));
        Console.WriteLine($"Levels: system {rig.SystemAudio?.TakeLevel():F3} mic {rig.Microphone?.TakeLevel():F3}");
        screen.PauseAsync().GetAwaiter().GetResult();
        Thread.Sleep(1500);
        screen.ResumeAsync().GetAwaiter().GetResult();
        Thread.Sleep((int)(seconds * 500));
        string? final = screen.StopAsync().GetAwaiter().GetResult();
        Console.WriteLine($"Screen: {final} via {screen.Grabber}; stretches: " + string.Join(" | ", screen.Stretches.Select(s => $"file {s.FileStart:F3} wall {s.WallStart:F3} len {s.Duration:F3}")));
        var manifest = rig.FinishAsync(final!, screen.Stretches, 640, 480, FrameRate.Fps30, new SessionCameraBox(420, 260, 200, 200, "Circle", true), new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
        Console.WriteLine(File.ReadAllText(Path.Combine(folder, SessionManifest.FileName)));
        MediaSource? Probe(SessionTrack? track) => track == null ? null : MediaSource.From(Ffprobe.ProbeAsync(Path.Combine(folder, track.File)).GetAwaiter().GetResult());
        var project = SessionProject.Build(manifest, MediaSource.From(Ffprobe.ProbeAsync(final!).GetAwaiter().GetResult()), Probe(manifest.Camera), Probe(manifest.SystemAudio), Probe(manifest.Microphone));
        foreach (var track in project.Tracks)
            Console.WriteLine($"{track.Name}: " + string.Join(" | ", track.Clips.Select(clip => $"start {clip.Start} len {clip.Duration} in {clip.SourceIn:F3}")));
        VideoProjectFile.Save(Path.Combine(folder, SessionProject.ProjectFileName), project);
        var plan = TimelineOps.ToPlan(project, graphicsFolder: Path.Combine(folder, "graphics"));
        var export = runner.RunAsync(ExportArguments.Build(plan, encoder, 75, Path.Combine(folder, "flat.mp4"))).GetAwaiter().GetResult();
        Console.WriteLine("Export: " + (export.Succeeded ? "ok" : export.ErrorText(4)));
        Console.WriteLine("Folder: " + folder + "\n" + string.Join("\n", Directory.GetFiles(folder).Select(f => $"  {Path.GetFileName(f)} {new FileInfo(f).Length / 1024} KB")));
    }

    /// <summary>
    /// At the desk: opens the real recording window on a patch of the screen with every track
    /// switched on, saves pictures of it before, during and after a short recording into
    /// <paramref name="folder"/>, and leaves the recording there.
    /// </summary>
    public static void RecordUi(string folder, bool auto = false)
    {
        Directory.CreateDirectory(folder);
        Vibershot.UI.App.Hosted = true;
        var app = new Vibershot.UI.App();
        app.InitializeComponent();
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var settings = new Vibershot.Core.VideoSettings { RecordSystemAudio = true, RecordMicrophone = true, RecordCamera = true, StartImmediately = auto };
        var region = new Int32Rect(200, 160, 960, 540);
        var first = Vibershot.Core.CaptureService.Region(region.X, region.Y, region.Width, region.Height);
        var window = new Vibershot.UI.CaptureSessionWindow(region, first, true, null, settings, "test", "test", folder) { ShowActivated = false };
        T Field<T>(string name) => (T)typeof(Vibershot.UI.CaptureSessionWindow).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        void Snap(Window target, string name)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)target.ActualWidth), Math.Max(1, (int)target.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(target);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, name));
            encoder.Save(stream);
        }
        void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        int step = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            step++;
            if (step == 1)
            {
                Snap(window, "1-setup.png");
                var bubble = typeof(Vibershot.UI.CaptureSessionWindow).GetField("_bubble", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window) as Window;
                if (bubble != null) Snap(bubble, "1-bubble.png");
                // With "start straight away" the recording must already be running by itself; the hotkey paths are tried on it.
                if (auto)
                {
                    Console.WriteLine("Started by itself: " + (Field<System.Windows.Controls.Button>("_finish").Visibility == Visibility.Visible && Field<System.Windows.Controls.Button>("_start").Visibility != Visibility.Visible));
                    window.ToggleMute();
                    Console.WriteLine("Mute shortcut ticks the box: " + (Field<System.Windows.Controls.CheckBox>("_mute").IsChecked == true));
                    window.PauseOrResume();
                }
                else Click(Field<System.Windows.Controls.Button>("_start"));
                timer.Interval = TimeSpan.FromSeconds(5);
            }
            else if (step == 2)
            {
                Snap(window, "2-recording.png");
                if (auto) { Console.WriteLine("Pause shortcut paused: " + ((string)Field<System.Windows.Controls.Button>("_pause").Content == "Resume")); window.PauseOrResume(); timer.Interval = TimeSpan.FromSeconds(3); }
                else { Click(Field<System.Windows.Controls.Button>("_finish")); timer.Interval = TimeSpan.FromSeconds(1); }
            }
            else if (step == 3 && auto) { window.StartOrStop(); timer.Interval = TimeSpan.FromSeconds(1); }
            else if (typeof(Vibershot.UI.CaptureSessionWindow).GetField("_projectPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window) != null || step > 30)
            {
                timer.Stop();
                Snap(window, "3-done.png");
                Console.WriteLine("Status: " + Field<System.Windows.Controls.TextBlock>("_status").Text);
                window.Close();
            }
        };
        window.Loaded += (_, _) => timer.Start();
        window.Closed += (_, _) => app.Shutdown();
        app.Run(window);
        foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) Console.WriteLine($"  {file.Substring(folder.Length)} {new FileInfo(file).Length / 1024} KB");
    }

    /// <summary>At the desk: opens Settings on a copy of the config, shows the Video page and saves a picture of it.</summary>
    public static void SettingsPicture(string path)
    {
        Vibershot.UI.App.Hosted = true;
        var app = new Vibershot.UI.App();
        app.InitializeComponent();
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var config = Vibershot.Core.AppConfig.FromJson(app.Config.ToJson())!;
        var window = new Vibershot.UI.SettingsWindow(config) { ShowActivated = false, Height = 1500 };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        int step = 0;
        timer.Tick += (_, _) =>
        {
            if (step++ == 0) { ((System.Windows.Controls.RadioButton)window.FindName("NavVideo")).IsChecked = true; timer.Interval = TimeSpan.FromSeconds(1.5); return; }
            timer.Stop();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            window.Close();
        };
        window.Loaded += (_, _) => timer.Start();
        window.Closed += (_, _) => app.Shutdown();
        app.Run(window);
    }
}
