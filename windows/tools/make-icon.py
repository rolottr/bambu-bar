#!/usr/bin/env python3
"""BambuBar.ico and AppIcon.png for Windows, from the Mac's 1024 px icon (needs Pillow):

    python3 windows/tools/make-icon.py Resources/Assets.xcassets/AppIcon.appiconset/icon_512x512@2x.png

The Mac icon keeps Apple's margins around its rounded square (about 8 % a side); Windows draws icons edge to edge, so
there it looked shrunken. 48 px and up: the rounded square cropped to its edges. 16 to 40 px (taskbar, title bar, tray
lists): a closer crop on the printer, so the coloured print still reads, in a rounded square of its own.
"""
import os
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "..", "src", "BambuBar.Windows", "Assets")
SMALL = [16, 20, 24, 32, 40]
LARGE = [48, 64, 96, 128, 256]


def rounded(img, radius):
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, img.width - 1, img.height - 1), radius=radius, fill=255)
    out = img.copy()
    out.putalpha(Image.composite(img.getchannel("A"), mask, mask).point(lambda v: v))
    alpha = Image.new("L", img.size, 0)
    alpha.paste(img.getchannel("A"), (0, 0), mask)
    out.putalpha(alpha)
    return out


def main():
    src = Image.open(sys.argv[1]).convert("RGBA")
    s = src.width / 1024
    full = src.crop(src.getchannel("A").getbbox())
    # The printer and its print, with a little of the dark body around it.
    near = src.crop(tuple(round(v * s) for v in (200, 180, 824, 804)))
    near = rounded(near, round(near.width * 0.2))
    images = {n: near.resize((n, n), Image.LANCZOS) for n in SMALL}
    images.update({n: full.resize((n, n), Image.LANCZOS) for n in LARGE})
    order = SMALL + LARGE
    first = images[order[-1]]
    first.save(os.path.join(ASSETS, "BambuBar.ico"), sizes=[(n, n) for n in order],
               append_images=[images[n] for n in order[:-1]])
    images[256].save(os.path.join(ASSETS, "AppIcon.png"))
    print("written to", os.path.normpath(ASSETS))


if __name__ == "__main__":
    main()
