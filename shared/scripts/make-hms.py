#!/usr/bin/env python3
"""Builds shared/HMS/hms_<lang>.json.deflate from ha-bambulab's hms_error_text/hms_<lang>.json.gz (MIT), which
hold Bambu Lab's own HMS and print error texts in each language their HMS service offers.
Keeps, for each code, the P2S-specific text or else the generic one. A code whose every English text is empty maps
to "" in every language: Bambu Studio and Bambu Handy don't show those (HMS_0503_0000_0003_0027, which the X2D reports
for as long as it runs, is one), and neither does Bambu Bar. Stored as raw deflate (what
NSData.decompressed(using: .zlib) reads): about 100 KB per language instead of 700.
Usage: shared/scripts/make-hms.py <folder with hms_*.json.gz> shared/HMS"""
import gzip, json, os, sys, zlib

# ha-bambulab's file names → the app's localization codes.
LANGUAGES = {"en": "en", "de": "de", "es": "es", "fr": "fr", "hu": "hu", "it": "it", "ja": "ja", "ko": "ko",
             "nl": "nl", "pl": "pl", "pt-br": "pt-BR", "ru": "ru", "sv": "sv", "zh-Hans": "zh-Hans"}

src, dst = sys.argv[1], sys.argv[2]
os.makedirs(dst, exist_ok=True)
english = json.load(gzip.open(os.path.join(src, "hms_en.json.gz")))
hidden = {section: {code.upper() for code, texts in english[section].items() if not any(t.strip() for t in texts)}
          for section in ("device_hms", "device_error")}
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
            if chosen and chosen.strip():
                table[code.upper()] = chosen.strip()
        table.update(dict.fromkeys(hidden[section], ""))
        out["hms" if section == "device_hms" else "error"] = table
    raw = json.dumps(out, separators=(",", ":"), ensure_ascii=False, sort_keys=True).encode()
    deflate = zlib.compressobj(9, zlib.DEFLATED, -15)
    path = os.path.join(dst, f"hms_{lang}.json.deflate")
    open(path, "wb").write(deflate.compress(raw) + deflate.flush())
    print(path, len(out["hms"]), len(out["error"]))
