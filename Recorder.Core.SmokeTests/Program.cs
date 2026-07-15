using NAudio.Wave;
using Recorder.Core;

string testDirectory = Path.Combine(Path.GetTempPath(), $"recorda-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(testDirectory);

try
{
    string microphoneFile = Path.Combine(testDirectory, "mic.wav");
    string systemFile = Path.Combine(testDirectory, "system.wav");
    string outputFile = Path.Combine(testDirectory, "mixed.mp3");
    WriteTone(microphoneFile, 44_100, 1, 440);
    WriteTone(systemFile, 48_000, 2, 660);

    var progress = new InlineProgress();
    WavMerger.Merge(
        microphoneFile,
        systemFile,
        outputFile,
        micPad: TimeSpan.FromMilliseconds(40),
        systemPad: TimeSpan.Zero,
        micGain: 0.6f,
        systemGain: 0.6f,
        progress: progress);

    Assert(File.Exists(outputFile), "The MP3 output was not created.");
    Assert(new FileInfo(outputFile).Length > 1_000, "The MP3 output is unexpectedly small.");
    Assert(progress.LastValue >= 0.999, "Export progress did not reach completion.");

    using var reader = new Mp3FileReader(outputFile);
    Assert(reader.TotalTime > TimeSpan.FromMilliseconds(900), "The output duration is too short.");
    Assert(reader.WaveFormat.Channels == 2, "The output is not stereo.");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    bool cancellationObserved = false;
    try
    {
        WavMerger.Merge(microphoneFile, systemFile, Path.Combine(testDirectory, "cancelled.mp3"),
            cancellationToken: cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        cancellationObserved = true;
    }

    Assert(cancellationObserved, "A cancelled export did not stop.");
    Console.WriteLine("Recorder.Core smoke tests passed.");
}
finally
{
    try { Directory.Delete(testDirectory, true); } catch { }
}

static void WriteTone(string path, int sampleRate, int channels, double frequency)
{
    var format = new WaveFormat(sampleRate, 16, channels);
    using var writer = new WaveFileWriter(path, format);
    const double amplitude = 0.25;
    for (int frame = 0; frame < sampleRate; frame++)
    {
        short sample = (short)(Math.Sin(2 * Math.PI * frequency * frame / sampleRate) * short.MaxValue * amplitude);
        for (int channel = 0; channel < channels; channel++)
        {
            writer.WriteByte((byte)(sample & 0xff));
            writer.WriteByte((byte)((sample >> 8) & 0xff));
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class InlineProgress : IProgress<double>
{
    public double LastValue { get; private set; }
    public void Report(double value) => LastValue = value;
}
