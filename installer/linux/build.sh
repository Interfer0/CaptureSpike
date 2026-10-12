#!/bin/bash
# Build the CaptureSpike Linux packages (AppImage + .deb).
#
#   bash installer/linux/build.sh
#
# Steps:
#   1. Fetch appimagetool (cached in tools/appimagetool/).
#   2. Reuse bundled ffmpeg + mpv (cached in tools/linux-x64/).
#   3. dotnet publish self-contained to dist/publish/linux-x64/
#      with bundled tools in tools/linux-x64/ (so ToolPaths
#      finds them next to the .exe).
#   4. Stage the AppImage structure in dist/AppDir/.
#   5. Run appimagetool to produce dist/CaptureSpike-x86_64.AppImage.
#   6. Stage the .deb structure in dist/deb/.
#   7. Run dpkg-deb to produce
#      dist/CaptureSpike-Setup-<version>.deb.
#
# Both artifacts are SHA256-hashed next to themselves.
# Everything lands under dist/ which is gitignored.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

. scripts/fetch-tools.sh --no-banner >/dev/null 2>&1 || {
    echo "==> Re-running fetch-tools.sh to ensure ffmpeg + mpv are present"
    bash scripts/fetch-tools.sh
}

echo "==> Step 1/7: appimagetool"
TOOLS_DIR="$REPO_ROOT/tools"
APPIMAGETOOL_DIR="$TOOLS_DIR/appimagetool"
# Check the file is actually a binary, not a 404 HTML
# page that happens to be marked executable. A valid
# appimagetool is a ~14MB ELF; the broken 404 is
# typically 9 bytes of "Not Found".
if [ ! -x "$APPIMAGETOOL_DIR/appimagetool" ] || \
   [ $(stat -c%s "$APPIMAGETOOL_DIR/appimagetool" 2>/dev/null || echo 0) -lt 1000000 ]; then
    rm -f "$APPIMAGETOOL_DIR/appimagetool"
    mkdir -p "$APPIMAGETOOL_DIR"
    # appimagetool is a single binary, ~14 MB, no install
    # needed. We pin to the AppImageKit release that
    # matches the runtime we use (x86_64).
    APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    echo "Downloading appimagetool..."
    if ! curl -L -f -o "$APPIMAGETOOL_DIR/appimagetool" "$APPIMAGETOOL_URL"; then
        echo "ERROR: failed to download appimagetool from $APPIMAGETOOL_URL" >&2
        exit 1
    fi
    chmod +x "$APPIMAGETOOL_DIR/appimagetool"
fi
APPIMAGETOOL="$APPIMAGETOOL_DIR/appimagetool"

# --- Step 2: ffmpeg + mpv ---
LINUX_TOOLS="$TOOLS_DIR/linux-x64"
# Bundled ffmpeg is required (the app uses the bundled
# copy via ToolPaths).
if [ ! -x "$LINUX_TOOLS/ffmpeg" ]; then
    echo "==> Step 2/7: ffmpeg (re-fetch)"
    bash scripts/fetch-tools.sh
fi
# mpv can be either bundled (--build-mpv, slow) or
# system-installed (apt install mpv, fast). The build
# works either way; the .deb / AppImage will run mpv
# from PATH on the user's system. We don't bundle
# mpv by default because there's no trustworthy
# static mpv build for Linux with X11 video output.
if [ ! -x "$LINUX_TOOLS/mpv" ] && ! command -v mpv >/dev/null 2>&1; then
    echo "WARNING: mpv is not bundled and not on PATH."
    echo "         The build will continue, but the .deb / AppImage"
    echo "         will require mpv to be installed on the user's"
    echo "         system (apt install mpv) at runtime."
fi

# --- Step 3: dotnet publish ---
echo "==> Step 3/7: dotnet publish self-contained"
PUBLISH_DIR="$REPO_ROOT/dist/publish/linux-x64"
rm -rf "$PUBLISH_DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet publish \
    src/CaptureSpike.App \
    -c Release \
    -f net10.0 \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishReadyToRun=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$PUBLISH_DIR"

# Copy bundled ffmpeg + mpv into the publish output.
# ToolPaths (src/CaptureSpike.Core/Capture/ToolPaths.cs)
# looks at <exe-dir>/tools/linux-x64/, so they MUST
# end up there. The csproj's <Content Include=...> copy
# happens for the Windows TFM only; on Linux the user
# runs the .dll directly, so we copy here.
BUNDLED_TOOLS="$PUBLISH_DIR/tools/linux-x64"
mkdir -p "$BUNDLED_TOOLS"
cp -r "$LINUX_TOOLS"/* "$BUNDLED_TOOLS/" 2>/dev/null || true

# --- Step 4: Stage AppImage ---
echo "==> Step 4/7: Stage AppDir for AppImage"
APPDIR="$REPO_ROOT/dist/AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR"

# Mirror the publish output directly into the AppDir
# root. The .NET apphost looks for its sibling .dll
# AND for the .NET runtime (libhostfxr.so,
# libcoreclr.so, etc.) in the same directory or via
# DOTNET_ROOT. Splitting them across usr/bin/ and
# usr/lib/ complicates this; the simplest correct
# layout is to keep everything together.
#
# Icons go into usr/share/icons/hicolor/<size>x<size>/apps/
# (the freedesktop hicolor spec) so file managers
# can find them after install. The .desktop goes
# into usr/share/applications/ (system integration).
# Both are produced by the .csproj EmitLinuxIcons
# target into the publish output.
cp -r "$PUBLISH_DIR"/* "$APPDIR/" 2>/dev/null || true

# Mark the executable.
chmod +x "$APPDIR/CaptureSpike.App"
# AppStream metadata (optional but recommended for
# AppImageHub submission and for file managers that
# read it). appimagetool validates this with
# appstreamcli; the build fails if there are
# errors. The required fields are:
#   <id>               - reverse-DNS app id
#   <name>             - display name
#   <summary>          - one-line description
#   <metadata_license> - license of the metadata file itself
#   <project_license>  - license of the application
#   <description><p>   - longer description
#   <launchable>       - desktop-id (matches the .desktop file)
#   <content_rating>   - rating (oars-1.0 is the standard)
#   <developer><name>  - who made it
# Without these, appimagetool's validator emits
# errors and aborts. A more complete appdata.xml
# is on the roadmap (screenshots, release notes,
# translations).
mkdir -p "$APPDIR/usr/share/metainfo"
cat > "$APPDIR/usr/share/metainfo/io.github.Interfer0.CaptureSpike.appdata.xml" <<'METAINFO_EOF'
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>io.github.Interfer0.CaptureSpike</id>
  <metadata_license>CC0-1.0</metadata_license>
  <project_license>GPL-3.0-or-later</project_license>
  <name>CaptureSpike</name>
  <summary>Multi-source screen and audio recorder</summary>
  <description>
    <p>CaptureSpike records multiple video and audio sources
    in sync (screen, microphone, system audio). Per-take
    editor re-aligns audio by prepending silence.</p>
  </description>
  <launchable type="desktop-id">capturespike.desktop</launchable>
  <url type="homepage">https://github.com/Interfer0/CaptureSpike</url>
  <content_rating type="oars-1.0" />
  <developer id="https://github.com/Interfer0">
    <name>Daniel Bigelow</name>
  </developer>
  <categories>
    <category>AudioVideo</category>
    <category>Audio</category>
    <category>Video</category>
    <category>Recorder</category>
  </categories>
</component>
METAINFO_EOF

# Desktop file for the AppImage. The Exec= points at
# the AppRun script, which sets up the runtime and
# invokes the .NET binary. Different from the
# capturespike.desktop in src/.../Assets/ which is
# the dev-mode launcher (dotnet CaptureSpike.App.dll).
cat > "$APPDIR/capturespike.desktop" <<'APPIMAGE_DESKTOP_EOF'
[Desktop Entry]
Type=Application
Name=CaptureSpike
GenericName=Multi-source recorder
Comment=Record screen and audio from multiple sources in sync
Exec=AppRun %u
Icon=capturespike
Terminal=false
Categories=AudioVideo;Audio;Video;Recorder;
Keywords=screen;recording;audio;video;capture;
StartupNotify=true
StartupWMClass=CaptureSpike
APPIMAGE_DESKTOP_EOF

cat > "$APPDIR/usr/share/applications/capturespike.desktop" <<'APPIMAGE_DESKTOP_EOF'
[Desktop Entry]
Type=Application
Name=CaptureSpike
GenericName=Multi-source recorder
Comment=Record screen and audio from multiple sources in sync
Exec=AppRun %u
Icon=capturespike
Terminal=false
Categories=AudioVideo;Audio;Video;Recorder;
Keywords=screen;recording;audio;video;capture;
StartupNotify=true
StartupWMClass=CaptureSpike
APPIMAGE_DESKTOP_EOF

# appimagetool requires the icon at the AppDir root
# (with the name from the .desktop file's Icon= key)
# and a .DirIcon symlink. The 256x256 PNG from the
# hicolor set is the right size for this. The icons
# are inside usr/share/icons/hicolor/ (mirrored from
# the publish output), so extract from there.
ICON_SRC="$APPDIR/usr/share/icons/hicolor/256x256/apps/capturespike.png"
if [ -f "$ICON_SRC" ]; then
    cp "$ICON_SRC" "$APPDIR/capturespike.png"
    ln -s capturespike.png "$APPDIR/.DirIcon"
fi

# AppRun: the entry point that the AppImage runtime
# invokes. The .NET apphost needs to find its sibling
# .dll and the .NET runtime (libhostfxr.so, etc.).
# We keep everything in the AppDir root (mirrored
# from the publish output), so the apphost can
# resolve everything by relative path.
cat > "$APPDIR/AppRun" <<'APPRUN_EOF'
#!/bin/bash
# AppRun for CaptureSpike.
# The AppImage runtime sets $APPDIR to the mount
# point. The .NET apphost needs DOTNET_ROOT to find
# the .NET runtime libraries (libhostfxr.so etc.);
# pointing it at the AppDir root lets it find them
# alongside CaptureSpike.App.
HERE="$(dirname "$(readlink -f "$0")")"
export DOTNET_ROOT="$HERE"
exec "$HERE/CaptureSpike.App" "$@"
APPRUN_EOF
chmod +x "$APPDIR/AppRun"

# --- Step 5: Build AppImage ---
echo "==> Step 5/7: appimagetool"
APPIMAGE_OUT="$REPO_ROOT/dist/CaptureSpike-x86_64.AppImage"
# appimagetool needs --appimage-extract-and-run on hosts
# without FUSE. CI runners don't have FUSE, so we set
# this. Local builds usually don't need it.
ARCH=x86_64 "$APPIMAGETOOL" \
    --appimage-extract-and-run \
    "$APPDIR" \
    "$APPIMAGE_OUT" \
    2>&1 | tail -5

if [ ! -f "$APPIMAGE_OUT" ]; then
    echo "ERROR: AppImage was not produced" >&2
    exit 1
fi

# --- Step 6: Stage .deb ---
echo "==> Step 6/7: Stage .deb"
DEB_ROOT="$REPO_ROOT/dist/deb"
rm -rf "$DEB_ROOT"
mkdir -p "$DEB_ROOT/DEBIAN"
mkdir -p "$DEB_ROOT/opt/capturespike"
mkdir -p "$DEB_ROOT/usr/share/applications"
mkdir -p "$DEB_ROOT/usr/share/icons/hicolor"
mkdir -p "$DEB_ROOT/usr/share/doc/capturespike"
mkdir -p "$DEB_ROOT/usr/share/licenses/capturespike"
mkdir -p "$DEB_ROOT/usr/share/metainfo"

# Copy the same publish output, but to /opt/capturespike/
# (the FHS-standard location for self-contained third-party
# apps). On Debian, /opt/<app>/ is the equivalent of
# "Program Files" on Windows.
cp -r "$PUBLISH_DIR"/* "$DEB_ROOT/opt/capturespike/"
chmod +x "$DEB_ROOT/opt/capturespike/CaptureSpike.App"
if [ -d "$PUBLISH_DIR/usr/share/icons/hicolor" ]; then
    cp -r "$PUBLISH_DIR/usr/share/icons/hicolor/." "$DEB_ROOT/usr/share/icons/hicolor/"
fi
# Desktop file for the .deb. Exec= points at the
# .deb's install path (/opt/capturespike/), not at
# the dev-mode `dotnet CaptureSpike.App.dll` that
# the source repo's capturespike.desktop uses.
cat > "$DEB_ROOT/usr/share/applications/capturespike.desktop" <<'DEB_DESKTOP_EOF'
[Desktop Entry]
Type=Application
Name=CaptureSpike
GenericName=Multi-source recorder
Comment=Record screen and audio from multiple sources in sync
Exec=/opt/capturespike/CaptureSpike.App %u
Icon=capturespike
Terminal=false
Categories=AudioVideo;Audio;Video;Recorder;
Keywords=screen;recording;audio;video;capture;
StartupNotify=true
StartupWMClass=CaptureSpike
DEB_DESKTOP_EOF
# AppStream metadata for app stores (e.g. elementary
# AppCenter reads /usr/share/metainfo/*.appdata.xml).
cp "$REPO_ROOT/dist/AppDir/usr/share/metainfo/io.github.Interfer0.CaptureSpike.appdata.xml" \
   "$DEB_ROOT/usr/share/metainfo/io.github.Interfer0.CaptureSpike.appdata.xml" 2>/dev/null || true

# Get version from the .csproj.
APP_VERSION=$(grep -oP '<Version>\K[^<]+' src/CaptureSpike.App/CaptureSpike.App.csproj || echo "0.0.0-dev")

# DEBIAN/control: package metadata. The `Package` name
# must be all lowercase for apt.
cat > "$DEB_ROOT/DEBIAN/control" <<CONTROL_EOF
Package: capturespike
Version: $APP_VERSION
Architecture: amd64
Maintainer: Daniel Bigelow <Interfer0@gmail.com>
Section: video
Priority: optional
Depends: libx11-6, libxext6, libpulse0, libegl1, libgbm1, mpv
Description: Multi-source screen and audio recorder
 CaptureSpike records multiple video and audio sources in
 sync (screen, microphone, system audio). Per-take editor
 re-aligns audio by prepending silence.
 .
 Licensed under GPL-3.0-or-later. See /usr/share/licenses/capturespike/LICENSE
 for the full license text.
CONTROL_EOF

# DEBIAN/postinst: runs after install. Sets the binary
# as executable and refreshes the desktop file cache.
cat > "$DEB_ROOT/DEBIAN/postinst" <<'POSTINST_EOF'
#!/bin/bash
set -e
chmod +x /opt/capturespike/CaptureSpike.App
chmod +x /opt/capturespike/tools/linux-x64/ffmpeg
chmod +x /opt/capturespike/tools/linux-x64/mpv
if [ -x /usr/bin/update-desktop-database ]; then
    update-desktop-database /usr/share/applications
fi
if [ -x /usr/bin/gtk-update-icon-cache ]; then
    gtk-update-icon-cache -f -t /usr/share/icons/hicolor
fi
exit 0
POSTINST_EOF
chmod 755 "$DEB_ROOT/DEBIAN/postinst"

# DEBIAN/prerm: runs before uninstall. Kills any
# running instance of the app.
cat > "$DEB_ROOT/DEBIAN/prerm" <<'PRERM_EOF'
#!/bin/bash
set -e
# Stop any running instance so the .deb can overwrite files.
pkill -f "CaptureSpike.App" 2>/dev/null || true
exit 0
PRERM_EOF
chmod 755 "$DEB_ROOT/DEBIAN/prerm"

# DEBIAN/conffiles: empty (no config files to preserve).
touch "$DEB_ROOT/DEBIAN/conffiles"

# Conffiles is required to be present even if empty.
# The .deb installer doesn't preserve our /opt folder
# on upgrade because there are no conffiles marked
# for preservation.

# Install LICENSE + NOTICE alongside the binary so
# the user can read them later (matches the Windows
# installer's behavior).
cp "$REPO_ROOT/LICENSE" "$DEB_ROOT/usr/share/doc/capturespike/copyright"
cp "$REPO_ROOT/NOTICE" "$DEB_ROOT/usr/share/doc/capturespike/NOTICE"
cp "$REPO_ROOT/LICENSE" "$DEB_ROOT/usr/share/licenses/capturespike/LICENSE"
cp "$REPO_ROOT/tools/THIRD-PARTY-LICENSES.md" \
   "$DEB_ROOT/usr/share/doc/capturespike/THIRD-PARTY-LICENSES.md"

# --- Step 7: Build .deb ---
echo "==> Step 7/7: dpkg-deb"
DEB_OUT="$REPO_ROOT/dist/CaptureSpike-Setup-$APP_VERSION.deb"
dpkg-deb --build --root-owner-group "$DEB_ROOT" "$DEB_OUT" 2>&1 | tail -5

if [ ! -f "$DEB_OUT" ]; then
    echo "ERROR: .deb was not produced" >&2
    exit 1
fi

# SHA256 for both artifacts.
sha256sum "$APPIMAGE_OUT" | awk '{print $1, "  CaptureSpike-x86_64.AppImage"}' > "$APPIMAGE_OUT.sha256"
sha256sum "$DEB_OUT" | awk -v name="$(basename "$DEB_OUT")" '{print $1, "  " name}' > "$DEB_OUT.sha256"

echo ""
echo "==> Done."
echo "    AppImage: $APPIMAGE_OUT ($(du -h "$APPIMAGE_OUT" | awk '{print $1}'))"
echo "    .deb:     $DEB_OUT ($(du -h "$DEB_OUT" | awk '{print $1}'))"
echo "    Hash:     $(cat "$APPIMAGE_OUT.sha256")"
echo "             $(cat "$DEB_OUT.sha256")"
