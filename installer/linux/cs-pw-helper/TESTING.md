# Tomorrow's runbook for the cs-pw-helper screencast test

## Laptop state (as of 2026-10-13 05:42 UTC)

- Logged in as `interfer0` on **tty2** (NOT a graphical session).
- `XDG_SESSION_TYPE=tty`, `DISPLAY=`, `WAYLAND_DISPLAY=` — all empty.
- GDM is the login screen (no one is logged in graphically).
- Dev libraries installed: `libpipewire-0.3-dev libportal-dev libportal-gtk3-dev`.
- SSH: `ssh danslappy` (passwordless sudo via `/etc/sudoers.d/hermes-agent`).
- Helper binary at `~/CaptureSpike/installer/linux/cs-pw-helper/cs-pw-helper` is the latest build (commits pushed to `origin/wayland`).

## When Daniel logs in

1. From GDM, log in as `interfer0` with the passwordless sudo setup.
2. **Pick a session type**:
   - **GNOME on Wayland** — best for screencast (screencast portal is the
     most common case; works with current code).
   - **GNOME on X11** — x11grab via ffmpeg works, but our cs-pw-helper
     was designed for the portal path. Should still work because the
     portal supports x11 sessions.
3. Verify session:
   ```
   echo "XDG_SESSION_TYPE=$XDG_SESSION_TYPE"
   echo "XDG_CURRENT_DESKTOP=$XDG_CURRENT_DESKTOP"
   echo "DISPLAY=$DISPLAY"  # should be :0 or :1
   ```
4. Expected: `XDG_SESSION_TYPE=wayland` or `x11`, `XDG_CURRENT_DESKTOP=GNOME`,
   `DISPLAY=:0` or `:1`.

## Run the screencast test

Once Daniel is in a graphical session, do the actual screencast test:

```bash
# ssh into the laptop
ssh danslappy

# Verify portal is running
ps -ef | grep -E "xdg-desktop-portal|gnome-remote-desktop" | grep -v grep

# Run the helper (portal mode, no --node-id)
cd ~/CaptureSpike/installer/linux/cs-pw-helper
rm -f /tmp/cs-pw-test.sock /tmp/pw-test.log
DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus \
  ./cs-pw-helper --socket /tmp/cs-pw-test.sock --framerate 30 \
  > /tmp/pw-test.log 2>&1 &

# (Daniel clicks Share in the portal dialog)

# Watch the log
tail -f /tmp/pw-test.log

# Expect to see:
#   "requesting screen capture..."
#   "session created; starting (waiting for user)..."
#   "session started"
#   "pipewire remote fd = N"
#   "PipeWire main loop running on a dedicated thread"
#   "query EnumFormat via update_params returned 0"
#   "on_stream_param_changed id=4"  (Format event!)
#   "CSWH W=... H=... FPS=..."       (header to stderr)
#   "ffmpeg connected to socket"     (after C# side launches ffmpeg)
#   "stream state X -> 3"            (STREAMING)
#   "on_stream_process called"        (frames flowing)
```

## What the C# side does

The WaylandCapturer (in src/CaptureSpike.Core/Capture/Wayland/) spawns
the helper, reads CSWH from stderr, then spawns ffmpeg reading from the
socket. We can test the C# side either via:

a) Running the full CaptureSpike app and clicking Record on a Screen source
b) Building a small CLI test driver that exercises WaylandCapturer directly

Option (a) is more realistic. Option (b) is more focused.

## If the Format event doesn't fire (passive-source case)

The current code has a first-frame size derivation fallback. If the
source never sends a Format event but on_process fires with frames,
we derive W/H from `chunk->stride` and `chunk->size`, then emit CSWH.
This is the OBS-style "infer from first frame" approach.

## Known issues / things to watch

1. **`xdp_portal_create_screencast_session_finish failed: CreateSession failed`**
   on tty sessions. Login at GDM first.
2. **Camera (`--node-id 55`) test deadlocks at PAUSED** because V4L2
   sources don't push Format events. Not a screencast issue.
3. **Wrong-context warnings** on `pw_stream_new` etc. The setup is on
   the PW thread, but PipeWire's debug logger sometimes flags it. Not
   a correctness issue.

## Diagnostic file locations on the laptop

- Helper source: `~/CaptureSpike/installer/linux/cs-pw-helper/cs-pw-helper.c`
- Helper binary: `~/CaptureSpike/installer/linux/cs-pw-helper/cs-pw-helper`
- Test fake: `~/CaptureSpike/installer/linux/cs-pw-helper/test/fake_pw_capture.py`
- Helper stderr (live): `/tmp/pw-test.log` (when running test)
- PipeWire session log: `journalctl --user -u pipewire -f` (live)

## Open question for tomorrow

After the screencast path works, the remaining work is:

- [ ] Test with portal on actual Wayland session
- [ ] Test with portal on actual X11 session
- [ ] Verify ffmpeg reads the CSWH-configured stream correctly
- [ ] Test the full CaptureSpike app: add a screen source, click Record,
      see an .mp4 file produced
- [ ] Optional: write a CLI test driver for the C# WaylandCapturer
