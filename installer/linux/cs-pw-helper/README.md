# cs-pw-helper

The Wayland screen-capture helper for CaptureSpike.

## What it does

On a Wayland session, screen capture is mediated by
`xdg-desktop-portal`. The portal shows the user a system
dialog asking what to share (full screen, a specific
window, or a region), then hands the app a PipeWire node
ID. CaptureSpike's main app (a .NET 10 process) could
talk D-Bus to the portal directly, but the portal +
PipeWire code is non-trivial in C# and we'd be
reimplementing a lot of GIO and libspa boilerplate.

This helper is a small C program that does the
portal + PipeWire work and writes raw BGRA frames to
a Unix socket. The main app launches `cs-pw-helper`
as a child process and feeds ffmpeg the socket as a
rawvideo input.

```
   ffmpeg -f rawvideo -pix_fmt bgra -video_size WxH \
          -framerate N -i /path/to/socket
```

## Wire protocol

The helper writes a fixed-size header at the start of
the stream, then per-frame records:

```
Stream header (32 bytes, written once at start):
  bytes  0- 3  magic "CSWH"
  bytes  4- 7  version (LE u32; currently 1)
  bytes  8-11  width  (LE u32)
  bytes 12-15  height (LE u32)
  bytes 16-19  format (LE u32; 1 = BGRA, 2 = BGR0)
  bytes 20-23  framerate numerator   (LE u32)
  bytes 24-27  framerate denominator (LE u32)
  bytes 28-31  reserved (0)

Per-frame record (8-byte header + pixel data):
  bytes  0- 3  stride (LE u32) — == width*4 for BGRA
  bytes  4- 7  flags  (LE u32) — 0 = data frame, 1 = end of stream
  bytes  8- N  pixel data (stride * height bytes)
```

The C# side reads this as the rawvideo demuxer's
output. We add a thin wrapper that re-frames the
helper's output as rawvideo, since ffmpeg's rawvideo
demuxer expects a single header at the start of a
file (which a streaming socket is not). The simplest
implementation is for the C# side to just do the
header parsing itself; we don't go through ffmpeg's
rawvideo demuxer.

## Building

System packages (Ubuntu 24.04 / Debian 13 / Mint 22+):

```bash
sudo apt install build-essential libpipewire-0.3-dev \
                     libportal-dev libportal-gtk3-dev \
                     libgtk-3-dev pkg-config
```

Build:

```bash
make
```

The Makefile uses `pkg-config` to find all the right
include paths and library flags. If a package is
missing the build will warn you.

The result is a single static-ish binary at
`./cs-pw-helper` (~50 KB stripped, dynamically linked
against the system libpipewire + libportal + GTK).

## Status

**Skeleton only.** The file structure, the Unix
socket protocol, the PipeWire stream callbacks, and
the Makefile are all there. The portal D-Bus flow
is sketched but not actually wired up — the
`xdp_portal_create_screencast_session` call is a
synchronous placeholder, and the real API is async.

To finish the helper, someone needs to:

1. Wire up the async portal flow:
   - `xdp_portal_create_screencast_session_full` with
     a callback
   - On success, call `xdp_portal_session_select_sources`
     with the user's choice
   - Call `xdp_portal_session_start` to get the
     PipeWire node ID and file descriptor
   - Pass the FD to the PipeWire stream
2. Convert the file descriptor to a PipeWire stream
   (PW_KEY_NODE_ID, PW_KEY_FD, PW_KEY_STREAM_TYPE)
3. Test on a real Wayland session (KDE Plasma on
   Linux Mint, GNOME on Fedora, etc.)

None of this is testable on a headless X11 box.
Daniel will test the actual capture when he has a
Wayland session set up.

## Files

- `cs-pw-helper.c` — the C source, ~350 lines
- `Makefile` — build system, uses pkg-config

## Why a separate binary?

- libportal's API is GObject-based; awkward to use
  from C# but idiomatic in C with glib
- libpipewire's stream API is verbose but well-tested
  in C; the C# bindings are less mature
- Isolates the dependency on GTK3 (libportal-gtk3
  needs it for the parent window handle) to one
  small binary, not the whole app
- Avoids the GPL/LGPL issue with shipping a static
  ffmpeg with PipeWire
