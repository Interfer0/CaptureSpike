# CaptureSpike

A recording-only, scene-and-take tutorial recorder. Think storyboard, not live
switcher: plan scenes, record multiple takes per scene, keep the best, then
hand the project folder straight to DaVinci Resolve (or any NLE) for editing.

The app **does not export or edit**. The output is a plain folder of
self-describing media files — one per source per take — laid out so it
imports cleanly:

```
My Tutorial/
  project.json
  Scenes/
    01-intro/
      scene.json
      Takes/
        Take 01/
          take.json
          My Tutorial-01-intro-Take 01-SC-Screen.mkv
          My Tutorial-01-intro-Take 01-WE-Webcam.mkv
          My Tutorial-01-intro-Take 01-MI-Microphone.wav
          My Tutorial-01-intro-Take 01-SY-System Audio.mkv
```

Each take records every enabled source to its own file. `take.json` stores
each source's measured offset (milliseconds after the shared t0 when its first
data arrived) so Resolve can line up tracks without any guesswork. Sources
that the scene doesn't reference are skipped; a take starrable as the scene's
favorite keeps a `-favorite` suffix on its folder.

Built with C#, .NET 10 and Avalonia (Windows first, Linux second).
See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the long-form design.

## Build

```sh
dotnet build CaptureSpike.slnx
dotnet run --project src/CaptureSpike.App
```

## Tests (xunit v3 on Microsoft Testing Platform)

```sh
dotnet build src/CaptureSpike.Core.Tests
DOTNET_ROOT=~/.dotnet ./src/CaptureSpike.Core.Tests/bin/Debug/net10.0/CaptureSpike.Core.Tests
```

`ffmpeg` (and later `mpv`) are used as external subprocesses — see
`docs/ARCHITECTURE.md` for the `tools/` layout. Set `CAPTURESPIKE_SYNTHETIC=1`
to record generated test signals instead of real devices (used by the headless
end-to-end test).

## Layout

- `src/CaptureSpike.Core` — data model, `ProjectStore`, capture engine,
  playback engine
- `src/CaptureSpike.Core.Tests` — xunit tests (synthetic recorder
  round-trip, fake-mpv engine tests, naming, migration)
- `src/CaptureSpike.App` — Avalonia GUI
- `docs/` — architecture notes
- `scripts/` — tool-fetch helpers (ffmpeg / mpv bundling)
- `.github/AGENTS.md` — notes for AI coding agents working on the codebase

## For AI agents

Read `.github/AGENTS.md` before making changes. The short version: this is a
recording-only app, the on-disk layout is the contract, the measured offsets
in `take.json` are sacred, and **don't implement an exporter** — the project
is deliberately export-free because Resolve is the editor.