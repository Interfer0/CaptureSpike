# CaptureSpike — Architecture (fresh start, Oct 2026)

Scene-and-take tutorial recorder. Recording-only (no streaming): plan scenes
like a storyboard, record multiple takes per scene, review, keep the best,
export stitched in scene order. Companion/editor-agnostic — output is plain
MP4 + per-source files.

## Mental model

```
Project/                *.csp folder
  project.json          name, output settings (resolution, fps)
  Scenes/
    01-intro/
      scene.json        id, title, order, notes (teleprompter), favoriteTakeId,
                        source layout (rect per source for compositing)
      Takes/
        20261004-223015/
          take.json     startedAt, per-source { id, kind, file, offsetMs, ... }
          screen.mp4
          webcam.mp4
          mic.wav
          system.wav
```

Every take records each enabled source to its own file. `take.json` stores each
source's `offsetMs` — milliseconds after the shared t0 when that source's first
data actually arrived. Playback/export align tracks by these offsets. This is
the validated design from the spikes: heterogeneous capture backends are fine
as long as offsets are measured, not assumed.

## Stack

- .NET 10, C#, Avalonia 11 — cross-platform desktop UI (Windows first, Linux Mint second)
- Capture: ffmpeg subprocesses everywhere, plus NAudio on Windows for mic
  (WasapiCapture) and system-audio loopback (WasapiLoopbackCapture) — ffmpeg
  has no reliable WASAPI-loopback input
- Playback: mpv as a subprocess over its JSON IPC, live-mixing tracks with a
  lavfi filter graph built from take.json offsets (`setpts`/`adelay`)
- Export compositing: SkiaSharp → raw frames piped to ffmpeg
- ffmpeg + mpv ship inside the app: `<appdir>/tools/<rid>/`, fallback PATH,
  then user override. Binaries are gitignored; `scripts/fetch-tools.*` downloads
  them (with license texts) at build time.

## Seams (engine interfaces, UI talks only to these)

- `IRecorder` — Start(scene) / Stop() → Take; per-source capturers behind
  `ISourceCapturer` with platform factories (win: gdigrab/dshow/NAudio,
  linux: x11grab/v4l2/pulse)
- `IPlaybackEngine` — Load(take), Play/Pause/Seek, SetTrackMuted(id, bool),
  Position. `MpvPlaybackEngine` now; a pre-rendered `PreviewFileEngine` can
  replace it later without touching the UI.
- `IExporter` — Export(project, takeSelector) → final MP4 (per-scene composite
  at project resolution/fps, concat in scene order, chapter per scene)

## Capture matrix (v1)

| Source  | Windows                  | Linux (Mint/X11)              |
|---------|--------------------------|-------------------------------|
| Screen  | ffmpeg gdigrab (display) | ffmpeg x11grab                |
| Window  | ffmpeg gdigrab title=    | ffmpeg x11grab window id      |
| Webcam  | ffmpeg dshow             | ffmpeg v4l2                   |
| Mic     | NAudio WasapiCapture     | ffmpeg pulse (default source) |
| System  | NAudio WasapiLoopback    | ffmpeg pulse (sink monitor)   |

Wayland screen capture (PipeWire portal) is explicitly v2 — Mint defaults to X11.

## GUI (Avalonia)

- Left: scene list (add/rename/reorder). Center: preview player (mpv embedded
  via window handle). Bottom: take strip for selected scene — Record, play,
  star favorite, delete. Scene settings: enabled sources, layout, notes.
- Recording overlay: elapsed time + per-scene notes as teleprompter.
- v2: cursor highlight, keystroke overlay, click auto-zoom, per-scene script timing.

## Milestones

- M1: solution builds; Core data model + store; App shell with scene/take lists
- M2: capture engine records real takes on Linux (testsrc fallback for headless dev)
- M3: mpv playback engine + embedded player, per-track mute/hide
- M4: export — SkiaSharp composite → MP4 per scene, concat + chapters
- M5: Windows capture paths (NAudio loopback etc.), Daniel-validated
- M6: tools bundling + single-file publish for win-x64 / linux-x64
