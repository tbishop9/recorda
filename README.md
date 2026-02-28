# Recorda

*Because sometimes you need to capture both sides of the conversation — and your cat meowing in the background.*

A Windows app that records your **microphone** and **system audio** (everything coming out of your speakers) at the same time, then squishes them into a single stereo **MP3** when you hit stop. No virtual cables, no driver voodoo — just WASAPI and a big red button (or a keyboard, if you're that kind of nerd).

You get **two ways to run it**: a friendly WPF window with device pickers and a timer, or a no-nonsense console app. Same engine under the hood.

---

## What you need

- **Windows 10** or later
- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** (to build) or the runtime (if you're running a published build)
- A **microphone** that Windows can see
- Something actually **playing** through your default output if you want system audio on the recording (otherwise you'll get a very quiet system track and a lot of you)

---

## Project layout

```
recorda/
├── CallRecorder.sln          # The solution (yes, the folder is "recorda" — we're not picky)
├── Recorder.Core/            # The brain: WASAPI capture + merge-to-MP3
│   ├── WasapiDualRecorder.cs
│   ├── WavMerger.cs          # Despite the name, it outputs MP3. Don't @ us.
│   ├── AudioDeviceInfo.cs
│   └── Recorder.Core.csproj
├── Recorder.Cli/             # Console app: R to record, Q to quit
│   ├── Program.cs
│   └── Recorder.Cli.csproj
├── Recorder.Wpf/             # The pretty one: device pickers, REC button, timer, log
│   ├── MainWindow.xaml(.cs)
│   └── Recorder.Wpf.csproj
└── publish.ps1               # Builds single-file exes into dist/
```

| Project         | What it is   | What it does                                      |
|-----------------|-------------|----------------------------------------------------|
| **Recorder.Core** | Class lib   | Capture (WASAPI), merge, timing alignment, MP3 out |
| **Recorder.Cli**  | Console app | Keyboard-driven: R = record/stop, Q = quit         |
| **Recorder.Wpf**  | WPF app     | GUI: pick devices, pick folder, big REC, timer, log |

---

## Build & run

**Build everything:**

```powershell
dotnet build
```

**Run the GUI (recommended for most humans):**

```powershell
dotnet run --project Recorder.Wpf
```

**Run the CLI (for keyboard warriors):**

```powershell
dotnet run --project Recorder.Cli
```

You'll see something like:

```
CallRecorder ready
Press R to start/stop recording
Press Q to quit
```

*(We kept the old codename in the CLI. It's nostalgic.)*

---

## How to use it

### WPF app

- Choose **microphone** and **playback device** (system audio source). Hit **↻ Refresh** if you plug something in.
- Set the **output folder** (default: `Documents\Recorda`). **Browse…** to change it, **Open ↗** to jump there in Explorer.
- Press the big **REC** button to start. It turns red and says **STOP**; the timer counts up.
- Press **STOP** when you're done. The app merges mic + system into one MP3, then deletes the temporary WAVs.
- **Activity log** at the bottom: double‑click a “Saved: …” line to open that file in Explorer (or select it).

### CLI

| Key | Action |
|-----|--------|
| **R** | Start recording (if idle) |
| **R** | Stop recording, merge to MP3, then exit (if recording) |
| **Q** | Stop (if recording), merge, then quit |

So: one recording per run. Press R to start, R again (or Q) to stop and get your file.

---

## What you get on disk

When you stop a recording:

| File | What it is |
|------|------------|
| `YYYY-MM-DD_HHMMSS_recording.mp3` | **The one you want.** Stereo MP3: one channel from mic, one from system (mixed). LAME STANDARD preset. |

The raw `_mic.wav` and `_system.wav` files are created during recording, then **deleted automatically** after a successful merge. Only the MP3 is kept.

**Where files go:**

- **WPF:** The folder shown in “Output folder” (default `Documents\Recorda`).
- **CLI:** The current working directory of the process.

---

## Timing alignment (the boring magic)

Mic and system capture don’t always start in perfect sync. The recorder measures when each stream actually starts delivering audio and, if one is late, pads it with silence so the final MP3 is aligned. You might see a log line like “mic offset +120 ms” or “no offset” — that’s just the app telling you it fixed the timing (or that no fix was needed).

---

## Publishing (single-file exes)

From the repo root:

```powershell
.\publish.ps1
```

This builds **self-contained, single-file** exes into `dist/`:

- `dist/Recorda.UI/Recorda.UI.exe` — the WPF app
- `dist/Recorda.CLI/Recorda.CLI.exe` — the console app

Optional: `.\publish.ps1 -Runtime win-arm64` (or another RID) if you're on something other than `win-x64`.

---

## How it works (short version)

- **Capture:** Two WASAPI sessions: one capture on the default mic, one *loopback* on the default playback device. That loopback is what gives you “everything the system is playing” without extra drivers.
- **Merge:** Both streams are normalized (sample rate, stereo), aligned with the silence padding above, mixed together, and encoded to MP3 with NAudio + LAME.

### Dependencies

| Package | Used for |
|---------|----------|
| [NAudio](https://github.com/naudio/NAudio) | WASAPI capture, WAV read, sample plumbing |
| [NAudio.Lame](https://github.com/Corey-M/NAudio.Lame) | MP3 encoding |

---

## Odds & ends

- The app uses the **default** Windows audio devices. Change them in Windows Sound settings before starting, or in the WPF UI (mic/playback dropdowns).
- **One instance at a time** for a given device — two instances fighting over the same default mic will end in tears.
- If nothing is playing during recording, the system side of the MP3 will be silence; the merge still runs and you still get a valid file.
