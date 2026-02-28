using System;
using Recorder.Core;

Console.WriteLine("CallRecorder ready");
Console.WriteLine("Press R to start/stop recording");
Console.WriteLine("Press Q to quit");

using var recorder = new WasapiDualRecorder();
string? currentMicFile = null;
string? currentSystemFile = null;

while (true)
{
    var keyInfo = Console.ReadKey(intercept: true);
    var key = keyInfo.Key;

    if (key == ConsoleKey.Q)
    {
        if (recorder.IsRecording)
        {
            StopAndMerge();
        }
        break;
    }
    else if (key == ConsoleKey.R)
    {
        if (recorder.IsRecording)
        {
            StopAndMerge();
            break;
        }
        else
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            currentMicFile = $"{timestamp}_mic.wav";
            currentSystemFile = $"{timestamp}_system.wav";

            try
            {
                recorder.Start(currentMicFile, currentSystemFile);
                Console.WriteLine($"Recording started: {currentMicFile}, {currentSystemFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting recording: {ex.Message}");
                currentMicFile = null;
                currentSystemFile = null;
            }
        }
    }
}

Console.WriteLine();
Console.Write("Press Enter to exit...");
Console.ReadLine();

void StopAndMerge()
{
    Console.WriteLine("Stopping recording...");
    recorder.Stop();
    Console.WriteLine("Recording stopped.");

    if (currentMicFile is null || currentSystemFile is null) return;

    string mergedFile = currentMicFile.Replace("_mic.wav", "_merged.wav");
    if (recorder.MicSilencePad > TimeSpan.Zero)
        Console.WriteLine($"Timing offset: mic delayed by {recorder.MicSilencePad.TotalMilliseconds:F0} ms — padding mic track.");
    else if (recorder.SystemSilencePad > TimeSpan.Zero)
        Console.WriteLine($"Timing offset: system delayed by {recorder.SystemSilencePad.TotalMilliseconds:F0} ms — padding system track.");
    else
        Console.WriteLine("Timing offset: none detected.");

    Console.WriteLine($"Merging tracks -> {mergedFile} ...");

    try
    {
        WavMerger.Merge(currentMicFile, currentSystemFile, mergedFile, recorder.MicSilencePad, recorder.SystemSilencePad);
        Console.WriteLine($"Merged: {mergedFile}");

        try { File.Delete(currentMicFile); } catch { }
        try { File.Delete(currentSystemFile); } catch { }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Merge failed: {ex.Message}");
    }

    currentMicFile = null;
    currentSystemFile = null;
}
