#!/usr/bin/env python3
"""Builds Resources/filaments.json from Bambu Studio's filament profiles (AGPL-3.0, data only):
  presets: preset id (tray_info_idx) -> [vendor, product], from resources/profiles/BBL/filament/* @base.json
  colors:  "<preset id>-<colour code>" -> English colour name, and "<preset id>-<RRGGBB>" -> name,
           from resources/profiles/BBL/filament/filaments_color_codes.json
Usage: scripts/make-filaments.py Resources/filaments.json [git ref, default master]"""
import json, sys, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor

dst = sys.argv[1]
ref = sys.argv[2] if len(sys.argv) > 2 else "master"
repo = "bambulab/BambuStudio"
folder = "resources/profiles/BBL/filament/"


def fetch(path):
    url = f"https://raw.githubusercontent.com/{repo}/{ref}/{urllib.parse.quote(path)}"
    return json.load(urllib.request.urlopen(url))


tree = json.load(urllib.request.urlopen(f"https://api.github.com/repos/{repo}/git/trees/{ref}?recursive=1"))
bases = [t["path"] for t in tree["tree"] if t["path"].startswith(folder) and t["path"].endswith(" @base.json")]


def preset(path):
    profile = fetch(path)
    title = path.rsplit("/", 1)[-1][:-len(" @base.json")]
    vendor = profile.get("filament_vendor")
    vendor = vendor[0] if isinstance(vendor, list) else vendor
    if not vendor or vendor == "None":
        vendor = "Bambu Lab" if title.startswith("Bambu ") else "Generic" if title.startswith("Generic ") else None
    product = title
    for prefix in ("Bambu ", "Generic ", (vendor or "") + " "):
        if prefix.strip() and product.startswith(prefix):
            product = product[len(prefix):]
            break
    return profile.get("filament_id"), vendor, product


presets = {}
with ThreadPoolExecutor(16) as pool:
    for fid, vendor, product in pool.map(preset, bases):
        if fid and vendor:
            presets[fid] = [vendor, product]


def code(value):
    """"K01" and "K1" are the same colour code."""
    letters = value.rstrip("0123456789")
    digits = value[len(letters):]
    return letters + (str(int(digits)) if digits else "")


colors, by_hex, clashes = {}, {}, set()
for entry in fetch(folder + "filaments_color_codes.json")["data"]:
    name = entry["fila_color_name"].get("en", "").strip()
    if not name:
        continue
    fid = entry["fila_id"]
    colors[f"{fid}-{code(entry['color_code'])}"] = name
    hex_key = f"{fid}-{entry['fila_color'][0].lstrip('#')[:6].upper()}"
    if by_hex.get(hex_key, name) != name:
        clashes.add(hex_key)
    by_hex[hex_key] = name
for key in clashes:
    del by_hex[key]
colors.update(by_hex)

json.dump({"presets": presets, "colors": colors}, open(dst, "w"), separators=(",", ":"), ensure_ascii=False, sort_keys=True)
print(dst, len(presets), "presets,", len(colors), "colour keys")
