using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Threading;

namespace Recorder.Core;

public class WasapiDualRecorder : IDisposable
{
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _systemWriter;

    private readonly ManualResetEventSlim _micStopped = new(false);
    private readonly ManualResetEventSlim _systemStopped = new(false);
    private readonly object _lock = new();

    public bool IsRecording { get; private set; }
    public string? LastMicPath { get; private set; }
    public string? LastSystemPath { get; private set; }

    public void Start(string micPath, string systemPath)
    {
        lock (_lock)
        {
            if (IsRecording) return;

            LastMicPath = micPath;
            LastSystemPath = systemPath;

            _micStopped.Reset();
            _systemStopped.Reset();

            _micCapture = new WasapiCapture();
            _micWriter = new WaveFileWriter(micPath, _micCapture.WaveFormat);
            _micCapture.DataAvailable += (s, a) => _micWriter?.Write(a.Buffer, 0, a.BytesRecorded);
            _micCapture.RecordingStopped += (s, a) =>
            {
                _micWriter?.Dispose();
                _micWriter = null;
                _micStopped.Set();
            };

            _systemCapture = new WasapiLoopbackCapture();
            _systemWriter = new WaveFileWriter(systemPath, _systemCapture.WaveFormat);
            _systemCapture.DataAvailable += (s, a) => _systemWriter?.Write(a.Buffer, 0, a.BytesRecorded);
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
    }

    public void Dispose()
    {
        Stop();
        _micCapture?.Dispose();
        _micCapture = null;
        _systemCapture?.Dispose();
        _systemCapture = null;
        _micStopped.Dispose();
        _systemStopped.Dispose();
    }
}
