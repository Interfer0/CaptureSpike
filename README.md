# CaptureSpike

A recording-only, scene-and-take tutorial recorder. Think storyboard, not live
switcher: plan scenes, record multiple takes per scene, keep the best, export
them stitched together in scene order. No streaming, no editor — output is
plain MP4 plus per-source files you can fix up afterward.

Built with C#, .NET 10 and Avalonia (Windows first, Linux second).

## Status

M1 — solution builds; project/scene/take data model on disk (`take.json` with
per-source offsets); app shell with scene list, take list, favorites.
Recording (M2), playback (M3) and export (M4) are still ahead. See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build

```sh
dotnet build CaptureSpike.slnx
dotnet run --project src/CaptureSpike.App
```

Tests (xunit v3 on Microsoft Testing Platform — the test assembly runs directly):

```sh
dotnet build src/CaptureSpike.Core.Tests
DOTNET_ROOT=~/.dotnet ./src/CaptureSpike.Core.Tests/bin/Debug/net10.0/CaptureSpike.Core.Tests
```

`ffmpeg` (and later `mpv`) are used as bundled subprocesses — see
`docs/ARCHITECTURE.md` for the `tools/` layout. Set `CAPTURESPIKE_SYNTHETIC=1`
to record generated test signals instead of real devices (headless dev).

## Layout

- `src/CaptureSpike.Core` — data model, `ProjectStore`, capture engine, engine interfaces
- `src/CaptureSpike.Core.Tests` — xunit tests (synthetic recorder round-trip)
- `src/CaptureSpike.App` — Avalonia GUI
- `docs/` — architecture notes
- `scripts/` — tool-fetch helpers (ffmpeg/mpv bundling)
