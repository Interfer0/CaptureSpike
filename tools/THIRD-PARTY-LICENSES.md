# Third-party tools bundled with CaptureSpike

CaptureSpike bundles the following external tools under `tools/<rid>/`
(they are fetched by `scripts/fetch-tools.*`, not stored in git).
End users receive these binaries as part of the CaptureSpike distribution;
nothing needs to be installed separately.

## mpv — GPL-2.0-or-later

- What: the mpv media player, used for in-app take preview/playback.
- Source: https://github.com/mpv-player/mpv
- Windows builds: https://github.com/shinchiro/mpv-winbuild-cmake
  (the build recommended on https://mpv.io/installation/)
- Linux: compiled from source with https://github.com/mpv-player/mpv-build
- License: GNU General Public License v2 or later.
- Source offer (GPLv2 §3): the complete corresponding source code is
  available from the repositories above. To request a copy of the exact
  sources used for a CaptureSpike release, contact the CaptureSpike
  maintainer via https://github.com/Interfer0/CaptureSpike/issues.

## ffmpeg — GPL (version depends on the build; see ffmpeg-LICENSE.txt)

- What: ffmpeg, used for screen/window/webcam/audio capture.
- Source: https://github.com/FFmpeg/FFmpeg
- Windows builds: https://www.gyan.dev/ffmpeg/builds/
- Linux builds: https://github.com/BtbN/FFmpeg-Builds
- License: the GPL text shipped next to the binary (`ffmpeg-LICENSE.txt`).
- Source offer: the complete corresponding source code is available from
  the repositories above. To request a copy of the exact sources used for
  a CaptureSpike release, contact the CaptureSpike maintainer via
  https://github.com/Interfer0/CaptureSpike/issues.

## cs-pw-helper — GPL-3.0-or-later (same as CaptureSpike)

- What: a small C program CaptureSpike compiles and bundles. On a Wayland
  session it drives `xdg-desktop-portal` and PipeWire to negotiate a
  screencast stream and writes raw frames to a Unix socket that ffmpeg
  reads. It exists because .NET cannot talk the portal/PipeWire buffer
  negotiation itself.
- Source: this repository, `installer/linux/cs-pw-helper/cs-pw-helper.c`.
  Built from source by `installer/linux/build-helper.sh` during packaging.
- License: GPL-3.0-or-later, matching CaptureSpike. It is our own source
  and ships as part of this project, so it needs no separate third-party
  grant.
- Build dependencies (needed to compile it, not redistributed):
  `build-essential`, `pkg-config`, `libpipewire-0.3-dev`, `libportal-dev`,
  `libportal-gtk3-dev`, `libgtk-3-dev`.

### Libraries cs-pw-helper links against

These are dynamically linked at runtime by the helper. They are not
bundled — the `.deb` declares them as dependencies and AppImages resolve
them from the host — but they are part of what CaptureSpike ships and are
listed here for attribution.

- **libportal** — LGPL-3.0-or-later AND LGPL-2.1-or-later.
  https://gitlab.freedesktop.org/libportal/libportal
- **PipeWire (libpipewire-0.3)** — MIT (Expat) AND LGPL-2.1-or-later.
  https://gitlab.freedesktop.org/pipewire/pipewire
- **GTK 3 / GLib** — LGPL-2.1-or-later.
  https://www.gtk.org/

All are LGPL or MIT. They are used as ordinary dynamically linked
libraries and the helper is built to allow replacement of them, which is
what keeps that arrangement conforming: none of their code is linked into
CaptureSpike, and a user with different versions installed is unaffected.

## 7-Zip console extractor (fetch script only, not distributed)

`scripts/fetch-tools.ps1` downloads `7zr.exe` from https://www.7-zip.org/
to extract the mpv `.7z` archive during the fetch step. It is a build-time
helper only and is not shipped with CaptureSpike. 7-Zip is LGPL-licensed.
