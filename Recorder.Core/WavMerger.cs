using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Recorder.Core;

public static class WavMerger
{
    /// <summary>
    /// Mixes micPath and systemPath into a single stereo WAV at outputPath.
    /// Both inputs are normalized to the same sample rate and channel count
    /// before mixing so the output is always valid stereo PCM.
    /// </summary>
    public static void Merge(string micPath, string systemPath, string outputPath)
    {
        using var micReader = new AudioFileReader(micPath);
        using var sysReader = new AudioFileReader(systemPath);

        // Use the highest sample rate of the two inputs to avoid quality loss
        int targetSampleRate = Math.Max(micReader.WaveFormat.SampleRate, sysReader.WaveFormat.SampleRate);
        // Always output stereo
        const int targetChannels = 2;

        ISampleProvider micProvider = Normalize(micReader, targetSampleRate, targetChannels);
        ISampleProvider sysProvider = Normalize(sysReader, targetSampleRate, targetChannels);

        var mixer = new MixingSampleProvider(new[] { micProvider, sysProvider });

        WaveFileWriter.CreateWaveFile16(outputPath, mixer);
    }

    private static ISampleProvider Normalize(AudioFileReader reader, int targetSampleRate, int targetChannels)
    {
        ISampleProvider provider = reader;

        // Upmix mono -> stereo before resampling so the resampler works on the correct channel count
        if (reader.WaveFormat.Channels == 1 && targetChannels == 2)
            provider = new MonoToStereoSampleProvider(provider);

        if (reader.WaveFormat.SampleRate != targetSampleRate)
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);

        return provider;
    }
}
