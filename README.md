# Recorda

*Because sometimes you need to capture both sides of the conversation — and your cat meowing in the background.*

A Windows app that records your **microphone** and **system audio** (everything coming out of your speakers) at the same time, then squishes them into a single stereo **MP3** when you hit stop. No virtual cables, no driver voodoo — just WASAPI and a big red button.

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
├── Recorda.sln
├── Recorder.Core/            # The brain: WASAPI capture + merge-to-MP3
│   ├── WasapiDualRecorder.cs
│   ├── WavMerger.cs          # Despite the name, it outputs MP3. Don't @ us.
│   ├── AudioDeviceInfo.cs
│   └── Recorder.Core.csproj
├── Recorda/                  # The app: device pickers, REC button, timer, log
│   ├── MainWindow.xaml(.cs)
│   └── Recorda.csproj
└── publish.ps1               # Builds a single-file exe into dist/
```

| Project           | What it is  | What it does                                       |
|-------------------|-------------|----------------------------------------------------|
| **Recorder.Core** | Class lib   | Capture (WASAPI), merge, timing alignment, MP3 out |
| **Recorda**       | WPF app     | GUI: pick devices, pick folder, big REC, timer, log |

---

## Build & run

**Build everything:**

```powershell
dotnet build
```

**Run the app:**

```powershell
dotnet run --project Recorda
```

---

## How to use it

- Choose **microphone** and **playback device** (system audio source). Hit **↻ Refresh** if you plug something in.
- Set the **output folder** (default: `Documents\Recorda`). **Browse…** to change it, **Open ↗** to jump there in Explorer.
- Press the big **REC** button to start. It turns red and says **STOP**; the timer counts up.
- Press **STOP** when you're done. The app merges mic + system into one MP3, then deletes the temporary WAVs.
- **Activity log** at the bottom: double‑click a "Saved: …" line to open that file in Explorer (or select it).

---

## What you get on disk

When you stop a recording:

| File | What it is |
|------|------------|
| `YYYY-MM-DD_HHMMSS_recording.mp3` | **The one you want.** Stereo MP3: one channel from mic, one from system (mixed). LAME STANDARD preset. |

The raw `_mic.wav` and `_system.wav` files are created during recording, then **deleted automatically** after a successful merge. Only the MP3 is kept.

Files go to the folder shown in "Output folder" (default `Documents\Recorda`).

---

## Timing alignment (the boring magic)

Mic and system capture don't always start in perfect sync. The recorder measures when each stream actually starts delivering audio and, if one is late, pads it with silence so the final MP3 is aligned. You might see a log line like "mic offset +120 ms" or "no offset" — that's just the app telling you it fixed the timing (or that no fix was needed).

---

## Publishing (single-file exe)

From the repo root:

```powershell
.\publish.ps1
```

This builds a **self-contained, single-file** exe into `dist/Recorda/Recorda.exe`.

Optional: `.\publish.ps1 -Runtime win-arm64` (or another RID) if you're on something other than `win-x64`.

---

## How it works (short version)

- **Capture:** Two WASAPI sessions: one capture on the default mic, one *loopback* on the default playback device. That loopback is what gives you "everything the system is playing" without extra drivers.
- **Merge:** Both streams are normalized (sample rate, stereo), aligned with the silence padding above, mixed together, and encoded to MP3 with NAudio + LAME.

### Dependencies

| Package | Used for |
|---------|----------|
| [NAudio](https://github.com/naudio/NAudio) | WASAPI capture, WAV read, sample plumbing |
| [NAudio.Lame](https://github.com/Corey-M/NAudio.Lame) | MP3 encoding |

---

## Odds & ends

- The app uses the **default** Windows audio devices. Change them in Windows Sound settings before starting, or use the mic/playback dropdowns in the app.
- **One instance at a time** for a given device — two instances fighting over the same default mic will end in tears.
- If nothing is playing during recording, the system side of the MP3 will be silence; the merge still runs and you still get a valid file.
