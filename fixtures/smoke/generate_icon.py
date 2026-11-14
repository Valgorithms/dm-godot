#!/usr/bin/env python3
"""Generate the fixture's exact 3-state DMI asset using only Python's stdlib.

These geometric test pixels are original project fixtures, not game artwork.
Run this script to reproduce smoke.dmi byte for byte with the same zlib version.
"""

from pathlib import Path
import struct
import zlib


def chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def main() -> None:
    metadata = """# BEGIN DMI
version = 4.0
\twidth = 32
\theight = 32
state = "floor"
\tdirs = 1
\tframes = 1
state = "wall"
\tdirs = 1
\tframes = 1
state = "player"
\tdirs = 1
\tframes = 1
# END DMI
"""
    pixels = bytearray()
    for y in range(32):
        pixels.append(0)  # PNG filter: none
        for x in range(96):
            frame, local_x = divmod(x, 32)
            if frame == 0:
                value = 220 if local_x in (0, 31) or y in (0, 31) else 255
                rgba = (value, value, value, 255)
            elif frame == 1:
                value = 180 if local_x in (0, 31) or y in (0, 15, 31) else 255
                rgba = (value, value, value, 255)
            else:
                inside = (local_x - 15.5) ** 2 + (y - 15.5) ** 2 <= 100
                rgba = (255, 255, 255, 255 if inside else 0)
            pixels.extend(rgba)
    data = b"\x89PNG\r\n\x1a\n"
    data += chunk(b"IHDR", struct.pack(">IIBBBBB", 96, 32, 8, 6, 0, 0, 0))
    data += chunk(b"zTXt", b"Description\x00\x00" + zlib.compress(metadata.encode("latin-1")))
    data += chunk(b"IDAT", zlib.compress(bytes(pixels), level=9))
    data += chunk(b"IEND", b"")
    Path(__file__).with_name("smoke.dmi").write_bytes(data)


if __name__ == "__main__":
    main()
