using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Threading;

namespace Recorder.Core;

public class WasapiDualRecorder : IDisposable
{
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _systemWriter;
    private MMDevice? _micDevice;
    private MMDevice? _systemDevice;

    private readonly ManualResetEventSlim _micStopped = new(false);
    private readonly ManualResetEventSlim _systemStopped = new(false);
    private readonly object _lock = new();

    // Stopwatch ticks of the first DataAvailable event for each capture.
    // 0 means not yet received. Written once via Interlocked.CompareExchange.
    private long _micFirstTick;
    private long _systemFirstTick;
    private volatile bool _isMicrophoneMuted;
    private byte[] _silentMicBuffer = Array.Empty<byte>();

    public bool IsRecording { get; private set; }
    public bool IsMicrophoneMuted => _isMicrophoneMuted;
    public string? LastMicPath { get; private set; }
    public string? LastSystemPath { get; private set; }

    // Set after Stop() — silence to prepend to each track before merging.
    // Exactly one of these will always be TimeSpan.Zero.
    public TimeSpan MicSilencePad { get; private set; }
    public TimeSpan SystemSilencePad { get; private set; }

    /// <summary>Returns all active audio input (microphone) devices.</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetMicDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    /// <summary>Returns all active audio output (playback) devices, usable for loopback capture.</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    private static MMDevice? ResolveDevice(string? deviceId)
    {
        if (deviceId is null) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(deviceId);
        }
        catch
        {
            return null;
        }
    }

    public void Start(string micPath, string systemPath,
                      string? micDeviceId = null, string? outputDeviceId = null)
    {
        lock (_lock)
        {
            if (IsRecording) return;

            // Release any devices held from a previous recording session
            _micDevice?.Dispose();
            _micDevice = null;
            _systemDevice?.Dispose();
            _systemDevice = null;

            LastMicPath = micPath;
            LastSystemPath = systemPath;

            _micFirstTick = 0;
            _systemFirstTick = 0;
            MicSilencePad = TimeSpan.Zero;
            SystemSilencePad = TimeSpan.Zero;
            _isMicrophoneMuted = false;
            _silentMicBuffer = Array.Empty<byte>();

            _micStopped.Reset();
            _systemStopped.Reset();

            _micDevice = ResolveDevice(micDeviceId);
            _micCapture = _micDevice is not null
                ? new WasapiCapture(_micDevice)
                : new WasapiCapture();
            _micWriter = new WaveFileWriter(micPath, _micCapture.WaveFormat);
            _micCapture.DataAvailable += (s, a) =>
            {
                // Record the timestamp of the very first buffer only
                Interlocked.CompareExchange(ref _micFirstTick, Stopwatch.GetTimestamp(), 0);

                if (!_isMicrophoneMuted)
                {
                    _micWriter?.Write(a.Buffer, 0, a.BytesRecorded);
                    return;
                }

                if (_silentMicBuffer.Length < a.BytesRecorded)
                    _silentMicBuffer = new byte[a.BytesRecorded];

                _micWriter?.Write(_silentMicBuffer, 0, a.BytesRecorded);
            };
            _micCapture.RecordingStopped += (s, a) =>
            {
                _micWriter?.Dispose();
                _micWriter = null;
                _micStopped.Set();
            };

            _systemDevice = ResolveDevice(outputDeviceId);
            _systemCapture = _systemDevice is not null
                ? new WasapiLoopbackCapture(_systemDevice)
                : new WasapiLoopbackCapture();

            // Some drivers return a mono mix format from GetMixFormat(). When NAudio
            // initialises the loopback audio client with that format, Windows reconfigures
            // the endpoint's internal mixer to mono, which is also heard in the headphones.
            // Overriding to stereo IEEE float (the standard WASAPI shared-mode format)
            // keeps the render pipeline stereo while still capturing a usable loopback stream.
            var sysFmt = _systemCapture.WaveFormat;
            if (sysFmt.Channels < 2)
                _systemCapture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sysFmt.SampleRate, 2);

            _systemWriter = new WaveFileWriter(systemPath, _systemCapture.WaveFormat);
            _systemCapture.DataAvailable += (s, a) =>
            {
                Interlocked.CompareExchange(ref _systemFirstTick, Stopwatch.GetTimestamp(), 0);
                _systemWriter?.Write(a.Buffer, 0, a.BytesRecorded);
            };
            _systemCapture.RecordingStopped += (s, a) =>
            {
                _systemWriter?.Dispose();
                _systemWriter = null;
                _systemStopped.Set();
            };

            _micCapture.StartRecording();
            _systemCapture.StartRecording();

            IsRecording = true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRecording) return;
            IsRecording = false;
        }

        _micCapture?.StopRecording();
        _systemCapture?.StopRecording();

        // Block until both WaveFileWriters have been flushed and closed
        _micStopped.Wait(TimeSpan.FromSeconds(10));
        _systemStopped.Wait(TimeSpan.FromSeconds(10));

        ComputeSilencePads();
    }

    public void SetMicrophoneMuted(bool muted)
    {
        _isMicrophoneMuted = muted;
    }

    private void ComputeSilencePads()
    {
        if (_micFirstTick == 0 || _systemFirstTick == 0)
            return;

        long delta = _micFirstTick - _systemFirstTick;
        double seconds = (double)Math.Abs(delta) / Stopwatch.Frequency;

        // Cap at 5s — anything larger likely indicates a measurement anomaly
        seconds = Math.Min(seconds, 5.0);

        if (delta > 0)
        {
            // Mic delivered its first buffer later — pad the start of mic
            MicSilencePad = TimeSpan.FromSeconds(seconds);
            SystemSilencePad = TimeSpan.Zero;
        }
        else
        {
            // System delivered its first buffer later — pad the start of system
            MicSilencePad = TimeSpan.Zero;
            SystemSilencePad = TimeSpan.FromSeconds(seconds);
        }
    }

    public void Dispose()
    {
        Stop();
        _micCapture?.Dispose();
        _micCapture = null;
        _systemCapture?.Dispose();
        _systemCapture = null;
        _micDevice?.Dispose();
        _micDevice = null;
        _systemDevice?.Dispose();
        _systemDevice = null;
        _micStopped.Dispose();
        _systemStopped.Dispose();
    }
}
