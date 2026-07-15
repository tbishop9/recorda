using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Recorder.Core;

public static class WavMerger
{
    public static void Merge(
        string micPath,
        string systemPath,
        string outputPath,
        TimeSpan micPad = default,
        TimeSpan systemPad = default,
        float micGain = 0.72f,
        float systemGain = 0.72f,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var micReader = new AudioFileReader(micPath);
        using var systemReader = new AudioFileReader(systemPath);

        int targetSampleRate = Math.Max(micReader.WaveFormat.SampleRate, systemReader.WaveFormat.SampleRate);
        const int targetChannels = 2;

        ISampleProvider micProvider = new VolumeSampleProvider(Normalize(micReader, targetSampleRate, targetChannels))
        {
            Volume = Math.Clamp(micGain, 0f, 1f)
        };
        ISampleProvider systemProvider = new VolumeSampleProvider(Normalize(systemReader, targetSampleRate, targetChannels))
        {
            Volume = Math.Clamp(systemGain, 0f, 1f)
        };

        if (micPad > TimeSpan.Zero)
            micProvider = new OffsetSampleProvider(micProvider) { DelayBy = micPad };
        if (systemPad > TimeSpan.Zero)
            systemProvider = new OffsetSampleProvider(systemProvider) { DelayBy = systemPad };

        var mixer = new MixingSampleProvider(new[] { micProvider, systemProvider });
        var pcmProvider = mixer.ToWaveProvider16();
        double expectedSeconds = Math.Max(micReader.TotalTime.TotalSeconds + micPad.TotalSeconds,
            systemReader.TotalTime.TotalSeconds + systemPad.TotalSeconds);

        using var mp3Writer = new LameMP3FileWriter(outputPath, pcmProvider.WaveFormat, LAMEPreset.STANDARD);
        var buffer = new byte[pcmProvider.WaveFormat.AverageBytesPerSecond];
        long bytesProcessed = 0;
        int read;
        while ((read = pcmProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mp3Writer.Write(buffer, 0, read);
            bytesProcessed += read;
            if (expectedSeconds > 0)
                progress?.Report(Math.Min(1d, bytesProcessed / (pcmProvider.WaveFormat.AverageBytesPerSecond * expectedSeconds)));
        }

        progress?.Report(1d);
    }

    private static ISampleProvider Normalize(AudioFileReader reader, int targetSampleRate, int targetChannels)
    {
        ISampleProvider provider = reader;
        if (reader.WaveFormat.Channels == 1 && targetChannels == 2)
            provider = new MonoToStereoSampleProvider(provider);
        if (reader.WaveFormat.SampleRate != targetSampleRate)
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        return provider;
    }
}
