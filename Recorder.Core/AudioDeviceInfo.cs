namespace Recorder.Core;

public record AudioDeviceInfo(string Id, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}
