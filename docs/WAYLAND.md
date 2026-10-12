# Wayland screen capture

## Status: design phase, not yet implemented

This document describes the design for adding Wayland
screen capture to CaptureSpike. Nothing here is
implemented yet. The goal is to scope the work, identify
the right libraries, and document the approach so the
implementation has a clear target.

## Problem

CaptureSpike currently only supports X11 screen capture on
Linux. The `CreateLinux` path in
`src/CaptureSpike.Core/Capture/CaptureFactory.cs` hard-codes
`ffmpeg -f x11grab`, which:

- Only works when `DISPLAY` is set (X11 session)
- Fails with "Cannot open display" on Wayland sessions
- The README explicitly states "Wayland capture is not
  supported" and recommends using an X11 session

This blocks CaptureSpike on the major Linux desktop
environments that are moving to Wayland by default:
GNOME 46+, KDE Plasma 5.27+, Fedora Workstation,
Ubuntu 24.04 LTS, etc.

## Why Wayland is different

X11 allows any client to read any other client's
window contents. That's how `x11grab` works — the X
server just hands you the pixels.

Wayland removed that capability for security reasons.
A Wayland compositor never lets a client read another
client's windows directly. Instead, the user must
explicitly grant permission via a system dialog, mediated
by `xdg-desktop-portal`.

## The portal approach

The standard Wayland screen-capture path is:

```
+----------------+        D-Bus         +-----------------------+
| CaptureSpike   |  ──────────────►    | xdg-desktop-portal    |
| (our app)      |  SelectSources()    | (system service)      |
|                |  Start()            |                       |
|                |                     | talks to compositor   |
+----------------+                     | (GNOME/KDE/Sway/etc.) |
        │                              +-----------------------+
        │                                        │
        │                                        │ user clicks
        │                                        │ "Share" in dialog
        │                                        ▼
        │                              +-----------------------+
        │                              | compositor             |
        │                              | (GNOME Mutter /        |
        │                              |  KDE KWin / wlroots)   |
        │                              +-----------------------+
        │                                        │
        │                                        │ opens PipeWire
        │                                        │ stream of the
        │                                        │ selected source
        │                                        ▼
        │                              +-----------------------+
        │                              | PipeWire daemon        |
        │                              | (pw.service)           |
        │                              +-----------------------+
        │                                        │
        │ FD + node id (passed back via D-Bus)    │
        │ ◄──────────────────────────────────────┘
        │
        │ ffmpeg reads from the PipeWire FD/node
        ▼
+----------------+
| ffmpeg          |
| -f pipewiresrc  |
| -i fd=<N>       |
|   path=<id>     |
+----------------+
```

The user's interaction is one dialog box (per session,
typically). The permission grant is for the lifetime of
the PipeWire stream. The user picks what to share
(full screen, a specific window, or a region) and the
compositor decides what gets sent over the PipeWire
stream.

## Libraries and packages

### System packages (runtime)

- **`libpipewire-0.3-0`** — PipeWire client library.
  ffmpeg's `pipewiresrc` is linked against this. We
  don't call it directly; ffmpeg does.
- **`libportal1`** (optional, for a simpler C# API) —
  the GTK/libportal library wraps the D-Bus calls to
  `xdg-desktop-portal` into a clean C API. Optional
  because we can talk D-Bus directly.
- **`xdg-desktop-portal`** (the portal daemon itself) —
  usually pre-installed on Wayland systems. We need
  at least one portal backend (`xdg-desktop-portal-gtk`
  for GNOME, `xdg-desktop-portal-kde` for KDE, etc.)
- **A PipeWire session manager** — `wireplumber` is
  the standard one; usually pre-installed.

### .NET packages

- **`Tmds.DBus`** — D-Bus bindings for .NET. Used to
  call the portal. The portal's D-Bus interface is
  the standard `org.freedesktop.portal.*` family.
  No other D-Bus library is needed; this one is
  actively maintained and works on Linux.
- (No PipeWire C# binding needed — ffmpeg handles it.)

### ffmpeg

The bundled ffmpeg must be built with `--enable-libpipewire`.
The current bundled build is from gyan.dev, which I
believe already includes it. **Verify on first run:**
`ffmpeg -version 2>&1 | grep -i pipewire` should show
`--enable-libpipewire`. If not, switch the ffmpeg fetch
to a build that has it.

## Code changes

### 1. Session detection (small, do first)

Add a helper that returns the current display server:

```csharp
// src/CaptureSpike.Core/Capture/SessionInfo.cs
public static class SessionInfo
{
    public enum DisplayServer { X11, Wayland, Unknown }

    public static DisplayServer Current =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? DisplayServer.Wayland
            : Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 }
                ? DisplayServer.X11
                : DisplayServer.Unknown;
}
```

Use it in `CreateLinux` to branch on capture backend.

### 2. Portal interaction (medium, the hard part)

New file: `src/CaptureSpike.Core/Capture/Wayland/PortalScreenCast.cs`

Wraps the two D-Bus calls to the portal:

```csharp
// Pseudo-code, not final
public sealed class PortalScreenCast
{
    // Step 1: ask the user to pick what to share.
    // Returns a "session handle" the user later
    // confirms (or cancels) in a system dialog.
    public Task<string> SelectSourcesAsync(CancellationToken ct);

    // Step 2: actually start the capture. Returns
    // a PipeWire node ID + file descriptor for
    // ffmpeg to consume.
    public Task<(uint NodeId, int Fd)> StartAsync(
        string sessionHandle, CancellationToken ct);
}
```

The D-Bus calls are to:
- `org.freedesktop.portal.ScreenCast.SelectSources(parent_window, options)`
- `org.freedesktop.portal.ScreenCast.Start(parent_window, session_handle, options)`

`parent_window` is the X11/Wayland window ID of our
app, which the portal uses to position the dialog
modally. On Wayland this comes from
`wl_surface`; on X11 from the X11 window. Avalonia
can give us the XID of the toplevel window.

### 3. ffmpeg integration (small)

`CreateLinux` for the Screen source kind on Wayland
emits different ffmpeg args:

```
-f pipewiresrc -framerate <fps> -i fd=<fd>:path=<node-id>
```

instead of:

```
-f x11grab -framerate <fps> -i <DISPLAY>
```

The ffmpeg process holds the FD open; when the
recording stops, we close the PipeWire stream
(close the FD, send a D-Bus call to the portal
to end the session).

### 4. Window capture (later)

X11's `x11grab` can target a specific X window. Wayland's
portal has the same capability (the user picks a window
in the "Share" dialog), but the user's choice is
communicated to ffmpeg via the stream's metadata, not
as a separate parameter. The capture will already work
in v0.5; we just need to map the user's "Window" source
kind to the portal's "window" share type.

### 5. UI changes (small)

The error message when starting a recording on a
Wayland system should mention:
- "A permission dialog will appear; click Share to allow
  screen capture."
- "If no dialog appears, ensure `xdg-desktop-portal` and
  a portal backend (GNOME/KDE/Sway) are installed."

A small status indicator could show whether the portal
stream is healthy (the FD hasn't been closed, the
portal session is still active).

## Phases

Each phase is shippable as a working build, but the
Wayland support is incomplete until Phase 4.

### Phase 1: Detect and report (1-2 days)

- Add `SessionInfo.Current` helper
- On Wayland, when the user tries to record the screen,
  show a clear error: "Wayland screen capture is not yet
  supported. Please log into an X11 session."
- This is honest and stops the confusing "Cannot open
  display" ffmpeg error.
- **Acceptance:** X11 still works, Wayland shows the
  clear error message, no crashes.

### Phase 2: Portal integration (3-5 days)

- Add `Tmds.DBus` dependency
- Implement `PortalScreenCast.SelectSourcesAsync` and
  `StartAsync`
- Wire it into `CreateLinux` for the Screen source on
  Wayland
- Update the .deb to depend on `xdg-desktop-portal` (the
  portal daemon is a runtime dep now)
- **Acceptance:** on Wayland, the portal dialog appears
  when the user records, ffmpeg consumes the PipeWire
  stream, the recorded .mp4 is correct

### Phase 3: Per-take source lifecycle (2-3 days)

- The portal session is created when recording starts
  and torn down when it ends
- Multiple takes in a row don't re-prompt (or do,
  depending on portal backend behavior)
- The portal session is per-app, not per-window, so
  the app holds a single session handle for its
  lifetime
- **Acceptance:** recording multiple takes works, the
  portal only prompts once, no leaked PipeWire streams

### Phase 4: Window + region capture, multi-monitor (3-5 days)

- Map the Window source kind to the portal's "window"
  share type
- Map a region (`X,Y,WxH` in source.Device) to the
  portal's "region" choice (or a crop filter in ffmpeg)
- Multi-monitor: the portal lets the user pick which
  monitor; pass that through
- **Acceptance:** Window, region, and multi-monitor
  captures work on Wayland

## Testing strategy

Without a Wayland dev box, we can't test the actual
portal flow. Tests are limited to:

- **Unit tests:** the D-Bus marshaling is hard to
  unit-test without a real portal. The `SessionInfo`
  helper can be tested with environment variable
  manipulation.
- **Compile-only check:** the code compiles on Linux
  with the right dependencies.
- **Manual test on a Wayland box:** the only real
  validation. KDE Plasma or GNOME on Fedora/Ubuntu is
  the target. Run the test plan in Phase 2.

Test environment setup (for whoever does the manual
test):
- Install KDE Plasma on the existing Mint (the
  recommended approach for this dev box) or use Fedora
  Workstation in a VM with virtio-gpu (so the portal
  has a real display to capture).
- Verify `echo $XDG_SESSION_TYPE` returns `wayland`
- Verify `xdg-desktop-portal` is running:
  `systemctl --user status xdg-desktop-portal`
- Verify a portal backend is installed:
  `which xdg-desktop-portal-kde` (or `-gtk` for GNOME)

## Open questions

1. **ffmpeg + PipeWire is not bundled in any major
   prebuilt ffmpeg.** Verified 2026-10-12:
   - gyan.dev `ffmpeg-release-essentials` — no
     `--enable-libpipewire`
   - BtbN `ffmpeg-n9.0-latest-linux64-gpl` — no
     `--enable-libpipewire`
   - BtbN `ffmpeg-n9.0-latest-linux64-lgpl` — no
     `--enable-libpipewire` either (and no x264/x265,
     since those are GPL)

   **Why:** PipeWire's library is LGPL/MIT-licensed.
   The GPL-licensed ffmpeg can't statically link
   against non-GPL libraries. The prebuilt
   distributions don't ship a separate
   PipeWire-enabled build.

   **Implication for the design:** to use ffmpeg's
   built-in `pipewiresrc`, we need to build ffmpeg
   from source. That's a 5-10 minute build step
   that requires build-essential + many dev packages
   on Linux. The build artifact is large (~150 MB
   after stripping, vs the current 50 MB prebuilt).

   **Decision (for v1):** ship two ffmpeg builds:
   - `ffmpeg-x11`: gyan.dev (works for X11, the
     current default)
   - `ffmpeg-wayland`: built from source with
     `--enable-libpipewire`, downloaded or built
     on demand for Wayland systems
   The `fetch-tools.sh` script can build the
   Wayland variant on demand if the user requests
   it (or always, with the result being cached).

   **Alternative (simpler, v0.5):** a small C helper
   that handles the portal + PipeWire + frame
   extraction, exposes them via a Unix socket, and
   ffmpeg reads from the socket. This avoids the
   ffmpeg rebuild but means writing a non-trivial
   C program. The C helper still needs libpipewire
   + libportal at build time.

2. **Does gyan.dev's ffmpeg build include `--enable-libpipewire`?**
   See #1: confirmed NO for gyan.dev. **Resolved.**

3. **Parent window ID.** The portal's `SelectSources`
   takes a "parent window" so the dialog can be modal
   to our window. On Wayland this is a `wl_surface*`,
   which is not easy to get from .NET. On X11 it's the
   XID of the toplevel window. Avalonia has `TopLevel`
   APIs but no cross-platform way to get a "parent
   handle" that the portal accepts.
   **Possible workaround:** pass empty string for
   parent window; the portal centers the dialog on
   the screen. Not ideal but it works.

4. **The ffmpeg FD lifecycle.** We open an FD, pass it
   to ffmpeg, ffmpeg reads from it until we kill the
   process. If ffmpeg dies but the FD is still open,
   we leak it. Need a try/finally around the ffmpeg
   process.

5. **A single source or multiple?** The portal's
   `Start()` returns a single PipeWire node. The user
   can choose "Full screen" (one big stream) or
   "Window" (just that window). The "Region" choice
   is a ffmpeg crop, not a separate stream. So a
   single Screen source = one portal session = one
   PipeWire node. Multiple Screen sources in the same
   project would each need their own portal session
   and the user would have to grant each one — bad UX.
   **Current decision:** for v1, only one Screen source
   per project. The portal session is per-app, but each
   Screen source in the project re-uses the same
   stream. This matches what OBS does.

6. **Region capture.** Wayland's portal doesn't have a
   "region" option like X11's `-video_size 800x600
   -i +100,100`. The user picks full-screen, then
   we crop in ffmpeg with a `crop` filter:
   `-vf "crop=800:600:100:100"`. This is fine but
   wastes bandwidth on the parts of the screen we're
   cropping out. Not a big deal for typical use.

## References

- [xdg-desktop-portal ScreenCast spec](https://flatpak.github.io/xdg-desktop-portal/portal-docs.html#gdbus-org.freedesktop.portal.ScreenCast)
- [PipeWire documentation](https://docs.pipewire.org/)
- [Tmds.DBus .NET package](https://github.com/tmds/Tmds.DBus)
- [ffmpeg pipewiresrc documentation](https://ffmpeg.org/ffmpeg-devices.html#pipewiresrc)

## Target versions

- Wayland compositor: KDE Plasma 5.27+ (Plasma 6+)
  or GNOME 46+
- xdg-desktop-portal: 1.18+
- PipeWire: 0.3.50+
- ffmpeg: 6.0+ (with `--enable-libpipewire`)
- .NET: 10.0+ (already required)
