#!/usr/bin/env bash
# Fetches bundled ffmpeg (+ optionally mpv) builds into tools/linux-x64/.
# Binaries are gitignored; the app finds them via ToolPaths (bundled tools/<rid>/ first).
#
# Usage:
#   bash scripts/fetch-tools.sh            # ffmpeg only (mpv falls back to PATH)
#   bash scripts/fetch-tools.sh --build-mpv  # also compile a bundled mpv from source
#
# Why build mpv from source? There is no trustworthy downloadable static mpv for
# Linux with X11 video output (needed for the embedded preview). mpv-build
# produces a binary with ffmpeg/libass statically linked; it dynamically links
# system X11/ALSA/Pulse libs, so build it on the oldest distro you ship for
# (e.g. Ubuntu 22.04 for Linux Mint 21+).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TOOLS="$ROOT/tools/linux-x64"
mkdir -p "$TOOLS"

# --- ffmpeg (BtbN static gpl build) ---
# Has x11grab + v4l2 + pulse, which johnvansickle's static build lacks.
# Resolve the asset via the API so a version bump doesn't break the URL.
FFMPEG_ASSET="$(curl -fsSL https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest \
  | python3 -c "
import json, sys
assets = json.load(sys.stdin)['assets']
names = [a['name'] for a in assets]
cands = [n for n in names if 'linux64-gpl' in n and n.endswith('.tar.xz') and 'shared' not in n]
cands.sort()
print(cands[-1])
")"
echo "ffmpeg asset: $FFMPEG_ASSET"
TMP="$(mktemp -d -p "$ROOT" .fetch-tmp.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT
curl -fsSL -o "$TMP/ffmpeg.tar.xz" \
  "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$FFMPEG_ASSET"
tar --no-same-owner -xf "$TMP/ffmpeg.tar.xz" -C "$TMP"
BIN="$(find "$TMP" -name ffmpeg -type f | head -1)"
cp "$BIN" "$TOOLS/ffmpeg"
chmod +x "$TOOLS/ffmpeg"
LIC="$(find "$TMP" -maxdepth 2 -iname 'LICENSE*' | head -1)"
[ -n "$LIC" ] && cp "$LIC" "$TOOLS/ffmpeg-LICENSE.txt"
"$TOOLS/ffmpeg" -hide_banner -version | head -1

# --- mpv ---
if [ "${1:-}" = "--build-mpv" ]; then
  echo "Building mpv from source via mpv-build (takes a while)..."
  sudo apt update
  sudo apt install -y build-essential git meson ninja-build nasm \
    libx11-dev libxext-dev libxinerama-dev libxrandr-dev libxss-dev libxv-dev \
    libxkbcommon-dev libwayland-dev wayland-protocols libasound2-dev \
    libpulse-dev libjack-dev libva-dev libvdpau-dev libgl1-mesa-dev \
    libegl1-mesa-dev libplacebo-dev libass-dev libfreetype6-dev \
    libfontconfig1-dev libfribidi-dev libharfbuzz-dev libjpeg-dev \
    liblcms2-dev libbluray-dev libdvdnav-dev libdvdread-dev libcdio-dev \
    libuchardet-dev libzimg-dev libvapoursynth-dev libvmaf-dev \
    libaom-dev libdav1d-dev libdrm-dev libgles2-mesa-dev libsdl2-dev \
    libarchive-dev libsmbclient-dev lua5.1 liblua5.1-dev libluajit-5.1-dev \
    python3-pip pkg-config yasm cmake
  BUILD="$ROOT/.mpv-build"
  if [ ! -d "$BUILD" ]; then
    git clone https://github.com/mpv-player/mpv-build.git "$BUILD"
  fi
  (cd "$BUILD" && ./rebuild -j"$(nproc)")
  cp "$BUILD/mpv/build/mpv" "$TOOLS/mpv"
  chmod +x "$TOOLS/mpv"
  cp "$ROOT/tools/THIRD-PARTY-LICENSES.md" "$TOOLS/THIRD-PARTY-LICENSES.md"
  echo "Bundled: $("$TOOLS/mpv" --version | head -1)"
else
  if command -v mpv >/dev/null 2>&1; then
    echo "mpv: using system install at $(command -v mpv) (dev fast path)."
    echo "      Run with --build-mpv to compile a bundled copy for distribution."
  else
    echo "mpv: not found on PATH and not bundled."
    echo "      Install it (sudo apt install mpv) for development, or run"
    echo "      this script with --build-mpv to compile a bundled copy."
  fi
fi

echo "Done. Binaries live in $TOOLS (gitignored)."
