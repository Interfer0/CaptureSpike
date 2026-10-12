# Release process

This is the step-by-step recipe for cutting a new CaptureSpike
release. The release-drafter workflow auto-generates the
changelog from merged PRs, so most of the work is bookkeeping.

## Versioning

`src/CaptureSpike.App/CaptureSpike.App.csproj` has the
`<Version>` element. Update it before cutting the release:

- **Pre-release / alpha:** `0.1.0-alpha.1`, `0.1.0-alpha.2`, ...
- **Pre-release / beta:** `0.1.0-beta.1`, `0.1.0-beta.2`, ...
- **Stable:** `0.1.0`, `0.2.0`, `1.0.0` (no suffix)

The first public release is `0.1.0-alpha.1`.

## Pre-release checklist

1. [ ] Update `<Version>` in `src/CaptureSpike.App/CaptureSpike.App.csproj`
2. [ ] Update "What's new" section in `docs/RELEASING.md` below
      (this file). Two-paragraph summary that goes at the
      top of the GitHub release. **Write this by hand —
      the auto-generated changelog lists PRs, but a
      human summary explains what users should care about.**
3. [ ] Run the test suite locally:
      `dotnet build src/CaptureSpike.Core.Tests` then
      `dotnet CaptureSpike.Core.Tests/bin/Debug/net10.0/CaptureSpike.Core.Tests.dll -parallel none`
4. [ ] Build the installer on Windows:
      `powershell -ExecutionPolicy Bypass -File installer/windows/build-installer.ps1`
5. [ ] Test the installer on a clean Windows VM (or your
      own machine — uninstall first to get a clean test).
      - Start Menu shortcut works
      - Desktop shortcut works (if checked)
      - App launches and records
      - ffmpeg + mpv found (check the log: should NOT say
        "ffmpeg not found")
      - Uninstall leaves no orphan files in
        `%LOCALAPPDATA%\CaptureSpike`
6. [ ] Compute the SHA256 hash and put it in the release
      notes (the build script writes
      `CaptureSpike-Setup-X.Y.Z.exe.sha256` next to the
      installer — open that file and copy the hash).
7. [ ] Push the version bump: `git tag v0.1.0-alpha.1 && git push --tags`
8. [ ] Wait for the `build-windows-installer` GitHub Action
      to finish (the tag push triggers it).
9. [ ] Download the artifact from the Actions run.
10. [ ] Verify the hash matches the locally-built installer.
11. [ ] Go to GitHub → Releases → click the draft
      (release-drafter already created one).
12. [ ] Edit the release notes:
      - Tick **This is a pre-release** (alpha/beta)
      - Add the "What's new" human summary at the top
      - Attach the .exe and .sha256 files
      - Publish

## What's new (human-written summaries)

### 0.1.0-alpha.1 — initial alpha

First public release of CaptureSpike. Records multiple
sources in sync, with a per-take editor that re-aligns
audio by prepending silence (no re-encoding, no quality
loss).

**Known limitations:**

- **Linux:** build-from-source only, no signed package
- **SmartScreen warning** on Windows (unsigned publisher;
  click "More info" → "Run anyway")
- **No auto-updater** — re-download the installer for new
  versions
- The normalize pass uses pure duration matching; silence
  detection is not configurable

**What's working:**

- Multi-source recording (screen, mic, system audio) on
  Windows
- Per-take normalize: `NORM-` prefix on output files so
  they sort to the top in DaVinci Resolve
- Project palette is independent of the global sources
  list (changes to one don't affect the other)
- Per-project source list lives in `project.json`; the
  master list lives in `palette.json`
- Undo normalize button on each take row
- Single-mpv tiled playback for synced review

**Feedback:** file an issue at
https://github.com/Interfer0/CaptureSpike/issues with the
log file attached (the **Open log** button in the app).

<!--
For the next release, copy the section above, bump the
version, and edit the bullets.
-->
