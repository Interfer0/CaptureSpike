#!/usr/bin/env python3
"""Fake cs-pw-helper: writes BGRA frames to a Unix socket.

Used for end-to-end testing of the ffmpeg / C# side without
needing a real PipeWire source. Wire protocol matches
cs-pw-helper README.md.
"""
import argparse
import os
import socket
import struct
import sys
import time

HDR_MAGIC_STREAM = b"CSWH"
HDR_MAGIC_FRAME  = b"CSWF"


def make_frame(width: int, height: int, bar_y: int) -> bytes:
    """Build a single BGRA frame as bytes.

    Pixels are laid out row-major: B, G, R, A per pixel.
    We use a horizontal gradient (B/G vary with x, R fixed)
    plus a 10px-tall rolling red bar at bar_y.
    """
    row = bytearray(width * 4)
    for x in range(width):
        b = (x * 255) // (width - 1) if width > 1 else 0
        g = (x * 200) // (width - 1) if width > 1 else 0
        r = 0
        a = 255
        row[x*4 + 0] = b
        row[x*4 + 1] = g
        row[x*4 + 2] = r
        row[x*4 + 3] = a
    frame = bytearray()
    for y in range(height):
        if bar_y <= y < bar_y + 10:
            # red bar
            frame.extend(b"\x00\x00\xff\xff" * width)
        else:
            frame.extend(row)
    return bytes(frame)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--socket", required=True)
    ap.add_argument("--width", type=int, default=1920)
    ap.add_argument("--height", type=int, default=1080)
    ap.add_argument("--framerate", type=float, default=30.0)
    ap.add_argument("--format", default="BGRA")
    ap.add_argument("--duration", type=float, default=5.0)
    args = ap.parse_args()

    if os.path.exists(args.socket):
        os.unlink(args.socket)
    ss = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    ss.bind(args.socket)
    ss.listen(1)
    print(f"fake-helper: listening on {args.socket}", flush=True)

    print("fake-helper: waiting for client...", flush=True)
    cs, _ = ss.accept()
    cs.settimeout(2.0)
    print("fake-helper: client connected", flush=True)

    fmt_code = {"BGRA": 0, "BGRx": 1, "RGBx": 2, "BGR": 3}[args.format]
    header = struct.pack("<4sIIIIIf",
        HDR_MAGIC_STREAM,
        1,
        args.width,
        args.height,
        fmt_code,
        int(args.framerate),
        0.0)
    cs.sendall(header)
    print(f"fake-helper: sent CSWH {args.width}x{args.height} {args.format}@{args.framerate}", flush=True)

    frame_size = args.width * args.height * 4
    period = 1.0 / args.framerate
    n_frames = int(args.duration * args.framerate)
    t0 = time.monotonic()
    seq = 0
    print(f"fake-helper: generating {n_frames} frames at {args.framerate} fps "
          f"({frame_size/1024/1024:.1f} MiB each)...", flush=True)
    for i in range(n_frames):
        bar_y = (i * 8) % args.height
        frame = make_frame(args.width, args.height, bar_y)
        ts = int((time.monotonic() - t0) * 1_000_000)
        hdr = struct.pack("<4sIQI", HDR_MAGIC_FRAME, seq, ts, frame_size)
        try:
            cs.sendall(hdr + frame)
        except (BrokenPipeError, ConnectionResetError):
            print(f"fake-helper: client disconnected at frame {seq}", flush=True)
            return
        seq += 1
        target = t0 + (seq * period)
        sleep = target - time.monotonic()
        if sleep > 0:
            time.sleep(sleep)
    print(f"fake-helper: sent {seq} frames in {time.monotonic()-t0:.2f}s", flush=True)
    cs.close()
    ss.close()
    os.unlink(args.socket)


if __name__ == "__main__":
    main()
