using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Recorder.Core;

public sealed class WasapiDualRecorder : IDisposable
{
    private readonly object _gate = new();
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _systemWriter;
    private MMDevice? _micDevice;
    private MMDevice? _systemDevice;
    private TaskCompletionSource _micStopped = NewCompletionSource();
    private TaskCompletionSource _systemStopped = NewCompletionSource();
    private long _startTick;
    private long _micBytesWritten;
    private long _systemBytesWritten;
    private volatile bool _isRecording;
    private volatile bool _isMicrophoneMuted;
    private byte[] _silentMicBuffer = Array.Empty<byte>();
    private float _micPeak;
    private float _systemPeak;
    private bool _disposed;

    public bool IsRecording => _isRecording;
    public bool IsMicrophoneMuted => _isMicrophoneMuted;
    public float MicrophonePeak => Math.Clamp(Volatile.Read(ref _micPeak), 0f, 1f);
    public float SystemPeak => Math.Clamp(Volatile.Read(ref _systemPeak), 0f, 1f);
    public string? LastMicPath { get; private set; }
    public string? LastSystemPath { get; private set; }
    public TimeSpan MicSilencePad { get; private set; }
    public TimeSpan SystemSilencePad { get; private set; }
    private Exception? _micFailure;
    private Exception? _systemFailure;

    /// <summary>
    /// Fired the moment a capture device drops out (e.g. Bluetooth headphones disconnecting)
    /// while recording is still in progress. Recording continues on the surviving device;
    /// this is purely a heads-up so the UI can warn the user before they hit Stop.
    /// </summary>
    public event EventHandler<DeviceLostEventArgs>? DeviceLost;

    public static IReadOnlyList<AudioDeviceInfo> GetMicDevices() => GetDevices(DataFlow.Capture);

    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => GetDevices(DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var results = new List<AudioDeviceInfo>(devices.Count);
        foreach (MMDevice device in devices)
        {
            using (device)
                results.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
        }

        return results;
    }

    private static MMDevice? ResolveDevice(string? deviceId)
    {
        if (deviceId is null)
            return null;

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

    public void Start(string micPath, string systemPath, string? micDeviceId = null, string? outputDeviceId = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording)
                throw new InvalidOperationException("A recording is already in progress.");

            ResetSession(micPath, systemPath);

            try
            {
                _micDevice = ResolveDevice(micDeviceId);
                _micCapture = _micDevice is not null ? new WasapiCapture(_micDevice) : new WasapiCapture();
                _micWriter = new WaveFileWriter(micPath, _micCapture.WaveFormat);
                _micCapture.DataAvailable += MicDataAvailable;
                _micCapture.RecordingStopped += MicRecordingStopped;

                _systemDevice = ResolveDevice(outputDeviceId);
                _systemCapture = _systemDevice is not null
                    ? new WasapiLoopbackCapture(_systemDevice)
                    : new WasapiLoopbackCapture();

                var systemFormat = _systemCapture.WaveFormat;
                if (systemFormat.Channels < 2)
                    _systemCapture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(systemFormat.SampleRate, 2);

                _systemWriter = new WaveFileWriter(systemPath, _systemCapture.WaveFormat);
                _systemCapture.DataAvailable += SystemDataAvailable;
                _systemCapture.RecordingStopped += SystemRecordingStopped;

                _startTick = Stopwatch.GetTimestamp();
                _micCapture.StartRecording();
                _systemCapture.StartRecording();
                _isRecording = true;
            }
            catch
            {
                AbortPartialStart();
                TryDelete(micPath);
                TryDelete(systemPath);
                throw;
            }
        }
    }

    public async Task<RecorderStopResult> StopAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        WasapiCapture? micCapture;
        WasapiLoopbackCapture? systemCapture;
        Task micStopped;
        Task systemStopped;

        lock (_gate)
        {
            if (!_isRecording)
                return RecorderStopResult.None;

            _isRecording = false;
            micCapture = _micCapture;
            systemCapture = _systemCapture;
            micStopped = _micStopped.Task;
            systemStopped = _systemStopped.Task;
        }

        try { micCapture?.StopRecording(); } catch (Exception ex) { _micFailure ??= ex; _micStopped.TrySetResult(); }
        try { systemCapture?.StopRecording(); } catch (Exception ex) { _systemFailure ??= ex; _systemStopped.TrySetResult(); }

        Exception? unexpected = null;
        try
        {
            await Task.WhenAll(micStopped, systemStopped)
                .WaitAsync(timeout ?? TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller-driven cancellation should still propagate
        }
        catch (Exception ex)
        {
            // A device fault (e.g. AUDCLNT_E_DEVICE_INVALIDATED from a disconnected Bluetooth
            // headset) is already captured in _micFailure/_systemFailure via TrySetResult above,
            // and whatever audio each track managed to record is safely on disk - so a known
            // device fault should never block the caller from attempting the mixdown. Anything
            // else here (e.g. a genuine stop timeout) is unexpected and should still surface.
            if (_micFailure is null && _systemFailure is null)
                unexpected = ex;
        }
        finally
        {
            lock (_gate)
            {
                ReleaseSession();
            }
        }

        if (unexpected is not null)
            throw new InvalidOperationException("One or more audio devices could not be stopped cleanly.", unexpected);

        return new RecorderStopResult(_micFailure, _systemFailure);
    }

    public void SetMicrophoneMuted(bool muted) => _isMicrophoneMuted = muted;

    private void ResetSession(string micPath, string systemPath)
    {
        ReleaseSession();
        LastMicPath = micPath;
        LastSystemPath = systemPath;
        _micBytesWritten = 0;
        _systemBytesWritten = 0;
        MicSilencePad = TimeSpan.Zero;
        SystemSilencePad = TimeSpan.Zero;
        _isMicrophoneMuted = false;
        _silentMicBuffer = Array.Empty<byte>();
        Volatile.Write(ref _micPeak, 0f);
        Volatile.Write(ref _systemPeak, 0f);
        _micFailure = null;
        _systemFailure = null;
        _micStopped = NewCompletionSource();
        _systemStopped = NewCompletionSource();
    }

    private void MicDataAvailable(object? sender, WaveInEventArgs args)
    {
        FillGap(_micWriter, ref _micBytesWritten, args.BytesRecorded);
        Volatile.Write(ref _micPeak, _isMicrophoneMuted ? 0f : CalculatePeak(args.Buffer, args.BytesRecorded, _micCapture?.WaveFormat));

        if (!_isMicrophoneMuted)
        {
            _micWriter?.Write(args.Buffer, 0, args.BytesRecorded);
            _micBytesWritten += args.BytesRecorded;
            return;
        }

        if (_silentMicBuffer.Length < args.BytesRecorded)
            _silentMicBuffer = new byte[args.BytesRecorded];

        _micWriter?.Write(_silentMicBuffer, 0, args.BytesRecorded);
        _micBytesWritten += args.BytesRecorded;
    }

    private void SystemDataAvailable(object? sender, WaveInEventArgs args)
    {
        FillGap(_systemWriter, ref _systemBytesWritten, args.BytesRecorded);
        Volatile.Write(ref _systemPeak, CalculatePeak(args.Buffer, args.BytesRecorded, _systemCapture?.WaveFormat));
        _systemWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        _systemBytesWritten += args.BytesRecorded;
    }

    // WASAPI loopback delivers no packets while nothing is playing, and capture devices start
    // late, so a track's byte count falls behind the wall clock. Both tracks are padded with
    // silence to a shared clock (this chunk ends "now") so they stay aligned in the mixdown.
    private void FillGap(WaveFileWriter? writer, ref long bytesWritten, int incomingBytes)
    {
        if (writer is null)
            return;

        WaveFormat format = writer.WaveFormat;
        double elapsed = (double)(Stopwatch.GetTimestamp() - _startTick) / Stopwatch.Frequency;
        long expected = (long)(elapsed * format.AverageBytesPerSecond) - incomingBytes;
        long gap = expected - bytesWritten;
        if (gap < format.AverageBytesPerSecond / 10)
            return;

        gap -= gap % format.BlockAlign;
        var silence = new byte[Math.Min(gap, 1 << 20)];
        for (long remaining = gap; remaining > 0; remaining -= silence.Length)
            writer.Write(silence, 0, (int)Math.Min(silence.Length, remaining));
        bytesWritten += gap;
    }

    private void MicRecordingStopped(object? sender, StoppedEventArgs args)
    {
        try { _micWriter?.Dispose(); }
        finally
        {
            _micWriter = null;
            if (args.Exception is not null)
            {
                _micFailure = args.Exception;
                // If we're still meant to be recording, the device dropped out on its own
                // (e.g. Bluetooth headset disconnected) rather than us calling StopRecording().
                // Warn the caller immediately instead of only surfacing this at export time.
                if (_isRecording)
                    DeviceLost?.Invoke(this, new DeviceLostEventArgs("Microphone", args.Exception));
            }

            // Always resolve successfully: the audio captured so far is already flushed to
            // disk above, and a device fault here should never fault the whole stop sequence.
            _micStopped.TrySetResult();
        }
    }

    private void SystemRecordingStopped(object? sender, StoppedEventArgs args)
    {
        try { _systemWriter?.Dispose(); }
        finally
        {
            _systemWriter = null;
            if (args.Exception is not null)
            {
                _systemFailure = args.Exception;
                if (_isRecording)
                    DeviceLost?.Invoke(this, new DeviceLostEventArgs("System audio", args.Exception));
            }

            _systemStopped.TrySetResult();
        }
    }

    private void AbortPartialStart()
    {
        _isRecording = false;
        try { _micCapture?.StopRecording(); } catch { }
        try { _systemCapture?.StopRecording(); } catch { }
        ReleaseSession();
    }

    private void ReleaseSession()
    {
        if (_micCapture is not null)
        {
            _micCapture.DataAvailable -= MicDataAvailable;
            _micCapture.RecordingStopped -= MicRecordingStopped;
            _micCapture.Dispose();
            _micCapture = null;
        }

        if (_systemCapture is not null)
        {
            _systemCapture.DataAvailable -= SystemDataAvailable;
            _systemCapture.RecordingStopped -= SystemRecordingStopped;
            _systemCapture.Dispose();
            _systemCapture = null;
        }

        _micWriter?.Dispose();
        _micWriter = null;
        _systemWriter?.Dispose();
        _systemWriter = null;
        _micDevice?.Dispose();
        _micDevice = null;
        _systemDevice?.Dispose();
        _systemDevice = null;
        Volatile.Write(ref _micPeak, 0f);
        Volatile.Write(ref _systemPeak, 0f);
    }

    private static float CalculatePeak(byte[] buffer, int count, WaveFormat? format)
    {
        if (format is null || count == 0)
            return 0f;

        float peak = 0f;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int index = 0; index + 4 <= count; index += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, index)));
        }
        else if (format.BitsPerSample == 16)
        {
            for (int index = 0; index + 2 <= count; index += 2)
                peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2)) / 32768f));
        }
        else if (format.BitsPerSample == 32)
        {
            for (int index = 0; index + 4 <= count; index += 4)
                peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(index, 4)) / 2147483648f));
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _isRecording = false;
            try { _micCapture?.StopRecording(); } catch { }
            try { _systemCapture?.StopRecording(); } catch { }
            ReleaseSession();
        }
    }
}
