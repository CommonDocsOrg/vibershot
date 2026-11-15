using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Vibershot.Media.Audio;

/// <summary>
/// The one clock every recorded track is stamped against: seconds on the
/// QueryPerformanceCounter timeline, which is also what WASAPI stamps audio packets with.
/// </summary>
public static class MediaClock
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

/// <summary>A sound device as the pickers show it.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public static class AudioDevices
{
    /// <summary>Microphones and other inputs that are plugged in and enabled, the default one first.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Microphones()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            string? defaultId = null;
            try { defaultId = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch (Exception) { }
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
        }
        catch (Exception) { }
        return list.OrderByDescending(device => device.IsDefault).ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The speakers or headphones Windows plays through now; null when there are none.</summary>
    public static AudioDeviceInfo? DefaultOutput()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new AudioDeviceInfo(device.ID, device.FriendlyName, true);
        }
        catch (Exception) { return null; }
    }
}

/// <summary>What one finished audio track holds.</summary>
/// <param name="Path">The WAV as captured, in the device's own format.</param>
/// <param name="Start">The <see cref="MediaClock"/> time of the first sample in the file.</param>
public sealed record AudioTrackResult(string Path, double Start, long Frames, int SampleRate, int Channels)
{
    public double Duration => SampleRate > 0 ? Frames / (double)SampleRate : 0;
}

/// <summary>
/// Listens to one sound device: a microphone, or what an output device is playing (loopback).
/// It always measures the level, for the meter; between <see cref="BeginWriting"/> and
/// <see cref="EndWriting"/> it also writes the samples to a WAV whose sample position equals
/// elapsed time (silent gaps are filled in), so the file can be lined up with the other tracks
/// from its start time alone.
/// </summary>
public sealed class AudioTrackRecorder : IDisposable
{
    /// <summary>A hole longer than this between packets is filled with silence.</summary>
    private const double PadThreshold = 0.1;

    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _opened = new();
    private volatile bool _stop;
    private float _peak;
    private WaveFileWriter? _writer;
    private string? _path;
    private long _frames;
    private int _packets;
    private double _firstStamp, _startSum;
    private int _startCount;
    private int _rate, _channels;

    /// <param name="deviceId">Null records the default device of its kind.</param>
    /// <param name="loopback">True records what an output device plays instead of an input.</param>
    public AudioTrackRecorder(string? deviceId, bool loopback)
    {
        Loopback = loopback;
        // WASAPI objects belong to the thread that made them, so the whole capture lives on one thread.
        _thread = new Thread(() => Run(deviceId)) { IsBackground = true, Name = loopback ? "Vibershot system audio" : "Vibershot microphone" };
        _thread.Start();
    }

    public bool Loopback { get; }

    /// <summary>Linear gain applied to the samples (and the meter): 1 leaves the sound unchanged.</summary>
    public double Gain { get; set; } = 1;

    /// <summary>Writes silence while set; the meter still moves so the user can see the device is alive.</summary>
    public bool Muted { get; set; }

    public string DeviceName { get; private set; } = "";

    /// <summary>Why the device could not be opened or stopped delivering; null while all is well.</summary>
    public string? Error { get; private set; }

    public bool IsWriting { get { lock (_gate) return _writer != null; } }

    /// <summary>Waits until the device is open (or failed). False on failure or timeout.</summary>
    public bool WaitUntilOpen(TimeSpan timeout) => _opened.Wait(timeout) && Error == null;

    /// <summary>The loudest sample since the last call, 0 to 1.</summary>
    public double TakeLevel()
    {
        float peak = Interlocked.Exchange(ref _peak, 0);
        return Math.Clamp(peak, 0, 1);
    }

    public void BeginWriting(string wavPath)
    {
        if (!_opened.Wait(TimeSpan.FromSeconds(3)) || Error != null) throw new InvalidOperationException(Error ?? "The sound device did not open.");
        lock (_gate)
        {
            if (_writer != null) throw new InvalidOperationException("Already writing.");
            _path = wavPath; _frames = 0; _packets = 0; _startSum = 0; _startCount = 0;
            _writer = new WaveFileWriter(wavPath, _format!);
        }
    }

    /// <summary>Closes the WAV. Null when nothing was being written or no sound arrived.</summary>
    public AudioTrackResult? EndWriting()
    {
        lock (_gate)
        {
            if (_writer == null) return null;
            _writer.Dispose(); _writer = null;
            if (_packets == 0) { try { File.Delete(_path!); } catch (Exception) { } return null; }
            // The very first packet's stamp is a few milliseconds off the rest, so the start is the
            // average of what the following packets say it was.
            double start = _startCount > 0 ? _startSum / _startCount : _firstStamp;
            return new AudioTrackResult(_path!, start, _frames, _rate, _channels);
        }
    }

    private WaveFormat? _format;

    private void Run(string? deviceId)
    {
        WasapiOut? keepAlive = null;
        AudioClient? client = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            var device = deviceId != null ? devices.GetDevice(deviceId)
                : devices.GetDefaultAudioEndpoint(Loopback ? DataFlow.Render : DataFlow.Capture, Loopback ? Role.Multimedia : Role.Communications);
            DeviceName = device.FriendlyName;
            client = device.AudioClient;
            var format = client.MixFormat;
            _format = format; _rate = format.SampleRate; _channels = format.Channels;
            client.Initialize(AudioClientShareMode.Shared, Loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None, 2_000_000, 0, format, Guid.Empty);
            var capture = client.AudioCaptureClient;
            if (Loopback)
            {
                // Loopback hands over nothing while the device is idle; a silent stream of our own keeps it running.
                try
                {
                    keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
                    keepAlive.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)));
                    keepAlive.Play();
                }
                catch (Exception) { keepAlive = null; }
            }
            bool isFloat = format.BitsPerSample == 32, isShort = format.BitsPerSample == 16;
            int block = format.BlockAlign;
            byte[] buffer = new byte[format.SampleRate * block / 2];
            client.Start();
            _opened.Set();
            while (!_stop)
            {
                Thread.Sleep(5);
                while (capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out long _, out long stamp);
                    int bytes = frames * block;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                    if (silent) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    capture.ReleaseBuffer(frames);

                    float gain = (float)Math.Max(0, Gain), peak = 0;
                    if (!silent && isFloat)
                    {
                        var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes));
                        for (int i = 0; i < samples.Length; i++)
                        {
                            float value = samples[i] * gain;
                            if (gain != 1) samples[i] = Math.Clamp(value, -1f, 1f);
                            float size = Math.Abs(value);
                            if (size > peak) peak = size;
                        }
                    }
                    else if (!silent && isShort)
                    {
                        var samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytes));
                        for (int i = 0; i < samples.Length; i++)
                        {
                            float value = samples[i] * gain;
                            if (gain != 1) samples[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                            float size = Math.Abs(value) / 32768f;
                            if (size > peak) peak = size;
                        }
                    }
                    if (peak > _peak) _peak = peak;

                    lock (_gate)
                    {
                        if (_writer == null) continue;
                        double at = stamp / 1e7;
                        if (_packets == 0) _firstStamp = at;
                        else
                        {
                            double gap = at - (_firstStamp + _frames / (double)_rate);
                            if (gap > PadThreshold)
                            {
                                long pad = (long)Math.Round(gap * _rate);
                                byte[] zeros = new byte[Math.Min(pad, _rate) * block];
                                for (long left = pad; left > 0;) { int n = (int)Math.Min(left, zeros.Length / block); _writer.Write(zeros, 0, n * block); left -= n; }
                                _frames += pad;
                            }
                            if (_startCount < 200) { _startSum += at - _frames / (double)_rate; _startCount++; }
                        }
                        if (Muted) Array.Clear(buffer, 0, bytes);
                        _writer.Write(buffer, 0, bytes);
                        _frames += frames; _packets++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Error = ex is COMException { HResult: unchecked((int)0x80070005) }
                ? "Windows is blocking access to this device (Settings → Privacy → Microphone)."
                : ex.Message;
            _opened.Set();
        }
        finally
        {
            try { client?.Stop(); } catch (Exception) { }
            try { keepAlive?.Stop(); keepAlive?.Dispose(); } catch (Exception) { }
            try { client?.Dispose(); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(3000);
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }

    /// <summary>
    /// Arguments that turn a captured WAV into AAC at 48 kHz stereo. <paramref name="measuredRate"/>
    /// is how many samples the device really delivered per second of the shared clock; when it
    /// differs from the nominal rate the sound is stretched back onto the clock.
    /// </summary>
    public static string[] ToAacArguments(string wavPath, string outputPath, int nominalRate = 0, double measuredRate = 0)
    {
        string filter = "aresample=48000";
        if (nominalRate > 0 && measuredRate > 0 && Math.Abs(measuredRate / nominalRate - 1) is > 0.00005 and < 0.01)
            filter = $"asetrate={measuredRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},aresample=48000";
        return FfmpegRunner.StandardPrefix.Concat(new[]
        {
            "-i", wavPath, "-vn", "-af", filter, "-ac", "2", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "ipod", "-y", outputPath,
        }).ToArray();
    }
}
