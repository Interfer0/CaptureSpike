#!/usr/bin/env bash
# Build the cs-pw-helper C binary for Wayland screen capture.
# Run on a Linux box (dev or build) after the dev libraries are installed.
#
# Usage:   bash installer/linux/build-helper.sh
# Output:  tools/linux-x64/cs-pw-helper
#
# The build is hermetic: we make a clean, then copy the binary into the
# tools/ directory the .deb and AppImage builders expect.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HELPER_DIR="$ROOT/installer/linux/cs-pw-helper"
OUT_DIR="$ROOT/tools/linux-x64"

echo "==> 1/4: checking dependencies"
MISSING=()
for pkg in libpipewire-0.3-dev libportal-dev pkg-config build-essential; do
    if ! dpkg -s "$pkg" >/dev/null 2>&1; then
        MISSING+=("$pkg")
    fi
done
if (( ${#MISSING[@]} )); then
    echo "Missing dev packages: ${MISSING[*]}" >&2
    echo "Install with: sudo apt install -y ${MISSING[*]}" >&2
    exit 1
fi

# libportal-gtk3 is OPTIONAL. The helper uses xdp_portal_new() (no
# GTK at runtime) — we only link against libportal-gtk3 if the user
# has it. This keeps the binary usable on headless build servers.
HAVE_GTK3=0
if dpkg -s libportal-gtk3-dev >/dev/null 2>&1; then
    HAVE_GTK3=1
fi

echo "==> 2/4: building"
cd "$HELPER_DIR"
make clean >/dev/null 2>&1 || true
if (( HAVE_GTK3 )); then
    make
else
    # The Makefile already prefers the non-gtk3 portal when
    # libportal-gtk3 is absent; we just verify.
    make
fi

echo "==> 3/4: staging"
mkdir -p "$OUT_DIR"
cp -f cs-pw-helper "$OUT_DIR/cs-pw-helper"
chmod 755 "$OUT_DIR/cs-pw-helper"

echo "==> 4/4: verifying"
"$OUT_DIR/cs-pw-helper" --help | head -5

echo
echo "Done. Binary at: $OUT_DIR/cs-pw-helper"
ls -la "$OUT_DIR/cs-pw-helper"
