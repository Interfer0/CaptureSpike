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

First public release of CaptureSpike. A recording-only,
scene-and-take video + audio recorder for Linux Mint and
Windows. Plan a scene, record multiple takes, hand the
project folder straight to DaVinci Resolve for editing.

**This is an alpha.** Expect rough edges. Test on a
project you can re-record; do not use for anything
unrecoverable yet.

#### What's working

**Multi-source recording (Windows and Linux X11):**
- Screen capture at your project's frame rate
- Microphone capture (any input device NAudio / PulseAudio
  exposes)
- System audio capture on Windows (WASAPI loopback)
- Per-take editor that re-aligns sources by prepending
  silence to shorter ones (no re-encoding, no quality loss,
  preserves the original audio codec — 32-bit float stays
  32-bit float)

**Project model:**
- Per-project `project.json` with the source list (separate
  from the global palette so opening a project never changes
  your master source list)
- Master `palette.json` lives at `~/Videos/CaptureSpike/`
  — add, remove, or rename sources here and they appear in
  every project
- Per-scene "sources in scene" list (so you can have a mic
  active for one scene and not another)
- Per-take note (markdown editor)
- One-click **Unnormalize** to revert a take to original files

**Review workflow:**
- Single-mpv tiled playback — runs all takes' video sources
  in one mpv instance so the audio clock is shared and the
  videos stay in sync (no per-process drift)
- Per-source mute / include in the player strip

**Installer (Windows):**
- Per-user Inno Setup installer (no admin required)
- Bundles .NET 10 runtime, ffmpeg, and mpv — no need to
  install anything else
- Start Menu and optional Desktop shortcut
- Installs LICENSE.txt and NOTICE alongside the .exe
- Shows the GPL-3.0 license text in the wizard

**Installer (Linux):**
- AppImage (single file, no install, `chmod +x` and run)
- `.deb` package for Debian / Ubuntu / Linux Mint
- Both bundle .NET 10 + ffmpeg; mpv is expected from the
  system package manager (no trustworthy static mpv build
  for Linux with X11 video output)
- X11 only (Wayland screen capture is not yet supported —
  the app needs an X11 session)

#### Known limitations

- **Linux: X11 only.** Wayland sessions will fail at the
  ffmpeg x11grab step. The Settings dialog's session type
  should help you confirm.
- **Linux: no bundled mpv.** Install `mpv` from your
  package manager (`sudo apt install mpv`).
- **Windows: SmartScreen warning.** The .exe is not
  code-signed yet (signing costs ~$120/year for an Azure
  Trusted Signing subscription; open-source projects often
  skip it). Click "More info" → "Run anyway" or unblock
  the .exe in Properties. The README explains this in
  detail.
- **No auto-updater.** New versions are published as new
  installers on GitHub Releases; you'll re-download when
  you want to upgrade.
- **mpv and ffmpeg are bundled and not user-replaceable**
  in the installed app. If a future version of ffmpeg
  fixes a bug, you'll need to wait for a new release.
- **Single project at a time.** The app opens one project
  at a time; switching projects is by opening from the
  project browser.
- **No Wayland, no macOS.** Both are on the roadmap but
  not implemented yet.

#### Reporting issues

If something doesn't work:

1. Click **Open log** in the app's footer to open the
   log folder.
2. Attach the most recent `.log` file to a GitHub issue:
   https://github.com/Interfer0/CaptureSpike/issues/new
3. Include:
   - Your OS and version (Windows 10/11, Linux Mint 21.x)
   - The version of CaptureSpike (visible in the title
     bar of the app and in `Settings → About`)
   - A description of what you were doing when it broke
   - The log file

For "where do I get help" questions, use GitHub
Discussions: https://github.com/Interfer0/CaptureSpike/discussions

#### License and commercial use

CaptureSpike is open source under the
[GNU General Public License v3.0 or later](https://www.gnu.org/licenses/gpl-3.0.html).
You can use, modify, and redistribute it under GPL-3.0
terms (which requires sharing source for any
redistributed version).

A separate **commercial license** is available for
companies that want to embed CaptureSpike in a
proprietary product without the GPL-3.0
source-availability obligation. To inquire, open a
GitHub issue with `[commercial]` in the title.
Details: [COMMERCIAL.md](../COMMERCIAL.md).

#### Thanks

Thanks to anyone who tested pre-release builds, to the
open-source projects that CaptureSpike stands on
shoulders of (.NET, Avalonia, ffmpeg, mpv), and to the
users willing to try an alpha and report what breaks.

— Daniel Bigelow

<!--
For the next release, copy the section above, bump the
version, and edit the bullets.
-->
