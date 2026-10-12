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

## Development Setup

Install the .NET 10 SDK. The app also launches `ffmpeg` for capture and
normalization and `mpv` for playback; install both and put them on `PATH`, or
fetch platform builds into the ignored `tools/` directory as described below.

```sh
dotnet build CaptureSpike.slnx
dotnet run --project src/CaptureSpike.App
```

### Linux Mint

On Linux Mint with an X11 session, the repository helper installs the runtime
libraries, fonts, `ffmpeg`, and `mpv`, checks for the .NET 10 SDK, and builds
the app:

```sh
bash scripts/install-linux.sh
```

To check dependencies without installing packages, run
`bash scripts/install-linux.sh --verify`. To install, build, and launch, run
`bash scripts/install-linux.sh --launch`. The script uses `sudo apt` and is a
developer setup helper, not an end-user installer. Linux screen capture and
preview currently require X11; Wayland capture is not supported.

### Windows

Install the .NET 10 SDK. Fetch the Windows tools from the repository root when
you want them bundled with the app instead of installed separately:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1
dotnet build CaptureSpike.slnx
dotnet run --project src/CaptureSpike.App -f net10.0-windows10.0.19041
```

The Windows app uses NAudio for microphone and system-audio capture. The
Windows target is `net10.0-windows10.0.19041`; the plain `net10.0` target is
used on Linux.

## Tests (xunit v3 on Microsoft Testing Platform)

Linux/macOS:

```sh
dotnet build src/CaptureSpike.Core.Tests
DOTNET_ROOT=~/.dotnet ./src/CaptureSpike.Core.Tests/bin/Debug/net10.0/CaptureSpike.Core.Tests
```

Windows PowerShell:

```powershell
dotnet build src/CaptureSpike.Core.Tests
& .\src\CaptureSpike.Core.Tests\bin\Debug\net10.0\CaptureSpike.Core.Tests.exe
```

Set `CAPTURESPIKE_SYNTHETIC=1` to record generated test signals instead of
real devices (used by the headless end-to-end test).

## Publishing Builds

These commands create portable publish folders, not installers. Run the tool
fetch script for the target platform first if the release should include
`ffmpeg` and `mpv`; the fetched binaries are ignored by Git and must be present
on the build machine. Keep the corresponding license files and source
information with any distributed tool binaries; see
[`tools/THIRD-PARTY-LICENSES.md`](tools/THIRD-PARTY-LICENSES.md).

### Windows x64 — installer (.exe wizard)

Run in PowerShell from the repository root. The script fetches Inno Setup
into `tools/inno-setup/` if it's not already there, fetches ffmpeg + mpv
into `tools/win-x64/`, runs `dotnet publish` self-contained, and finally
runs `iscc` to produce the installer:

```powershell
powershell -ExecutionPolicy Bypass -File installer/windows/build-installer.ps1
```

Output: `dist\CaptureSpike-Setup-<version>.exe`. Per-user install
(no UAC prompt) into `%LOCALAPPDATA%\CaptureSpike`. Creates a Start Menu
shortcut; an optional Desktop shortcut is off by default. The
`.iss` script and CI workflow are in
[`installer/windows/`](installer/windows/) and
[`.github/workflows/build-windows-installer.yml`](.github/workflows/build-windows-installer.yml).

A signed installer and Microsoft Store MSIX package are not configured
yet; Store release work also needs package identity/manifest
configuration, signing, and Partner Center submission.

### Windows x64 — portable folder

Run in PowerShell from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1
dotnet publish src/CaptureSpike.App/CaptureSpike.App.csproj `
  -c Release -f net10.0-windows10.0.19041 -r win-x64 `
  --self-contained true -o artifacts/win-x64
```

The publish folder includes the .NET runtime and any fetched tools. Zip that
folder for a portable test build. A signed installer and Microsoft Store
MSIX package are not configured yet; Store release work also needs package
identity/manifest configuration, signing, and Partner Center submission.

### Linux Mint x64

Run on Linux Mint (or a compatible Linux build host) from the repository root:

```sh
bash scripts/fetch-tools.sh
PATH="$PWD/tools/linux-x64:$PATH" dotnet publish src/CaptureSpike.App/CaptureSpike.App.csproj \
  -c Release -f net10.0 -r linux-x64 --self-contained true \
  -o artifacts/linux-x64
```

The default fetch script downloads `ffmpeg`; `mpv` is taken from `PATH` unless
you explicitly build and bundle it with `bash scripts/fetch-tools.sh
--build-mpv`. Validate the published folder on a clean Mint X11 machine before
sharing it. The current `.desktop` launcher invokes `dotnet`, and no `.deb`,
AppImage, or Flatpak installer is generated yet; a self-contained Linux
release needs a launcher that starts the published app executable directly,
plus packaging and install/uninstall testing.

The project has no configured release automation or Store/Flathub submission
workflow yet. Treat published folders as test artifacts until those packaging,
license, signing, and clean-machine checks are complete.

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