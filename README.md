# Recorda

A minimal Windows console app that simultaneously records your microphone and system audio (everything you hear) into separate WAV files, then automatically merges them into a single stereo track when recording stops.

---

## Requirements

- Windows 10 or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A microphone connected/enabled
- Audio playing through your default output device for system capture to record anything

---

## Project Structure

```
recorda/
├── CallRecorder.sln
├── Recorder.Core/          # Class library — capture and merge logic
│   ├── WasapiDualRecorder.cs
│   ├── WavMerger.cs
│   └── Recorder.Core.csproj
└── Recorder.Cli/           # Console app — UI and entry point
    ├── Program.cs
    └── Recorder.Cli.csproj
```

| Project | Type | Purpose |
|---|---|---|
| `Recorder.Core` | Class library | Audio capture via WASAPI, WAV file merging |
| `Recorder.Cli` | Console app | Keyboard-driven interface |

---

## Building

From the repo root:

```powershell
dotnet build
```

---

## Running

```powershell
dotnet run --project Recorder.Cli
```

Output on launch:

```
CallRecorder ready
Press R to start/stop recording
Press Q to quit
```

---

## Usage

| Key | Action |
|---|---|
| `R` | Start recording (if stopped) |
| `R` | Stop recording and merge tracks (if recording) |
| `Q` | Stop recording (if active), merge, then quit |

### What happens when you press R to start

Three files are created in the working directory, all sharing the same timestamp prefix:

```
2026-02-28_143045_mic.wav
2026-02-28_143045_system.wav
```

### What happens when you press R to stop (or Q)

1. Both captures are stopped and their WAV files are fully flushed to disk.
2. The two tracks are automatically merged into:

```
2026-02-28_143045_merged.wav
```

The console prints each step:

```
Stopping recording...
Recording stopped.
Merging tracks -> 2026-02-28_143045_merged.wav ...
Merged: 2026-02-28_143045_merged.wav
```

---

## Output Files

| File | Contents |
|---|---|
| `YYYY-MM-DD_HHMMSS_mic.wav` | Raw microphone capture (device native format) |
| `YYYY-MM-DD_HHMMSS_system.wav` | Raw system audio loopback (device native format) |
| `YYYY-MM-DD_HHMMSS_merged.wav` | Mixed stereo 16-bit PCM WAV |

All files are written to the directory from which you ran `dotnet run`.

---

## How It Works

### Capture (`WasapiDualRecorder`)

Both captures run simultaneously on separate WASAPI sessions:

- **Microphone** — uses `WasapiCapture` against the default recording device.
- **System audio** — uses `WasapiLoopbackCapture` against the default playback device. This captures everything rendered by Windows (music, calls, browser audio, etc.) without any virtual audio cable or driver.

Each capture writes raw PCM/IEEE-float audio directly to its WAV file via `WaveFileWriter` as data arrives. When `Stop()` is called, the app blocks until both `RecordingStopped` events have fired and both file writers have been fully flushed and closed before proceeding to the merge step.

### Merging (`WavMerger`)

Because mic and loopback devices typically use different native formats (e.g. mic: 16-bit mono 16 kHz; loopback: 32-bit IEEE float stereo 48 kHz), both tracks are normalised to a common format before mixing:

1. **Read** both files with `AudioFileReader`, which decodes any PCM/float WAV to a normalised float sample stream.
2. **Upmix** any mono track to stereo using `MonoToStereoSampleProvider`.
3. **Resample** to the higher of the two sample rates using `WdlResamplingSampleProvider` (Cockos WDL resampler — high quality).
4. **Mix** both streams with `MixingSampleProvider` (sample-accurate additive mixing).
5. **Write** the result as standard 16-bit stereo PCM WAV using `WaveFileWriter.CreateWaveFile16`.

### Dependencies

| Package | Version | Used for |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | WASAPI capture, WAV I/O, sample providers |

---

## Notes

- Both the individual track files and the merged file are kept after recording — delete whichever you don't need.
- If no audio is playing through the speakers/headphones during recording, `system.wav` will be silence but the merge will still succeed.
- The app uses the **default** Windows audio devices. To change input/output devices, set them in Windows Sound Settings before launching.
- Running multiple instances at the same time will cause the second instance to fail to open the default mic device.
