#!/usr/bin/env python3
"""Export macOS MenuBarIcon's 18-unit geometry for GTK and Plasma symbolic loaders.

Keep coordinates in sync with Sources/BambuBar/UI/MenuBarIcon.swift. Filled paths
are intentional: symbolic loaders recolor SVG shapes and cannot preserve stroked
outlines or alpha masks consistently. The printer, badge and dash outlines below
encode those holes in geometry, so desktop colors cannot fill them in.
"""
import math
import sys
from pathlib import Path

PREFIX = 'dev.rolottr.BambuBar-tray-'


def rect(x, y, w, h, r=0):
    r = min(r, w / 2, h / 2)
    return (f'M{x+r:g} {y:g}H{x+w-r:g}A{r:g} {r:g} 0 0 1 {x+w:g} {y+r:g}'
            f'V{y+h-r:g}A{r:g} {r:g} 0 0 1 {x+w-r:g} {y+h:g}'
            f'H{x+r:g}A{r:g} {r:g} 0 0 1 {x:g} {y+h-r:g}'
            f'V{y+r:g}A{r:g} {r:g} 0 0 1 {x+r:g} {y:g}Z')


def circle(x, y, r):
    return f'M{x+r:g} {y:g}A{r:g} {r:g} 0 1 0 {x-r:g} {y:g}A{r:g} {r:g} 0 1 0 {x+r:g} {y:g}Z'


def offline_outline():
    # The Mac's 1.5-wide outline with 2.2/1.6 dashes, expanded to filled polygons.
    r = 2.6
    segments = [
        (8.3, lambda t: (4.85+t, 1.75, 1, 0)),
        (math.pi*r/2, lambda t: (13.15+r*math.sin(t/r), 4.35-r*math.cos(t/r), math.cos(t/r), math.sin(t/r))),
        (9.3, lambda t: (15.75, 4.35+t, 0, 1)),
        (math.pi*r/2, lambda t: (13.15+r*math.cos(t/r), 13.65+r*math.sin(t/r), -math.sin(t/r), math.cos(t/r))),
        (8.3, lambda t: (13.15-t, 16.25, -1, 0)),
        (math.pi*r/2, lambda t: (4.85-r*math.sin(t/r), 13.65+r*math.cos(t/r), -math.cos(t/r), -math.sin(t/r))),
        (9.3, lambda t: (2.25, 13.65-t, 0, -1)),
        (math.pi*r/2, lambda t: (4.85-r*math.cos(t/r), 4.35-r*math.sin(t/r), math.sin(t/r), -math.cos(t/r))),
    ]
    total = sum(length for length, _ in segments)
    def point(distance):
        for length, fn in segments:
            if distance <= length:
                return fn(distance)
            distance -= length
        return segments[-1][1](segments[-1][0])
    paths = []
    start = 0
    while start < total:
        end = min(start + 2.2, total)
        points = [point(start + (end-start)*i/32) for i in range(33)]
        outer = [(x + .75*dy, y - .75*dx) for x, y, dx, dy in points]
        inner = [(x - .75*dy, y + .75*dx) for x, y, dx, dy in reversed(points)]
        paths.append('M' + 'L'.join(f'{x:.5f} {y:.5f}' for x, y in outer+inner) + 'Z')
        start += 3.8
    return paths


def svg(state, step=0, alert=False):
    offline = state == 'offline'
    badge = alert and state != 'failed' and not offline
    if offline:
        paths = offline_outline()
    else:
        if badge:
            # Clip just the frame and gantry around the badge's 1.2-unit clear ring.
            top = 14.5-math.sqrt(4.5**2-2.5**2)
            inner_top = 14.5-math.sqrt(4.5**2-1**2)
            right = 3.5+math.sqrt(4.5**2-2**2)
            inner_right = 3.5+math.sqrt(4.5**2-.5**2)
            outline = (f'M{top:g} 1H4.85A3.35 3.35 0 0 0 1.5 4.35V13.65A3.35 3.35 0 0 0 4.85 17H13.15A3.35 3.35 0 0 0 16.5 13.65'
                       f'V{right:g}A4.5 4.5 0 0 1 15 {inner_right:g}V13.65A1.85 1.85 0 0 1 13.15 15.5H4.85A1.85 1.85 0 0 1 3 13.65V4.35A1.85 1.85 0 0 1 4.85 2.5'
                       f'H{inner_top:g}A4.5 4.5 0 0 1 {top:g} 1Z')
            gx1 = 14.5-math.sqrt(4.5**2-1.3**2)
            gx2 = 14.5-math.sqrt(4.5**2-2.1**2)
            gantry = f'M3.25 4.8H{gx1:g}A4.5 4.5 0 0 0 {gx2:g} 5.6H3.25Z'
        else:
            outline = rect(1.5, 1, 15, 16, 3.35) + rect(3, 2.5, 12, 13, 1.85)
            gantry = rect(3.25, 4.8, 11.5, .8)
        paths = [outline, gantry, rect(4.45, 13.4, 9.1, 1.3, .6)]
        if state == 'paused':
            paths += [rect(6.6, 7.2, 1.6, 4.8, .8), rect(9.9, 7.2, 1.6, 4.8, .8)]
        elif state == 'failed':
            paths += [rect(8.2, 6.6, 1.6, 4.4, .8), circle(9, 12.7, .9)]
        elif state in ('printing', 'finished'):
            h = 5.6 if state == 'finished' else max(.9, 5.6 * step / 20)
            paths.append(rect(6.2, 13.4-h, 5.6, h, .9))
            if state != 'finished':
                paths.append(rect(7.3, max(5.8, 13.4-h-2.3), 3.4, 1.8, .5))
        else:
            paths.append(rect(3.85, 5.8, 3.2, 1.8, .5))
        if badge:
            paths.append(circle(14.5, 3.5, 3.3) + rect(13.9, 1.5, 1.2, 2.6, .6) + circle(14.5, 5.25, .65))
    style = '<style type="text/css" id="current-color-scheme">.ColorScheme-Text { color:#232629; }</style>'
    body = ''.join(f'<path class="ColorScheme-Text" fill="currentColor" fill-rule="evenodd" d="{p}"/>' for p in paths)
    return '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 18 18">' + style + body + '</svg>\n'


def generate(folder):
    folder.mkdir(parents=True, exist_ok=True)
    for state in ('idle', 'offline', 'paused', 'failed', 'finished', 'printing'):
        for step in range(21) if state == 'printing' else (0,):
            for alert in (False,) if state in ('offline', 'failed') else (False, True):
                key = state + (f'-{step}' if state == 'printing' else '') + ('-alert' if alert else '')
                (folder / f'{PREFIX}{key}-symbolic.svg').write_text(svg(state, step, alert))


if __name__ == '__main__':
    generate(Path(sys.argv[1]))
