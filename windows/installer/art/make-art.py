#!/usr/bin/env python3
"""The installer's pictures (BambuBar.iss: WizardImageFile, WizardSmallImageFile), one per Windows scale.

    cd windows
    dotnet run --project tools/BambuBar.Cli -- --art /tmp/printer.png 588 696
    python3 installer/art/make-art.py /tmp/printer.png       # needs Pillow; writes the .bmp files beside this script

The tall picture (welcome and finish pages): a deep green night, a glow of Bambu green, the printer at work standing on
it with its reflection, and the name in Nunito (OFL, the widgets' font). The small one (the other pages' header): the
app icon. 24-bit BMPs, which every Inno Setup 6 reads.
"""
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
FONT = os.path.join(REPO, "rainmeter", "Skins", "BambuBar", "@Resources", "Fonts", "Nunito-Bold.ttf")
ICON = os.path.join(REPO, "windows", "src", "BambuBar.Windows", "Assets", "AppIcon.png")

# Inno Setup's sizes for 100, 125, 150, 175, 200, 225 and 250 % (WizardImageFile / WizardSmallImageFile).
TALL = [(164, 314), (192, 386), (246, 459), (273, 556), (328, 628), (355, 700), (410, 797)]
SMALL = [(55, 55), (64, 68), (83, 80), (92, 97), (110, 106), (119, 123), (138, 140)]

TOP = (12, 20, 17)        # near black, a touch of green
BOTTOM = (6, 58, 34)      # Bambu's deep green
GLOW = (33, 204, 107)     # Bambu green (Palette.Accent)


def vertical(w, h, top, bottom):
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        t = y / max(1, h - 1)
        t = t * t * (3 - 2 * t)
        c = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
        for x in range(w):
            px[x, y] = c
    return img


def glow(w, h, cx, cy, rx, ry, color, alpha):
    layer = Image.new("L", (w, h), 0)
    ImageDraw.Draw(layer).ellipse((cx - rx, cy - ry, cx + rx, cy + ry), fill=round(255 * alpha))
    layer = layer.filter(ImageFilter.GaussianBlur(max(rx, ry) * 0.45))
    return Image.new("RGB", (w, h), color), layer


def tall(printer, w, h):
    s = w / 164
    img = vertical(w, h, TOP, BOTTOM)
    # The glow the printer stands in.
    col, mask = glow(w, h, w * 0.5, h * 0.62, w * 0.6, h * 0.25, GLOW, 0.5)
    img.paste(col, (0, 0), mask)
    # A floor line of light.
    col, mask = glow(w, h, w * 0.5, h * 0.87, w * 0.5, 6 * s, (120, 255, 180), 0.3)
    img.paste(col, (0, 0), mask)

    # The printer, its foot on the floor, and its reflection fading below.
    ph = round(h * 0.52)
    pw = round(printer.width * ph / printer.height)
    if pw > w * 0.8:
        pw = round(w * 0.8)
        ph = round(printer.height * pw / printer.width)
    p = printer.resize((pw, ph), Image.LANCZOS)
    px, py = (w - pw) // 2, round(h * 0.875) - ph
    reflection = p.transpose(Image.FLIP_TOP_BOTTOM)
    fade = Image.linear_gradient("L").resize((pw, ph)).point(lambda v: max(0, 255 - v * 3) * 0.28)
    fade = ImageChops.multiply(reflection.getchannel("A"), fade.convert("L"))
    reflection.putalpha(fade)
    img.paste(reflection, (px, py + ph - round(4 * s)), reflection)
    img.paste(p, (px, py), p)

    # The name and what it is, the line shrunk to fit.
    d = ImageDraw.Draw(img)
    title = ImageFont.truetype(FONT, round(26 * s))
    y = round(34 * s)
    d.text((w / 2, y), "Bambu Bar", font=title, fill=(255, 255, 255), anchor="mt")
    line = "Your printers in the tray"
    size = 11 * s
    while True:
        sub = ImageFont.truetype(FONT, round(size))
        if d.textlength(line, font=sub) <= w * 0.84 or size < 6:
            break
        size *= 0.95
    d.text((w / 2, y + round(34 * s)), line, font=sub, fill=(130, 232, 172), anchor="mt")
    return img


def small(icon, w, h):
    img = Image.new("RGB", (w, h), (255, 255, 255))
    side = round(min(w, h) * 0.86)
    i = icon.resize((side, side), Image.LANCZOS)
    img.paste(i, ((w - side) // 2, (h - side) // 2), i)
    return img


def main():
    printer = Image.open(sys.argv[1]).convert("RGBA")
    printer = printer.crop(printer.getbbox())
    icon = Image.open(ICON).convert("RGBA")
    for w, h in TALL:
        tall(printer, w, h).save(os.path.join(HERE, f"wizard-{w}x{h}.bmp"))
    for w, h in SMALL:
        small(icon, w, h).save(os.path.join(HERE, f"small-{w}x{h}.bmp"))
    print("written to", HERE)


if __name__ == "__main__":
    main()
