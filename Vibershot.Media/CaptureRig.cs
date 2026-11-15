using System.Text.Json;
using Vibershot.Media.Audio;
using Vibershot.Media.Camera;

namespace Vibershot.Media;

/// <summary>A recorded file beside the screen recording, and when its time zero was on <see cref="MediaClock"/>.</summary>
public sealed record SessionTrack(string File, double Start);

/// <summary>Where the camera bubble sat while recording, in pixels of the recorded region, and how it was shaped.</summary>
public sealed record SessionCameraBox(double X, double Y, double Width, double Height, string Shape, bool Mirror);

/// <summary>
/// What one recording session produced: <c>session.json</c> in the session folder. File names are
/// relative to that folder. The video editor project is built from this.
/// </summary>
public sealed record SessionManifest
{
    public const string FileName = "session.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public int Width { get; init; }
    public int Height { get; init; }
    public string FrameRate { get; init; } = "30";
    public DateTime Created { get; init; } = DateTime.Now;

    /// <summary>The screen recording.</summary>
    public string Screen { get; init; } = "";

    /// <summary>The unbroken runs inside the screen recording (one per pause), with their real start times.</summary>
    public IReadOnlyList<ScreenStretch> Stretches { get; init; } = Array.Empty<ScreenStretch>();

    public SessionTrack? Camera { get; init; }
    public SessionTrack? SystemAudio { get; init; }
    public SessionTrack? Microphone { get; init; }
    public SessionCameraBox? CameraBox { get; init; }

    /// <summary>Tracks that were asked for but could not be recorded, in words for the user.</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    [System.Text.Json.Serialization.JsonIgnore] public bool HasExtras => Camera != null || SystemAudio != null || Microphone != null;

    public void Save(string folder) => File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Options));

    public static SessionManifest? Load(string folder)
    {
        try { return JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(Path.Combine(folder, FileName)), Options); }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// The devices recorded beside the screen: system sound, a microphone and a webcam. They are
/// opened while the user is still choosing (so the meters move and the camera preview shows),
/// start writing at <see cref="BeginRecording"/> and are wrapped up, with the screen recording,
/// into a <see cref="SessionManifest"/> by <see cref="FinishAsync"/>.
/// </summary>
public sealed class CaptureRig : IDisposable
{
    private readonly FfmpegRunner _runner;
    private int _cameraOpens;
    private bool _recording;

    public CaptureRig(FfmpegRunner runner, string folder)
    {
        _runner = runner;
        Folder = folder;
    }

    /// <summary>The session folder every file of this recording goes into.</summary>
    public string Folder { get; }

    public AudioTrackRecorder? SystemAudio { get; private set; }
    public AudioTrackRecorder? Microphone { get; private set; }
    public CameraSession? Camera { get; private set; }
    public bool IsRecording => _recording;

    /// <summary>Tracks that failed, in words for the user.</summary>
    public List<string> Problems { get; } = new();

    public void UseSystemAudio(bool on)
    {
        if (_recording) return;
        SystemAudio?.Dispose();
        SystemAudio = on ? new AudioTrackRecorder(null, loopback: true) : null;
    }

    /// <param name="deviceId">Null uses the default microphone.</param>
    public void UseMicrophone(bool on, string? deviceId = null)
    {
        if (_recording) return;
        Microphone?.Dispose();
        Microphone = on ? new AudioTrackRecorder(deviceId, loopback: false) : null;
    }

    /// <summary>Opens a camera (closing the one open before); null turns the camera off.</summary>
    public void UseCamera(CameraDeviceInfo? device, EncoderInfo? encoder = null)
    {
        if (_recording) return;
        var old = Camera;
        Camera = null;
        if (old != null) { old.Dispose(); TryDelete(old.OutputPath); }
        if (device == null || encoder == null) return;
        Directory.CreateDirectory(Folder);
        // A new name each time: the process just killed may still hold the old file for a moment.
        Camera = new CameraSession(_runner, device, Path.Combine(Folder, $"camera_take{++_cameraOpens:D2}.mp4"), encoder);
    }

    /// <summary>Starts writing the sound tracks. Call it as the screen recording starts.</summary>
    public void BeginRecording()
    {
        if (_recording) return;
        _recording = true;
        Directory.CreateDirectory(Folder);
        Begin(SystemAudio, "system.wav", "System audio");
        Begin(Microphone, "mic.wav", "Microphone");
        if (Camera is { Error: { } cameraError }) Problems.Add("Camera: " + cameraError);
    }

    private void Begin(AudioTrackRecorder? recorder, string file, string label)
    {
        if (recorder == null) return;
        try { recorder.BeginWriting(Path.Combine(Folder, file)); }
        catch (Exception ex) { Problems.Add($"{label}: {ex.Message}"); }
    }

    /// <summary>
    /// Closes every track, turns the sound into AAC, tidies the camera file and writes the
    /// manifest. <paramref name="screenFile"/> is the finished screen recording inside <see cref="Folder"/>.
    /// </summary>
    public async Task<SessionManifest> FinishAsync(string screenFile, IReadOnlyList<ScreenStretch> stretches, int width, int height, FrameRate rate, SessionCameraBox? cameraBox, IProgress<string>? progress = null)
    {
        _recording = false;
        var system = await FinishAudioAsync(SystemAudio, "system.m4a", "System audio", progress).ConfigureAwait(false);
        var microphone = await FinishAudioAsync(Microphone, "mic.m4a", "Microphone", progress).ConfigureAwait(false);
        SystemAudio?.Dispose(); SystemAudio = null;
        Microphone?.Dispose(); Microphone = null;

        SessionTrack? camera = null;
        if (Camera != null)
        {
            progress?.Report("Finishing camera…");
            var session = Camera;
            Camera = null;
            var result = await session.StopAsync().ConfigureAwait(false);
            if (result == null) Problems.Add("Camera: " + (session.Error ?? "nothing was recorded."));
            else
            {
                string final = Path.Combine(Folder, "camera.mp4");
                var remux = await _runner.RunAsync(RecordingArguments.Remux(result.Path, final)).ConfigureAwait(false);
                if (remux.Succeeded && File.Exists(final)) { TryDelete(result.Path); camera = new SessionTrack("camera.mp4", result.Start); }
                else camera = new SessionTrack(Path.GetFileName(result.Path), result.Start); // The fragmented file plays as it is.
            }
            session.Dispose();
        }

        var manifest = new SessionManifest
        {
            Width = width, Height = height, FrameRate = rate.ToFfmpegString(),
            Screen = Path.GetFileName(screenFile), Stretches = stretches.ToList(),
            Camera = camera, SystemAudio = system, Microphone = microphone,
            CameraBox = camera != null ? cameraBox : null,
            Problems = Problems.ToList(),
        };
        manifest.Save(Folder);
        return manifest;
    }

    private async Task<SessionTrack?> FinishAudioAsync(AudioTrackRecorder? recorder, string file, string label, IProgress<string>? progress)
    {
        if (recorder == null) return null;
        var result = recorder.EndWriting();
        if (result == null)
        {
            if (!Problems.Any(problem => problem.StartsWith(label, StringComparison.Ordinal))) Problems.Add($"{label}: {recorder.Error ?? "no sound arrived."}");
            return null;
        }
        progress?.Report($"Finishing {label.ToLowerInvariant()}…");
        string final = Path.Combine(Folder, file);
        var encode = await _runner.RunAsync(AudioTrackRecorder.ToAacArguments(result.Path, final)).ConfigureAwait(false);
        if (encode.Succeeded && File.Exists(final)) { TryDelete(result.Path); return new SessionTrack(file, result.Start); }
        return new SessionTrack(Path.GetFileName(result.Path), result.Start); // Keep the WAV: it is the recording.
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }

    public void Dispose()
    {
        _recording = false;
        SystemAudio?.Dispose(); SystemAudio = null;
        Microphone?.Dispose(); Microphone = null;
        Camera?.Dispose(); Camera = null;
    }
}
