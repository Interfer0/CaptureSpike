# AGENTS.md — working notes for agents working on CaptureSpike

This file is a hand-off note, not a substitute for reading the code. It exists
so a fresh agent (or a fresh session of any) can get productive fast without
re-deriving the design from the commit history. If the file contradicts the
code, the code wins — file an issue.

## What this app does

A scene-and-take tutorial recorder. The output is **plain MP4 per source file**
inside a Resolve-friendly folder layout; the app does no in-app editing and no
in-app export. The whole point of the project is that someone can drag the
project folder into DaVinci Resolve (or any NLE) and get straight to work on
the footage. See `docs/ARCHITECTURE.md` for the long version and `README.md`
for the short version.

Do not implement `IExporter`. It exists as an interface in `src/CaptureSpike.Core/Engines.cs`
only because early docs promised one. It is **out of scope for this project**.

## Stack

- .NET 10, C#, Avalonia 11. Windows first, Linux Mint (X11) second.
- Capture: `ffmpeg` subprocesses everywhere; `NAudio` (WASAPI) on Windows for mic and
  system-audio loopback. Set `CAPTURESPIKE_SYNTHETIC=1` to record generated test
  signals instead of real devices (used by the headless test).
- Playback: `mpv` subprocess over its JSON IPC, with a `lavfi-complex` filter graph
  built from `take.json` offsets. Tiled mode runs one mpv per source into its own
  embedded child window.
- Tool resolution order is **explicit override → bundled `tools/<os>-<rid>/` → PATH**.
  Bundled binaries are gitignored; `scripts/fetch-tools.sh` (Linux) and
  `scripts/fetch-tools.ps1` (Windows) fetch/build them.

## Repo layout

```
src/
  CaptureSpike.Core/          data model, ProjectStore, capture engine, playback engine
    Models.cs                 Project, ProjectSource, Scene, SceneSource, Take, TakeSource
    Engines.cs                IRecordingSession, IRecorder, IPlaybackEngine, IPlayer, IExporter
    ProjectStore.cs           on-disk JSON layout + atomic writes + migration
    Naming.cs                 SanitizeFileName (Windows-invalid chars explicitly), UniqueName
    Capture/                   CaptureFactory, FfmpegCapturer, NAudioCapturer, DeviceEnumeration, ToolPaths
    Playback/                 MpvPlaybackEngine, FilterGraphBuilder, MpvIpcClient
  CaptureSpike.App/           Avalonia UI (single MainWindow, viewmodel-driven)
    Program.cs / App.axaml.cs entry + tray icon + global crash handler
    MainWindow.axaml          the whole layout (scene list, palette, takes, scene setup)
    ViewModels.cs             MainViewModel; ~1800 lines; UI talks only to this
    TiledPlaybackWindow.cs / TiledTakePlayer.cs / NativeVideoHost.cs / NativeVideoInterop (mpv embeds)
  CaptureSpike.Core.Tests/    xunit v3 on Microsoft Testing Platform
docs/ARCHITECTURE.md          the long-form design doc; keep in sync with reality
scripts/fetch-tools.{sh,ps1}  ffmpeg/mpv bundling
tools/THIRD-PARTY-LICENSES.md bundled with the binary when shipping
```

## On-disk layout (the contract)

```
<projectDir>/
  project.json
  scenes/<Scene Title>/
    scene.json
    takes/Take 05/
      take.json
      <Project>-<Scene>-Take 05-<Badge>-<Label>.mkv     # one file per video source
      <Project>-<Scene>-Take 05-<Badge>-<Label>.wav     # one file per audio source
```

- Folders and filenames are **derived from UI names** (sanitized + deduped with
  `Name 2`, `Name 3`, ...). Renaming a scene in the app renames its folder
  **and** its media filenames.
- `take.json` stores each source's measured `OffsetMs` (first-data-arrival minus t0,
  accurate to ~0.1s) and an optional user-overridden `ManualOffsetMs`. Playback
  uses `EffectiveOffsetMs` (manual if set, else measured). The recorder's value
  is never overwritten by the UI.
- Take folders are named `Take 01`, `Take 02`, ... with `max+1` numbering;
  deleting a take leaves a gap instead of renumbering.
- `-favorite` suffix is appended to a take's folder name when it is starred; that
  suffix survives renames and migrations.
- Video container is **MKV**, not MP4 — a killed ffmpeg / crash / power cut
  still leaves a playable file (no `moov` atom needed at end).

## Conventions / pitfalls

- **Measured offsets, not assumed.** The whole project hinges on
  `FfmpegCapturer.FirstDataAt` and `NAudioCapturer.FirstDataAt` being accurate
  enough that 4 heterogeneous sources line up. Don't change `-stats_period 0.1`
  or `-tune zerolatency` without thinking through the offset accuracy.
- **Parallel start, fail fast.** `Recorder.StartAsync` starts every capturer at
  once with a single linked cancellation. If any one fails within ~10 s, the
  others get torn down — no half-recorded takes. Don't make it serial.
- **Atomic JSON writes.** `ProjectStore.WriteJsonAtomic` writes a unique temp
  + retries `File.Move` 6 times with increasing backoff (handles AV /
  OneDrive transient locks). Use it for every JSON write.
- **Self-healing layout.** Scene's folder name is the source of truth; if it
  disagrees with `scene.FolderName`, `LoadScene` overwrites the JSON to match
  (best-effort). Take number is parsed from the folder name, not the JSON.
- **Migration is one-way and idempotent.** `MigrateToNamedFolders` /
  `MigrateTakeFolders` / `MigrateTakeFiles` skip work that's complete and
  repair partial moves from interrupted runs. Do not add a v2 → v3 migration
  without bumping `Project.NamingVersion`.
- **mpv IPC stream disposal ordering matters.** The `MpvIpcClient.DisposeAsync`
  comment explains why: writer before reader before stream, never throw.
- **WASAPI loopback keep-alive.** `NAudioCapturer` opens a silence render
  stream on the same device so WASAPI's "no data during silence" behavior
  doesn't freeze the capture clock. Don't remove it.
- **`fltb` → `amix` / `adelay` / `setpts`** in `FilterGraphBuilder`: the
  smallest offset is subtracted from every track so playback starts immediately
  but relative alignment is preserved. Don't change the normalization without
  re-reading the offset-algebra tests.
- **Single .slnx, not .sln.** This is .NET 10; the solution file is XML.
- **`core.editor=true`** in this repo's git config: a deliberate choice so
  `git commit -e` with no message doesn't block. Don't override.

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

The notable tests:
- `RecorderTests.SyntheticTake_RecordsAllSourcesWithSaneOffsets` — headless
  end-to-end with `CAPTURESPIKE_SYNTHETIC=1`. Records 4 sources for 4 s,
  asserts files exist with sane offsets and take.json round-trips.
- `MpvEngineTests` — drives `MpvPlaybackEngine` against a Python fake-mpv that
  implements just enough of the JSON IPC to validate lifecycle, polling,
  pause/resume, mute-triggered restart, unload.
- `PlaybackTests` — `FilterGraphBuilder` output across varied layouts.
- `NamingTests`, `StoreTests`, `RenameTakeTests`, `ManualOffsetTests`,
  `BadgeTests`, `DeviceEnumerationTests` — pure-data tests of the model and
  on-disk layout.

When changing capture or playback math, the relevant test is the one to
extend first; if you add a layout case, add it to `PlaybackTests`.

## Conventions for making changes

- The UI talks only to `MainViewModel`. Don't put capture / store / engine
  calls in code-behind.
- Engine interfaces (`IRecorder`, `IPlaybackEngine`, `IPlayer`) are the only
  seam the UI knows about. Keep them small; don't leak ffmpeg/mpv types.
- New capture sources go in `Capture/`. New playback modes go in `Playback/`.
  New windows in `App/` get their own `.axaml` + `.axaml.cs`, not new tabs in
  `MainWindow.axaml` (it's already 393 lines).
- If you change the on-disk format, add a `NamingVersion` migration in
  `ProjectStore` and update `docs/ARCHITECTURE.md`. Don't break old projects.
- When adding a `--build-mpv`-tier external dependency, document it in
  `tools/THIRD-PARTY-LICENSES.md` and in `scripts/fetch-tools.{sh,ps1}`.

## What NOT to do

- Don't add an `IExporter` implementation. The project is **recording-only**;
  output editing happens in Resolve.
- Don't add a streaming / live-switching mode. Out of scope.
- Don't add a Cloud / sync layer. The project folder is the artifact.
- Don't introduce a database. JSON files on disk are the store.
- Don't change the media container from MKV to MP4 mid-recording (the
  moov-atom issue is the whole reason it's MKV).
- Don't reorder take numbering on delete. Gaps are intentional.
- Don't remove the `RecordingSession.DisposeAsync` "abandon without take"
  path. A cancelled recording must not write a half-take.

## Reference

- `docs/ARCHITECTURE.md` — long-form design + capture matrix + milestones.
  If this file disagrees with it, the architecture doc usually wins because
  it's read by humans; this file is for agents.
- `README.md` — short user-facing pitch + build command + layout.