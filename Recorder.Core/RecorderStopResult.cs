namespace Recorder.Core;

/// <summary>
/// Describes how each capture device behaved while the recorder was stopping. A non-null
/// failure means that track's device dropped out (e.g. disconnected) at some point, but the
/// audio captured up to that point is still on disk and safe to mix.
/// </summary>
public sealed record RecorderStopResult(Exception? MicFailure, Exception? SystemFailure)
{
    public static readonly RecorderStopResult None = new(null, null);

    public bool HasFailure => MicFailure is not null || SystemFailure is not null;
}
