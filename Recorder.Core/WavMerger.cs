using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Recorder.Core;

public static class WavMerger
{
    /// <summary>
    /// Mixes micPath and systemPath into a single stereo WAV at outputPath.
    /// Both inputs are normalized to the same sample rate and channel count
    /// before mixing. Optional silence pads align the tracks when the two
    /// captures started delivering audio at slightly different times.
    /// </summary>
    public static void Merge(
        string micPath,
        string systemPath,
        string outputPath,
        TimeSpan micPad = default,
        TimeSpan systemPad = default)
    {
        using var micReader = new AudioFileReader(micPath);
        using var sysReader = new AudioFileReader(systemPath);

        int targetSampleRate = Math.Max(micReader.WaveFormat.SampleRate, sysReader.WaveFormat.SampleRate);
        const int targetChannels = 2;

        ISampleProvider micProvider = Normalize(micReader, targetSampleRate, targetChannels);
        ISampleProvider sysProvider = Normalize(sysReader, targetSampleRate, targetChannels);

        // Prepend silence to whichever track started late
        if (micPad > TimeSpan.Zero)
            micProvider = new OffsetSampleProvider(micProvider) { DelayBy = micPad };

        if (systemPad > TimeSpan.Zero)
            sysProvider = new OffsetSampleProvider(sysProvider) { DelayBy = systemPad };

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
