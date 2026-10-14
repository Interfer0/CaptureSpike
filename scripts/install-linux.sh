#!/usr/bin/env bash
# Installs everything needed to run CaptureSpike.App on a Linux Mint
# (Ubuntu-based) desktop with X11.
#
# Usage:
#   bash scripts/install-linux.sh                # install only
#   bash scripts/install-linux.sh --verify       # just check, don't change anything
#   bash scripts/install-linux.sh --launch       # install + launch the GUI after
#
# What it does:
#   1. Installs Avalonia runtime deps (libx11, libice, libsm,
#      libfontconfig) and a font (fonts-noto — without one, Avalonia
#      text renders as blank rectangles).
#   2. Installs ffmpeg + mpv from apt. The app finds tools on PATH
#      (ToolPaths) — bundling into tools/linux-x64/ is optional and
#      done separately by scripts/fetch-tools.sh.
#   3. Verifies dotnet 10 SDK is present.
#   4. Builds the project for net10.0.
#
# Requires sudo on apt-get; the script bails early if not on Linux.

set -euo pipefail

VERIFY_ONLY=0
LAUNCH_AFTER=0
for arg in "$@"; do
    case "$arg" in
        --verify)  VERIFY_ONLY=1 ;;
        --launch)  LAUNCH_AFTER=1 ;;
        -h|--help)
            head -16 "$0"
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg"
            echo "Use --verify (check only) or --launch (install + run)."
            exit 2
            ;;
    esac
done

# --- OS check ---
if ! command -v apt-get >/dev/null 2>&1; then
    echo "ERROR: this script targets Debian/Ubuntu/Mint (needs apt-get)."
    echo "For other distros, install the equivalents of:"
    echo "  libx11-6 libice6 libsm6 libfontconfig1 fonts-noto"
    echo "  ffmpeg mpv"
    exit 1
fi

# --- APT packages ---
APT_PACKAGES=(
    # Avalonia 12 X11 runtime:
    libx11-6
    libice6
    libsm6
    libfontconfig1
    # Avalonia text rendering needs at least one font family:
    fonts-noto
    # Tools the app shells out to:
    ffmpeg
    mpv
    # Wayland screen capture: the bundled cs-pw-helper talks to
    # the portal and PipeWire, so it needs both at runtime. A
    # portal *backend* is also required and is not listed here
    # because it is desktop-specific (gnome/kde/wlroots).
    xdg-desktop-portal
    libportal1
)

if [ "$VERIFY_ONLY" = 0 ]; then
    echo "==> apt-get update"
    sudo apt-get update
    # PipeWire and GTK3 both got "-t64" suffixes in Ubuntu 24.04 /
    # Mint 22, and the old names no longer exist there while the new
    # ones do not exist before it. Resolve whichever this system has
    # rather than naming one and breaking the other.
    for pair in \
        "libpipewire-0.3-0t64 libpipewire-0.3-0" \
        "libgtk-3-0t64 libgtk-3-0"
    do
        for cand in $pair; do
            if apt-cache show "$cand" >/dev/null 2>&1; then
                APT_PACKAGES+=("$cand")
                break
            fi
        done
    done
    echo "==> apt-get install ${APT_PACKAGES[*]}"
    sudo apt-get install -y "${APT_PACKAGES[@]}"
fi

# --- Tool verification ---
echo
echo "==> Verifying installed tools"

fail=0
check() {
    local cmd="$1"
    local label="$2"
    if command -v "$cmd" >/dev/null 2>&1; then
        local path
        path="$(command -v "$cmd")"
        echo "  OK  $label: $path"
    else
        echo "  MISSING  $label"
        fail=1
    fi
}

check ffmpeg    "ffmpeg (recording engine)"
check mpv       "mpv (playback preview)"
check dotnet    "dotnet 10 SDK"

# --- .NET version ---
if command -v dotnet >/dev/null 2>&1; then
    SDK_VER="$(dotnet --version 2>/dev/null || true)"
    echo "  dotnet version: $SDK_VER"
    case "$SDK_VER" in
        10.*) echo "  OK   .NET 10 SDK" ;;
        *)    echo "  WARN .NET 10 not found; install via https://dot.net/ or 'sudo apt install dotnet-sdk-10.0'" ;;
    esac
fi

# --- Library presence ---
echo
echo "==> Verifying Avalonia X11 runtime libs"
for lib in libx11-6 libice6 libsm6 libfontconfig1; do
    if dpkg -s "$lib" >/dev/null 2>&1; then
        echo "  OK  $lib"
    else
        echo "  MISSING  $lib"
        fail=1
    fi
done

# --- Build the app ---
if [ "$VERIFY_ONLY" = 0 ]; then
    echo
    echo "==> Building cs-pw-helper (Wayland screen capture)"
    # Must run before the dotnet build: the app's csproj copies
    # tools/linux-x64/** into its output, and CaptureFactory looks
    # for the helper next to the bundled ffmpeg. Without it the app
    # builds fine and then throws "cs-pw-helper is not bundled with
    # this build" the first time someone records on Wayland.
    if ! bash installer/linux/build-helper.sh; then
        echo "ERROR: cs-pw-helper failed to build." >&2
        echo "  Install the dev headers and retry:" >&2
        echo "    sudo apt install -y build-essential pkg-config \\" >&2
        echo "        libpipewire-0.3-dev libportal-dev \\" >&2
        echo "        libportal-gtk3-dev libgtk-3-dev" >&2
        echo "  Everything except Wayland screen capture still works." >&2
    fi

    echo
    echo "==> Building CaptureSpike.App for net10.0"
    dotnet build src/CaptureSpike.App -c Debug -f net10.0
fi

echo
if [ "$fail" = 0 ]; then
    echo "All checks passed. Ready to launch:"
    echo "    dotnet src/CaptureSpike.App/bin/Debug/net10.0/CaptureSpike.App.dll"
    if [ "$LAUNCH_AFTER" = 1 ]; then
        echo
        echo "==> Launching"
        exec dotnet src/CaptureSpike.App/bin/Debug/net10.0/CaptureSpike.App.dll
    fi
    exit 0
else
    echo "Some checks failed (see MISSING entries above)."
    echo "Re-run without --verify to install the missing pieces."
    exit 1
fi