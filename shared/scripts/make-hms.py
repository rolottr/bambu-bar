#!/usr/bin/env python3
"""Builds Resources/HMS/hms_<lang>.json.deflate from ha-bambulab's hms_error_text/hms_<lang>.json.gz (MIT), which
hold Bambu Lab's own HMS and print error texts in each language their HMS service offers.
Keeps, for each code, the P2S-specific text or else the generic one. Stored as raw deflate (what
NSData.decompressed(using: .zlib) reads): about 100 KB per language instead of 700.
Usage: scripts/make-hms.py <folder with hms_*.json.gz> Resources/HMS"""
import gzip, json, os, sys, zlib

# ha-bambulab's file names → the app's localization codes.
LANGUAGES = {"en": "en", "de": "de", "es": "es", "fr": "fr", "hu": "hu", "it": "it", "ja": "ja", "ko": "ko",
             "nl": "nl", "pl": "pl", "pt-br": "pt-BR", "ru": "ru", "sv": "sv", "zh-Hans": "zh-Hans"}

src, dst = sys.argv[1], sys.argv[2]
os.makedirs(dst, exist_ok=True)
for name, lang in LANGUAGES.items():
    data = json.load(gzip.open(os.path.join(src, f"hms_{name}.json.gz")))
    out = {}
    for section in ("device_hms", "device_error"):
        table = {}
        for code, texts in data[section].items():
            chosen = None
            for text, models in texts.items():
                if "P2S" in models:
                    chosen = text
                    break
                if not models and chosen is None:
                    chosen = text
            if chosen:
                table[code.upper()] = chosen.strip()
        out["hms" if section == "device_hms" else "error"] = table
    raw = json.dumps(out, separators=(",", ":"), ensure_ascii=False, sort_keys=True).encode()
    deflate = zlib.compressobj(9, zlib.DEFLATED, -15)
    path = os.path.join(dst, f"hms_{lang}.json.deflate")
    open(path, "wb").write(deflate.compress(raw) + deflate.flush())
    print(path, len(out["hms"]), len(out["error"]))
