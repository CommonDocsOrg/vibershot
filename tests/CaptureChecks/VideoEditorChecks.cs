using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vibershot.Editing.Video;
using Vibershot.Media;

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
            Video = new[] { new ExportVideoClip(@"C:\a.mp4", 1.5, 2, 0), new ExportVideoClip(@"C:\b.mp4", 0, 1, 2.5) },
            Audio = new[] { new ExportAudioClip(@"C:\a.mp4", 1.5, 2, 0, 0.5) },
        };
        const string fit = "scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,setsar=1";
        string graph = "color=c=0x000000:s=1280x720:r=30:d=4,format=yuv420p[bg]"
            + $";[0:v]fps=30,{fit},setpts=PTS-STARTPTS+0/TB[v0];[bg][v0]overlay=x=(W-w)/2:y=(H-h)/2:eof_action=pass:enable='gte(t,0)*lt(t,2)'[o0]"
            + $";[1:v]fps=30,{fit},setpts=PTS-STARTPTS+2.5/TB[v1];[o0][v1]overlay=x=(W-w)/2:y=(H-h)/2:eof_action=pass:enable='gte(t,2.5)*lt(t,3.5)'[o1]"
            + ";[o1]format=yuv420p[vout]"
            + ";[2:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.5,adelay=0:all=1[a2];[a2]amix=inputs=1:normalize=0:duration=longest,apad,atrim=0:4[aout]";
        Same(new[] { "-hide_banner", "-nostats", "-progress", "pipe:1", "-ss", "1.5", "-t", "2", "-i", @"C:\a.mp4", "-ss", "0", "-t", "1", "-i", @"C:\b.mp4", "-ss", "1.5", "-t", "2", "-vn", "-i", @"C:\a.mp4",
                "-filter_complex", graph, "-map", "[vout]", "-map", "[aout]", "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-g", "30",
                "-r", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-t", "4", "-movflags", "+faststart", "-f", "mp4", "-y", @"C:\out.mp4" },
            ExportArguments.Build(plan, EncoderProbe.Hardware[0], 75, @"C:\out.mp4"), "export arguments");
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
        Console.WriteLine("PASS: video editor timeline edits, project file and relink, frame reader/proxy/export/audio mix arguments.");
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
    }
}
