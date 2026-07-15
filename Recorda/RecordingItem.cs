using System.IO;

namespace Recorda;

internal sealed record RecordingItem(
    string FilePath,
    string Name,
    string Details,
    DateTime RecordedAt)
{
    public static RecordingItem FromFile(string filePath, TimeSpan? duration = null)
    {
        var info = new FileInfo(filePath);
        string durationText = duration is { } value ? FormatDuration(value) + "  ·  " : string.Empty;
        string details = $"{durationText}{FormatSize(info.Length)}  ·  {info.LastWriteTime:ddd d MMM, h:mm tt}";
        return new RecordingItem(filePath, Path.GetFileNameWithoutExtension(filePath), details, info.LastWriteTime);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");

    private static string FormatSize(long bytes)
    {
        double megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1 ? $"{megabytes:0.0} MB" : $"{bytes / 1024d:0} KB";
    }
}
