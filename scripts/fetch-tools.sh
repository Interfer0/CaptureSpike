#!/usr/bin/env bash
# Dev helper for Linux: the app falls back to PATH, so a system install is enough.
# Bundled tools/ binaries for Linux ship inside the AppImage (M6), not via this script.
set -euo pipefail

missing=0
for bin in ffmpeg mpv; do
  if ! command -v "$bin" >/dev/null 2>&1; then
    echo "missing: $bin"
    missing=1
  fi
done

if [ "$missing" -eq 1 ]; then
  echo "Install with: sudo apt install ffmpeg mpv   (mpv is only needed from M3)"
  exit 1
fi

echo "ffmpeg: $(command -v ffmpeg)"
command -v mpv >/dev/null && echo "mpv: $(command -v mpv)"
echo "OK - the app will find these on PATH."
