#!/usr/bin/env python3
"""Builds shared/HMS/hms_<lang>.json.deflate, Bambu Lab's HMS and print error texts, from Bambu's own HMS service
(https://e.bambulab.com/query.php?lang=<lang>&d=<serial prefix>, the catalogue Bambu Studio and Bambu Handy
download). Run it before a release; the app never downloads texts itself.

Bambu keeps one catalogue per printer model, and a code can read differently on each, or have text on one model
and none on another. Each output file holds, for every code, the text most models share ("hms", "error"), and
under "models" → serial prefix only the texts where that model differs. A text of "" means Bambu lists the code
with no text on that model: Bambu Studio and Bambu Handy don't show those (HMS_0503_0000_0003_0027, which the X2D
reports for as long as it runs, is one), and neither does Bambu Bar.

Bambu's service has dropped some codes it once listed (AMS units E to H, the AMS-HT's), which printers can still
report. --extra takes ha-bambulab's catalogues (MIT; custom_components/bambu_lab/pybambu/hms_error_text), saved
from earlier versions of the same service, and adds the codes Bambu no longer lists, with their common text.

A language's missing or empty texts fall back to English, and English decides whether a code is hidden. The model
list is read from Sources/Shared/PrinterModel.swift, so a new model is picked up once it has a serial prefix.
Stored as raw deflate (what NSData.decompressed(using: .zlib) and .NET's DeflateStream read).

Usage: shared/scripts/make-hms.py shared/HMS --extra <ha-bambulab hms_error_text> [--cache DIR]
--cache keeps Bambu's responses in DIR and reuses them on the next run (delete it to fetch again)."""
import argparse, collections, concurrent.futures, gzip, json, os, re, sys, time, urllib.request, zlib

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# The app's localization codes → Bambu's language codes. Czech and Ukrainian aren't offered; they get English.
LANGUAGES = {"en": "en", "de": "de", "es": "es", "fr": "fr", "hu": "hu", "it": "it", "ja": "ja", "ko": "ko",
             "nl": "nl", "pl": "pl", "pt-BR": "pt-BR", "ru": "ru", "sv": "sv", "tr": "tr", "zh-Hans": "zh-cn",
             "zh-Hant": "zh-tw"}
SECTIONS = {"device_hms": "hms", "device_error": "error"}
# The app's localization codes → ha-bambulab's file names, for --extra.
EXTRA_FILES = {"pt-BR": "pt-br"}


def model_prefixes():
    source = open(os.path.join(ROOT, "Sources", "Shared", "PrinterModel.swift"), encoding="utf-8").read()
    prefixes = list(dict.fromkeys(re.findall(r'serialPrefix: "([0-9A-Z]{3})"', source)))
    if len(prefixes) < 10:
        sys.exit(f"Found only {prefixes} in PrinterModel.swift")
    return prefixes


def fetch(lang, prefix, cache):
    path = cache and os.path.join(cache, f"{lang}_{prefix}.json")
    if path and os.path.exists(path):
        return json.load(open(path, encoding="utf-8"))
    url = f"https://e.bambulab.com/query.php?lang={lang}&d={prefix}"
    for attempt in range(4):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            raw = urllib.request.urlopen(request, timeout=60).read()
            data = json.loads(raw)
            break
        except Exception as error:
            if attempt == 3:
                sys.exit(f"{url}: {error}")
            time.sleep(2 ** attempt)
    if data.get("result") != 0 or not isinstance(data.get("data"), dict):
        sys.exit(f"{url}: no catalogue (result {data.get('result')})")
    if path:
        open(path, "wb").write(raw)
    return data


def texts(data):
    """{section: {code: text}} from one response."""
    out = {}
    for section in SECTIONS:
        block = data["data"][section]
        entries = next(v for k, v in block.items() if k != "ver")
        out[section] = {e["ecode"].upper(): " ".join(e["intro"].split()) for e in entries}
    return out


def extra_texts(folder, app_lang):
    """{section: {code: common text}} from ha-bambulab's hms_<lang>.json.gz, or {} if it has no such language."""
    path = os.path.join(folder, f"hms_{EXTRA_FILES.get(app_lang, app_lang)}.json.gz")
    if not os.path.exists(path):
        return {}
    data = json.load(gzip.open(path))
    # Each code maps its texts to the models they're for; the one for no particular model is the common one.
    return {section: {code.upper(): " ".join(next((t for t, models in by_text.items() if not models), "").split())
                      for code, by_text in data[section].items()} for section in SECTIONS}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dst")
    parser.add_argument("--extra", required=True)
    parser.add_argument("--cache")
    args = parser.parse_args()
    if args.cache:
        os.makedirs(args.cache, exist_ok=True)
    os.makedirs(args.dst, exist_ok=True)
    prefixes = model_prefixes()

    jobs = [(lang, prefix) for lang in LANGUAGES.values() for prefix in prefixes]
    with concurrent.futures.ThreadPoolExecutor(8) as pool:
        responses = dict(zip(jobs, pool.map(lambda job: fetch(*job, args.cache), jobs)))
    fetched = {job: texts(data) for job, data in responses.items()}
    english = {prefix: fetched["en", prefix] for prefix in prefixes}
    versions = sorted({responses["en", p]["data"]["device_hms"]["ver"] for p in prefixes})
    extra_english = extra_texts(args.extra, "en")
    if not extra_english:
        sys.exit(f"No hms_en.json.gz in {args.extra}")
    for app_lang, lang in LANGUAGES.items():
        out = {"hms": {}, "error": {}, "models": {}}
        extra = extra_texts(args.extra, app_lang)
        for section, key in SECTIONS.items():
            # Every code any model lists, with this model's text: the language's, else English's; "" if English has none.
            per_model = {}
            for prefix in prefixes:
                en, local = english[prefix][section], fetched[lang, prefix][section]
                per_model[prefix] = {code: (local.get(code) or text) if text else "" for code, text in en.items()}
            codes = sorted({code for table in per_model.values() for code in table})
            for code in codes:
                listed = [(p, per_model[p][code]) for p in prefixes if code in per_model[p]]
                common = collections.Counter(text for _, text in listed).most_common(1)[0][0]
                out[key][code] = common
                for prefix, text in listed:
                    if text != common:
                        out["models"].setdefault(prefix, {"hms": {}, "error": {}})[key][code] = text
            for code, text in extra_english[section].items():
                if code not in out[key]:
                    out[key][code] = (extra.get(section, {}).get(code) or text) if text else ""
        raw = json.dumps(out, separators=(",", ":"), ensure_ascii=False, sort_keys=True).encode()
        deflate = zlib.compressobj(9, zlib.DEFLATED, -15)
        path = os.path.join(args.dst, f"hms_{app_lang}.json.deflate")
        open(path, "wb").write(deflate.compress(raw) + deflate.flush())
        overrides = sum(len(m["hms"]) + len(m["error"]) for m in out["models"].values())
        added = sum(1 for section, key in SECTIONS.items() for c in extra_english[section]
                    if not any(c in english[p][section] for p in prefixes))
        hidden = sum(1 for k in ("hms", "error") for t in out[k].values() if not t)
        print(f"{path}: {len(out['hms'])} HMS, {len(out['error'])} errors ({added} from --extra), {hidden} hidden, "
              f"{overrides} model-specific, {os.path.getsize(path) // 1024} KB")
    print("Catalogue versions:", ", ".join(map(str, versions)))


if __name__ == "__main__":
    main()
