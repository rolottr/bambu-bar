#!/usr/bin/env python3
"""Draws the widgets' soft shadows: Skins/BambuBar/@Resources/Images/shadow-<Size>.png.

The mockup's tile shadow (0 10px 26px rgba(0,0,0,.36), 0 2px 6px rgba(0,0,0,.26)) does not fit in the skins'
16 px margin, so it is drawn tighter with the same look: a wide soft layer (y +5, sigma 6, 34 %) and a contact
layer (y +2, sigma 2.5, 24 %), faded to nothing at the window's edge. Rendered at 2x (the skins draw it at 1x
logical size), so it stays smooth at 200 %. Standard library only: python3 rainmeter/tests/make_shadows.py
"""
import math
import os
import struct
import zlib

MARGIN, RADIUS, SCALE = 16, 14, 2
SIZES = {"Small": (180, 180), "Medium": (380, 180), "Large": (380, 380)}
LAYERS = [(5.0, 6.0, 0.34), (2.0, 2.5, 0.24)]  # (y offset, sigma, alpha), in logical px


def rounded_rect_distance(px, py, x, y, w, h, r):
    """Signed distance from (px, py) to a rounded rectangle, positive outside."""
    cx, cy = x + w / 2, y + h / 2
    qx, qy = abs(px - cx) - (w / 2 - r), abs(py - cy) - (h / 2 - r)
    outside = math.hypot(max(qx, 0), max(qy, 0))
    return outside + min(max(qx, qy), 0) - r


def shadow(width, height):
    W, H = (width + 2 * MARGIN) * SCALE, (height + 2 * MARGIN) * SCALE
    rows = []
    for j in range(H):
        row = bytearray([0])  # filter: none
        y = (j + 0.5) / SCALE
        for i in range(W):
            x = (i + 0.5) / SCALE
            keep = 1.0
            for dy, sigma, a in LAYERS:
                d = rounded_rect_distance(x, y, MARGIN, MARGIN + dy, width, height, RADIUS)
                keep *= 1 - a * 0.5 * math.erfc(d / (sigma * math.sqrt(2)))
            alpha = 1 - keep
            edge = min(x, y, width + 2 * MARGIN - x, height + 2 * MARGIN - y)
            if edge < 4:  # fade out over the last 4 px so nothing is cut off
                t = max(0.0, edge / 4)
                alpha *= t * t * (3 - 2 * t)
            row += bytes((0, 0, 0, int(round(alpha * 255))))
        rows.append(bytes(row))
    return W, H, b"".join(rows)


def png(path, w, h, data):
    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(data, 9)))
        f.write(chunk(b"IEND", b""))


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Skins", "BambuBar", "@Resources", "Images")
    for size, (w, h) in SIZES.items():
        pw, ph, data = shadow(w, h)
        path = os.path.normpath(os.path.join(out, "shadow-%s.png" % size))
        png(path, pw, ph, data)
        print(path, pw, "x", ph)
