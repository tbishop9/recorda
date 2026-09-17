namespace Recorder.Core;

/// <summary>
/// Raised the moment a capture device (microphone or system audio) drops out while a
/// recording is still in progress, so the UI can warn the user immediately instead of only
/// discovering it when the export fails afterwards.
/// </summary>
public sealed class DeviceLostEventArgs : EventArgs
{
    public DeviceLostEventArgs(string source, Exception exception)
    {
        Source = source;
        Exception = exception;
    }

    /// <summary>"Microphone" or "System audio".</summary>
    public string Source { get; }

    public Exception Exception { get; }
}
