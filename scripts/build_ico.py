#!/usr/bin/env python3
"""Combine multiple PNGs into a single multi-resolution .ico file.

The .ico format is a simple binary container:
  - 6-byte header: reserved (2), type (2 = ico), count (2)
  - 16-byte directory entry per image
  - PNG data (or BMP) for each image

For PNG-encoded entries (which is what we want — they're
smaller than BMP and supported by every modern Windows),
the directory entry looks like:

  byte  width          (0 means 256)
  byte  height         (0 means 256)
  byte  color_count    (0 = >256 colors, i.e. truecolor)
  byte  reserved      (0)
  word  color_planes  (1)
  word  bits_per_pixel (32 for RGBA)
  dword image_size
  dword offset_to_data

This is the standard ICO file layout used by Windows since
Vista. Every Windows version since handles PNG-encoded
entries, so we don't need BMP fallbacks.
"""

import struct
import sys
from pathlib import Path

SIZES = [16, 32, 48, 64, 128, 256]


def main():
    if len(sys.argv) != 3:
        print("usage: build_ico.py <input-dir> <output.ico>", file=sys.stderr)
        sys.exit(2)
    in_dir = Path(sys.argv[1])
    out_path = Path(sys.argv[2])

    png_data = []
    for s in SIZES:
        png_file = in_dir / f"{s}.png"
        if not png_file.exists():
            print(f"missing: {png_file}", file=sys.stderr)
            sys.exit(1)
        png_data.append((s, png_file.read_bytes()))

    # ICO header: 6 bytes
    #   ICONDIR (6): reserved=0, type=1, count=N
    # ICONDIRENTRY (16 each): per-image metadata
    # ICON image data follows
    header_size = 6
    dir_size = 16 * len(png_data)
    offset = header_size + dir_size

    out = bytearray()
    # ICONDIR
    out += struct.pack("<HHH", 0, 1, len(png_data))

    for size, data in png_data:
        # ICONDIRENTRY
        # width/height: 0 means 256 (the spec quirk)
        w = 0 if size == 256 else size
        h = 0 if size == 256 else size
        out += struct.pack(
            "<BBBBHHII",
            w,            # width
            h,            # height
            0,            # color_count (0 = >256)
            0,            # reserved
            1,            # color_planes
            32,           # bits_per_pixel
            len(data),    # image_size
            offset,       # offset_to_data
        )
        offset += len(data)

    for _, data in png_data:
        out += data

    out_path.write_bytes(out)
    print(f"wrote {out_path} ({len(out)} bytes, {len(png_data)} sizes)")


if __name__ == "__main__":
    main()
