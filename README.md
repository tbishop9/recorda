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
├── build.ps1                 # Quick build (Debug/Release)
├── publish.ps1               # Single-file exe → dist/
├── Recorder.Core/            # The brain: WASAPI capture + merge-to-MP3
│   ├── WasapiDualRecorder.cs
│   ├── WavMerger.cs          # Despite the name, it outputs MP3. Don't @ us.
│   ├── AudioDeviceInfo.cs
│   └── Recorder.Core.csproj
└── Recorda/                  # The app: recorder console, setup, recent takes
    ├── MainWindow.xaml(.cs)
    ├── App.xaml(.cs)
    └── Recorda.csproj
```

| Project         | What it is  | What it does                                       |
|-----------------|-------------|----------------------------------------------------|
| **Recorder.Core** | Class lib   | Capture (WASAPI), merge, timing alignment, MP3 out |
| **Recorda**       | WPF app     | GUI: source meters, recording controls, setup, recent takes |

---

## Build & run

**Build:**

```powershell
dotnet build
```

Or use the helper: `.\build.ps1` (Debug by default). For a Release build: `.\build.ps1 -Configuration Release`.

**Run:**

```powershell
dotnet run --project Recorda
```

---

## How to use it

- Open **Setup** to choose the microphone, playback device, mix levels, and output folder. Recorda remembers these choices.
- Press the large **Record** button to start. Live meters show microphone and system-audio activity.
- Press **STOP** when you're done. The app merges mic + system into one MP3, deletes the temporary WAVs, and leaves only the MP3 in your output folder.
- Use **Recent Takes** to play, reveal, copy the path of, or delete a recording. Detailed events remain available under **Diagnostics**.
- Keyboard shortcuts: **Ctrl+R** records/stops, **Ctrl+M** mutes/unmutes the microphone, and **Ctrl+,** opens Setup.

---

## What you get on disk

When you stop a recording, **only one file** appears in your output folder:

| File | What it is |
|------|------------|
| `YYYY-MM-DD_HHMMSS_recording.mp3` | **The one you want.** Stereo MP3: mic and system mixed. LAME STANDARD preset. |

Raw microphone and system WAVs are written to a unique session directory in your **system temp folder**, then deleted after a successful merge. The MP3 is first encoded as a partial file and atomically renamed when complete. If export fails, the source WAVs are preserved and their location is shown under Diagnostics.

---

## Timing alignment (the boring magic)

Mic and system capture don't always start in perfect sync. The recorder measures when each stream actually starts delivering audio and, if one is late, pads it with silence so the final MP3 is aligned. You might see a log line like "mic offset +120 ms" or "no offset" — that's the app telling you it fixed the timing (or that no fix was needed).

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

- **Capture:** Two WASAPI sessions: one capture on the selected mic, one *loopback* on the selected playback device. That loopback is what gives you "everything the system is playing" without extra drivers.
- **Merge:** Both streams are normalized (sample rate, stereo), aligned with the silence padding above, mixed together, and encoded to MP3 with NAudio + LAME.

### Dependencies

| Package | Used for |
|---------|----------|
| [NAudio](https://github.com/naudio/NAudio) | WASAPI capture, WAV read, sample plumbing |
| [NAudio.Lame](https://github.com/Corey-M/NAudio.Lame) | MP3 encoding |

---

## Odds & ends

- Pick your **mic and playback device** in the app dropdowns (or set Windows defaults before starting).
- **One instance at a time** — two windows fighting over the same device will end in tears.
- If nothing is playing during recording, the system side of the MP3 will be silence; the merge still runs and you still get a valid file.
- **Recordings are gitignored** (`*.wav`, `*.mp3`) so you don't accidentally commit that karaoke session.
