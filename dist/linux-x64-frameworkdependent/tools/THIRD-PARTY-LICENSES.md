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

## 7-Zip console extractor (fetch script only, not distributed)

`scripts/fetch-tools.ps1` downloads `7zr.exe` from https://www.7-zip.org/
to extract the mpv `.7z` archive during the fetch step. It is a build-time
helper only and is not shipped with CaptureSpike. 7-Zip is LGPL-licensed.
