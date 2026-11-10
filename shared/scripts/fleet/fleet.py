#!/usr/bin/env python3
"""A mock fleet of Bambu Lab printers for developing Bambu Bar with many printers at once.

Each printer gets its own TLS MQTT listener (user `bblp`, its own access code) on consecutive ports from
BASE_PORT, answers `pushall` and `get_version` like the real thing and streams deltas while it prints:
progress, layers, temperatures, fans. Pause, resume, stop, speed and the chamber light work. Every Bambu Lab
FDM model is in the fleet, and each sends only what the real model sends (see MODELS): the X1, P1 and A1
families the flat report format, starting from the real A1 report in ../emulator/a1-sample.json, the others the
newer format, starting from the real P2S report in ../emulator/p2s-sample.json. Placeholders included: a P1S
or A1 sends a chamber temperature of 5 without a sensor and "0" for fans it doesn't have, as the real ones do.
One printer is listed but never answers, like one that's switched off.

It also writes OUT/BambuStudio.conf the way Bambu Studio saves LAN printers (access codes and the scrambled
`user_access_dev_ip`, see BBLUtil.cpp), so Bambu Bar's bulk import can be tried against it. With SSDP_TARGET
set (host.docker.internal) it sends each printer's SSDP NOTIFY there too, on SSDP_PORT, so they show up as
discovered in a Bambu Bar started with `--emulator`.

SERIAL_CASE reproduces printers whose serial turns up in another case than the uppercase one on their
certificate, as Bambu Studio sometimes saves it (each was taken for two printers, one of them offline):
  upper  (default) every serial uppercase, as before.
  lower  the settings file keys and the SSDP announcements use the lowercase serial.
  mixed  the settings file has a stale code under the uppercase serial (`access_code`) and the working one
         under the lowercase serial (`user_access_code`, `user_access_dev_ip`); announcements alternate between
         the two spellings. scripts/integration-test.sh runs the fleet this way.

Run it with scripts/fleet/run.sh, or: docker compose -f scripts/fleet/compose.yaml up --build"""
import copy, json, os, random, socket, subprocess, sys, threading, time, uuid

try:
    import paho.mqtt.client as mqtt
except ImportError:  # `--dump` runs without it
    mqtt = None

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.environ.get("OUT", "/out")
BASE_PORT = int(os.environ.get("BASE_PORT", "18801"))
# The address the app should use for these printers: where the ports are published.
PUBLISHED_HOST = os.environ.get("PUBLISHED_HOST", "127.0.0.1")
SSDP_TARGET = os.environ.get("SSDP_TARGET", "")
# Not 2021: Bambu Bar in daily use listens there too and would take the packets; `--emulator` runs listen on 2121.
SSDP_PORT = int(os.environ.get("SSDP_PORT", "2121"))
SERIAL_CASE = os.environ.get("SERIAL_CASE", "upper")
if SERIAL_CASE not in ("upper", "lower", "mixed"):
    raise SystemExit(f"SERIAL_CASE must be upper, lower or mixed, not {SERIAL_CASE!r}")
SPEED = float(os.environ.get("SPEED", "1"))  # >1 prints faster
CERTS = "/tmp/fleet-certs"

# What each model reports, so the fleet sends what the real printer sends and nothing it doesn't. Capabilities are
# from Bambu Studio's resources/printers/<code>.json (support_chamber_temp_display, support_aux_fan,
# support_chamber_fan, support_chamber_temp_edit_range, use_ams_type, default_extruder_count, sn_prefix) and its
# report parser (src/slic3r/GUI/DeviceManager.cpp, DeviceCore/DevChamber.cpp, DevFan.cpp, DevFilaSystem.cpp).
# Two report formats:
#   legacy  flat fields only: nozzle_temper, chamber_temper (+ ctt, the heater target), cooling_fan_speed and
#           big_fan1/2_speed as 0-15, the external spool as `vt_tray`. X1, P1 and A1 families. The A1 shape is
#           a real report (../emulator/a1-sample.json).
#   device  the flat fields plus a `device` block: packed temperatures, `ctc` for the chamber, `airduct` fans,
#           the external spool as `vir_slot`. P2S (a real report, ../emulator/p2s-sample.json), H2, X2D, A2L, and
#           the H3C from its own real report (../emulator/h3c-sample.json): an AMS 3H with nine slots and per-zone
#           humidity, nine hotends in a rack, nine virtual slots, five fans.
# chamber: "sensor" reports a real reading, "placeholder" sends chamber_temper 5 without a sensor (P1, A1: the
# app must hide it), None sends nothing. door: `fun` bit 12, the printer has a door sensor and reports it in `stat`.
# lights: the nodes in lights_report (the A1 lists a chamber_light it doesn't have).
# fans: the `airduct` parts as (id, func, percent while printing); by default part cooling, then aux and exhaust
# where the model has them. Bambu Studio names a part by bits 4-11 of its id: 1 part, 2 aux, 3 exhaust, and on the
# H3C 10 left aux and 15 chamber (resources/printers/N10.json, Widgets/FanControl.cpp).
# Not sampled from hardware here: the A2L's exact fields follow the newer P2S/H2 format.
def model(product, prefix, fmt, ams, nozzles=1, heater=None, chamber="sensor", aux=True, chamber_fan=True, door=True,
          lights=("chamber_light", "work_light"), fun=True, sample=None, fans=None):
    if fans is None:
        fans = [(16, 0, 90)] + ([(32, 6, 30)] if aux else []) + ([(48, 1, 40)] if chamber_fan else [])
    return {"product": product, "prefix": prefix, "format": fmt, "ams": ams, "nozzles": nozzles, "heater": heater,
            "chamber": chamber, "aux": aux, "chamber_fan": chamber_fan, "door": door, "lights": lights, "fun": fun,
            "sample": sample, "fans": fans}

MODELS = {
    "N7": model("P2S", "22E", "device", "pro2"),
    "C12": model("P1S", "01P", "legacy", "ams", chamber="placeholder", door=False, lights=("chamber_light",)),
    "C11": model("P1P", "01S", "legacy", "ams", chamber="placeholder", door=False, lights=("chamber_light",)),
    "BL-P001": model("X1 Carbon", "00M", "legacy", "ams"),
    "BL-P002": model("X1", "00W", "legacy", "ams"),
    "C13": model("X1E", "03W", "legacy", "ams", heater=60),
    "N6": model("X2D", "20P", "device", "pro2", nozzles=2, heater=65),
    "N2S": model("A1", "039", "legacy", "lite", chamber="placeholder", aux=False, chamber_fan=False, door=False,
                 lights=("chamber_light",), fun=False),
    "N1": model("A1 mini", "030", "legacy", "lite", chamber="placeholder", aux=False, chamber_fan=False, door=False,
                lights=("chamber_light",), fun=False),
    "N9": model("A2L", "26A", "device", "lite_mixed", chamber=None, aux=False, chamber_fan=False, door=False, lights=("chamber_light",)),
    "O1D": model("H2D", "094", "device", "pro2", nozzles=2, heater=65),
    "O1E": model("H2D Pro", "239", "device", "pro2", nozzles=2, heater=65),
    "O1S": model("H2S", "093", "device", "pro2", heater=65),
    "O1C": model("H2C", "31B", "device", "pro2", nozzles=2, heater=65),
    "N10": model("H3C", "32X", "device", "ams3h", heater=65, sample="h3c",
                 fans=[(16, 0, 90), (32, 6, 30), (160, 5, 25), (48, 2, 40), (240, 10, 35)]),
}
# AMS unit `info` codes as the printers send them (low nibble, Bambu Studio's DevAmsType: 1 AMS, 2 AMS Lite,
# 3 AMS 2 Pro, 4 AMS HT, 5 the A2L's AMS Lite, 6 the H3C's AMS 3H, as in h3c-sample.json).
AMS_MODULE = {"pro2": ("n3f", "AMS 2 Pro", "1003"), "ams": ("ams", "AMS", "1001"), "lite": ("ams_f1", "AMS Lite", "1002"),
              "ht": ("n3s", "AMS HT", "1004"), "lite_mixed": ("ams_f1", "AMS Lite", "1005"),
              "ams3h": ("n10a", "AMS 3H", "240002006"), "amsB": (None, "AMS-B", "240002007")}
SLOTS = {"ht": 1, "ams3h": 9, "amsB": 9}
# The H3C's rack AMS: the AMS 3H (type 6, rack A) as sampled, or the AMS-B (type 7, rack B). No AMS-B has been
# sampled anywhere; from Bambu Studio's source (DevFilaSystem.cpp, DevNozzleRack.h): slots at tray_exist_bits 37+,
# the unit at ams_exist_bits bit 14, no feeder motor. Guessed here: unit id 33, nine slots, and no humidity or
# temperature (it may have no sensors), so the app shows none rather than a made-up value.
RACK_AMS = {"ams3h": 32, "amsB": 33}


def tray_bit(unit_id, kind, slot):
    """The slot's bit in tray_exist_bits (Bambu Studio's DevAms::GetTrayId)."""
    if kind == "ht":
        return 16 + unit_id - 128 + slot
    if kind == "lite_mixed":
        return 24 + slot
    if kind == "ams3h":
        return 28 + slot
    if kind == "amsB":
        return 37 + slot
    return unit_id * 4 + slot


def unit_bit(unit_id, kind):
    """The unit's bit in ams_exist_bits."""
    return {"ht": 4 + unit_id - 128, "lite_mixed": 12, "ams3h": 13, "amsB": 14}.get(kind, unit_id)

# Bambu Lab spools: (product, preset, colour code, RRGGBB, material). Colour codes are Bambu Studio's.
def bambu(product, preset, code, hex_, material, remain):
    return {"kind": "bambu", "product": product, "preset": preset, "code": code, "hex": hex_, "material": material, "remain": remain}

def third(preset, hex_, material):
    return {"kind": "third", "preset": preset, "hex": hex_, "material": material}

ORANGE = bambu("PLA Basic", "GFA00", "A0", "FF6A13", "PLA", 64)
COBALT = bambu("PLA Basic", "GFA00", "B3", "0056B8", "PLA", 22)
TURQ = bambu("PLA Basic", "GFA00", "B5", "00B1B7", "PLA", 91)
GRASS = bambu("PLA Matte", "GFA01", "G1", "61C680", "PLA", 99)
CHARCOAL = bambu("PLA Matte", "GFA01", "K1", "000000", "PLA", 76)
IVORY = bambu("PLA Matte", "GFA01", "W2", "FFFFFF", "PLA", 48)
SILVER = bambu("PLA Silk", "GFA05", "D1", "EAECEB", "PLA", 35)
SILK_GREEN = bambu("PLA Silk", "GFA05", "G1", "4CE4A0", "PLA", 80)
PETG_BLUE = bambu("PETG HF", "GFG02", "B1", "1F79E5", "PETG", 57)
PETG_GRAY = bambu("PETG HF", "GFG02", "D0", "ADB1B2", "PETG", 12)
ABS_AZURE = bambu("ABS", "GFB00", "B4", "489FDF", "ABS", 70)
PA6CF = bambu("PA6-CF", "GFN05", "K0", "000000", "PA6-CF", 66)
SUNLU_PINK = third("GFSNL02", "F330F9", "PLA")
SUNLU_WHITE = third("GFSNL03", "FFFFFF", "PLA")
GENERIC_PETG = third("GFG99", "2E7D5B", "PETG")
ESUN_RED = third("GFL03", "D23A3A", "PLA")

# The most an H2 takes: AMS 2 Pro B-D after the first one, then eight single-slot AMS HTs (ids 128-135).
H2C_UNITS = [("pro2", [SILVER, IVORY, None, PETG_BLUE]), ("pro2", [COBALT, SUNLU_WHITE, PETG_GRAY, CHARCOAL]),
             ("pro2", [TURQ, ESUN_RED, SILK_GREEN, ORANGE])] + [("ht", [s]) for s in
             [SUNLU_PINK, PETG_BLUE, ORANGE, IVORY, GRASS, ABS_AZURE, PA6CF, None]]

# name, model, state, job, progress %, job minutes, active slot, spools (AMS slots, or the external spool)[, more units]
# Every model in MODELS once (the P1S twice, one switched off). The P1P runs without an AMS, from the external spool.
PRINTERS = [
    ("Workshop P2S", "N7", "RUNNING", "Planetary Gearbox", 42, 210, 1, [SUNLU_PINK, GRASS, CHARCOAL, IVORY]),
    ("Garage P1S", "C12", "RUNNING", "Cable Clips x24", 77, 95, 0, [ORANGE, COBALT, None, TURQ]),
    ("X1C Lab", "BL-P001", "PAUSE", "Drone Frame", 58, 340, 0, [PETG_BLUE, PETG_GRAY, GENERIC_PETG, None]),
    ("X1E Engineering", "C13", "RUNNING", "PA6-CF Bracket", 12, 480, 0, [PA6CF, ABS_AZURE, None, None]),
    ("H2D Studio", "O1D", "RUNNING", "Two-tone Vase", 63, 260, 2, [SILVER, SILK_GREEN, CHARCOAL, ESUN_RED]),
    ("H2S Prototype", "O1S", "PREPARE", "Enclosure Lid", 0, 150, 0, [IVORY, CHARCOAL, ORANGE, None]),
    ("P1P Corner", "C11", "IDLE", "", 0, 0, None, [ESUN_RED]),
    ("A1 Desk", "N2S", "FINISH", "Desk Organizer", 100, 120, 3, [GRASS, SUNLU_WHITE, COBALT, ORANGE]),
    ("X2D Shop", "N6", "RUNNING", "R40", 44, 240, 0, [ABS_AZURE, CHARCOAL, PETG_GRAY, None]),
    ("Mini", "N1", "FAILED", "3DBenchy", 23, 50, 0, [ORANGE, None, None, None]),
    ("H2C Batch", "O1C", "RUNNING", "Gridfinity Bins", 35, 600, 0, [ORANGE, IVORY, COBALT, GRASS], H2C_UNITS),
    ("X1 Basement", "BL-P002", "RUNNING", "Hinge Pins", 21, 70, 2, [CHARCOAL, IVORY, ABS_AZURE, None]),
    ("H2D Pro Line", "O1E", "IDLE", "", 0, 0, None, [PETG_BLUE, ORANGE, None, GRASS]),
    ("A2L Big Bed", "N9", "RUNNING", "Cosplay Helmet", 18, 720, 1, [IVORY, SUNLU_PINK, None, None]),
    ("H3C Rack", "N10", "RUNNING", "Nozzle Rack Test", 52, 300, 0, [ABS_AZURE, CHARCOAL, ORANGE, SILVER]),
    # Reconstructed, not sampled: an H3C with the AMS-B (rack B) instead of the AMS 3H; see RACK_AMS.
    ("H3C AMS-B", "N10", "RUNNING", "Tool Changer Demo", 30, 180, 2, [IVORY, None, PETG_BLUE, GRASS, None, ORANGE], [], "amsB"),
    ("Spare P1S", "C12", "OFFLINE", "", 0, 0, None, []),
]

NOZZLE = {"PLA": 220, "PETG": 250, "ABS": 260, "PA6-CF": 290}
BED = {"PLA": 55, "PETG": 70, "ABS": 90, "PA6-CF": 100}


def run(*args):
    subprocess.run(args, check=True, capture_output=True)


def encode_ip(ip, key):
    """BBLCrossTalk::Encode_DevIp: XOR with std::mt19937 seeded by FNV-1a of the slicer UUID, as hex."""
    seed = 2166136261
    for ch in key.encode():
        seed = ((seed ^ ch) * 16777619) & 0xFFFFFFFF
    mt = [seed] + [0] * 623
    for i in range(1, 624):
        mt[i] = (1812433253 * (mt[i - 1] ^ (mt[i - 1] >> 30)) + i) & 0xFFFFFFFF
    index = 624

    def next_():
        nonlocal index
        if index >= 624:
            for i in range(624):
                y = (mt[i] & 0x80000000) | (mt[(i + 1) % 624] & 0x7FFFFFFF)
                mt[i] = mt[(i + 397) % 624] ^ (y >> 1) ^ (0x9908B0DF if y & 1 else 0)
            index = 0
        y = mt[index]
        index += 1
        y ^= y >> 11
        y ^= (y << 7) & 0x9D2C5680
        y ^= (y << 15) & 0xEFC60000
        y ^= y >> 18
        return y

    return "".join(f"{b ^ (next_() & 0xFF):02x}" for b in ip.encode())


class Printer:
    def __init__(self, index, spec, samples, client):
        name, code, state, job, progress, minutes, active, spools = spec[:8]
        self.name, self.code, self.job_minutes, self.spools = name, code, minutes, spools
        self.m = MODELS[code]
        product, prefix, self.nozzles, self.heater = self.m["product"], self.m["prefix"], self.m["nozzles"], self.m["heater"]
        self.ams_kind = None if code == "C11" else (spec[9] if len(spec) > 9 else self.m["ams"])
        # (unit id, AMS kind, spools) for every unit: four-slot ones from 0, AMS HTs from 128
        self.units = [(RACK_AMS.get(self.ams_kind, 0), self.ams_kind, spools)] if self.ams_kind else []
        for kind, unit_spools in (spec[8] if len(spec) > 8 else []):
            ids = [u[0] for u in self.units if (u[0] >= 128) == (kind == "ht")]
            self.units.append((max(ids) + 1 if ids else (128 if kind == "ht" else 0), kind, unit_spools))
        self.product = product
        self.index = index
        self.serial = f"{prefix}F{index + 1:011d}"
        self.access_code = f"FLT{index + 1:05d}"
        self.stale_code = f"OLD{index + 1:05d}"
        self.port = BASE_PORT + index
        self.offline = state == "OFFLINE"
        self.client = client
        self.lock = threading.Lock()
        self.progress = float(progress)
        self.active = active
        self.state = "IDLE" if self.offline else state
        # Each format starts from a real report of it: the A1's for the flat one, the P2S's for the newer one; the
        # H3C from its own.
        sample = samples[self.m["sample"] or ("a1" if self.m["format"] == "legacy" else "p2s")]
        self.own_sample = self.m["sample"] is not None
        self.tray_template = samples["p2s"]["pushall"]["print"]["ams"]["ams"][0]["tray"][0]
        self.version = self.make_version(sample["get_version"] if self.own_sample else samples["p2s"]["get_version"])
        base = sample["pushall"]["print"]
        self.report = self.make_report(base, job)
        self.finished_at = time.time() if state == "FINISH" else None

    # Reports

    def make_version(self, base):
        version = copy.deepcopy(base)
        # The H3C's own reply lists its AMS 3H ("n10a", no unit number) and its nine hotends as they are.
        keep = None if self.own_sample else ("ota", "mc", "th")
        modules = [m for m in version["info"]["module"] if keep is None or m["name"] in keep]
        if self.own_sample and self.ams_kind != "ams3h":
            modules = [m for m in modules if m["name"] != "n10a"]  # no AMS 3H on this one
        for m in modules:
            if m["name"] == "ota" or not self.own_sample:
                m["sn"] = self.serial
            if m["name"] == "ota":
                m["product_name"] = f"Bambu Lab {self.product}"
        for n, (unit_id, kind, _) in enumerate(self.units):
            if AMS_MODULE[kind][0] is None or self.own_sample and any(m["name"] == AMS_MODULE[kind][0] for m in modules):
                continue  # the AMS-B's module name isn't known
            prefix, title, _ = AMS_MODULE[kind]
            modules.append({"name": f"{prefix}/{unit_id}", "product_name": f"{title} ({n + 1})", "sn": f"{self.serial}-AMS{n}",
                            "hw_ver": "", "sw_ver": "01.00.00.00", "loader_ver": "00.00.00.00", "flag": 0, "visible": True})
        version["info"]["module"] = modules
        return version

    def material(self):
        spool = self.spools[self.active] if self.active is not None and self.active < len(self.spools) else None
        return (spool or {}).get("material", "PLA")

    def tray(self, template, slot, spool):
        t = copy.deepcopy(template)
        t["id"] = str(slot)
        if spool is None:
            t.update({"tray_type": "", "tray_sub_brands": "", "tray_color": "00000000", "cols": [], "tray_info_idx": "",
                      "tray_id_name": "", "tag_uid": "0000000000000000", "tray_uuid": "0" * 32, "remain": -1,
                      "tray_weight": "0", "state": 10})
            return t
        bambu = spool["kind"] == "bambu"
        hex_ = spool["hex"] + "FF"
        low, high = {"PLA": (190, 230), "PETG": (230, 270), "ABS": (240, 280), "PA6-CF": (260, 300)}[spool["material"]]
        t.update({
            "tray_type": spool["material"], "tray_color": hex_, "cols": [hex_], "tray_info_idx": spool["preset"],
            "tray_sub_brands": spool["product"] if bambu else "",
            "tray_id_name": f"{spool['preset'][2:]}-{spool['code']}" if bambu else "",
            "tag_uid": f"{random.getrandbits(48):012X}0100" if bambu else "0000000000000000",
            "tray_uuid": uuid.uuid4().hex.upper() if bambu else "0" * 32,
            "remain": spool["remain"] if bambu else -1, "tray_weight": "1000" if bambu else "0",
            "nozzle_temp_min": str(low), "nozzle_temp_max": str(high), "state": 11,
        })
        return t

    def make_report(self, base, job):
        r = copy.deepcopy(base)
        template = base["ams"]["ams"][0]["tray"][0] if "tray_type" in base["ams"]["ams"][0]["tray"][0] else self.tray_template
        material = self.material()
        printing = self.state in ("RUNNING", "PAUSE", "PREPARE")
        nozzle_target = NOZZLE.get(material, 220) if printing else 0
        bed_target = BED.get(material, 55) if printing else 0
        total_layers = max(40, self.job_minutes * 2)
        legacy = self.m["format"] == "legacy"
        r.update({
            "gcode_state": self.state, "subtask_name": job, "gcode_file": f"{job}.gcode.3mf" if job else "",
            "mc_percent": int(self.progress), "layer_num": int(total_layers * self.progress / 100) if job else 0,
            "total_layer_num": total_layers if job else 0,
            "mc_remaining_time": int(self.job_minutes * (1 - self.progress / 100)) if self.state in ("RUNNING", "PAUSE", "PREPARE") else 0,
            "stg_cur": {"RUNNING": 0, "PAUSE": 16, "PREPARE": 2, "FINISH": 255, "FAILED": 255, "IDLE": 255}[self.state],
            "print_type": "idle" if self.state == "IDLE" else "local",
            "print_error": 0x03008003 if self.state == "FAILED" else 0,  # spaghetti detected
            "hms": [{"attr": 0x07002000, "code": 0x00020001}] if self.state == "PAUSE" else [],  # A1 ran out
            "spd_lvl": 2, "wifi_signal": f"-{random.randint(45, 70)}dBm", "sdcard": True,
            "nozzle_temper": float(nozzle_target or 28), "nozzle_target_temper": float(nozzle_target),
            "bed_temper": float(bed_target or 24), "bed_target_temper": float(bed_target),
            "task_id": str(100000 + self.index), "subtask_id": str(200000 + self.index),
            "lights_report": self.lights("on" if printing else "off"),
        })
        r.update(self.legacy_fans())
        # Developer Mode on, so the app's controls work; bit 12 says whether there's a door sensor. The A1 sends no
        # `fun` at all.
        fun = 0x60029FD183FF9CB7 & ~(1 << 12) | (1 << 12 if self.m["door"] else 0)
        if self.m["fun"]:
            r["fun"] = f"{fun:016X}"
        else:
            r.pop("fun", None)
            r.pop("fun2", None)
        if self.m["door"]:
            r["stat"] = "10002080F0"  # bit 23 clear: the door is closed
        else:
            r.pop("stat", None)
        # `info.temp` is the P2S's chamber reading in the flat fields; others don't send it (the A1's `info` is
        # its network list).
        if isinstance(r.get("info"), dict):
            r.pop("info")
        if self.m["chamber"] == "sensor" and not legacy:
            r["info"] = {"temp": 34 if printing else 26}
        r.pop("chamber_temper", None)
        r.pop("ctt", None)
        chamber_target = 45 if self.heater and material == "PA6-CF" and printing else 0
        chamber_now = chamber_target or (34 if printing else 26)
        if legacy:
            if self.m["chamber"] == "sensor":
                r["chamber_temper"] = float(chamber_now)
                if self.heater:
                    r["ctt"] = chamber_target
            elif self.m["chamber"] == "placeholder":
                r["chamber_temper"] = 5  # what a P1S or A1 sends without a sensor

        snow = 0xFFFF if self.active is None or not self.ams_kind else self.active
        if self.ams_kind in RACK_AMS:
            # unit << 8 | slot; idle, the virtual slot of the mounted hotend (rack position 4) with no slot.
            snow = (RACK_AMS[self.ams_kind] << 8) | self.active if self.active is not None else 0xFBFF
        if self.ams_kind is None and self.active is not None:
            snow = (255 << 8) | 0  # the external spool
        if legacy:
            r.pop("device", None)
        else:
            device = r["device"]
            extruder = device["extruder"]["info"][0]
            current = nozzle_target or 28
            extruder.update({"id": 0, "temp": (nozzle_target << 16) | current, "snow": snow})
            device["extruder"]["info"] = [extruder]
            device["extruder"]["state"] = 1
            if self.nozzles == 2:
                # Bambu Studio keys extruders by `id` (0 right, 1 left), not list order, and `state` bits 4-7 name
                # the one printing. The X2D lists its left nozzle first and prints with it here, as a user's did.
                second = copy.deepcopy(extruder)
                idle_target = 170 if printing else 0
                second.update({"id": 1, "temp": (idle_target << 16) | (idle_target or 27), "snow": 0xFFFF})
                if self.code == "N6":
                    extruder["id"], second["id"] = 1, 0
                    device["extruder"]["info"] = [extruder, second]
                    device["extruder"]["state"] = (1 << 4) | 2
                else:
                    device["extruder"]["info"].append(second)
                    device["extruder"]["state"] = 2
            device["bed"]["info"]["temp"] = (bed_target << 16) | (bed_target or 24)
            device["bed_temp"] = device["bed"]["info"]["temp"]
            if self.m["chamber"] == "sensor":
                device["ctc"]["info"]["temp"] = (chamber_target << 16) | chamber_now
            else:
                device.pop("ctc", None)
            running = self.state == "RUNNING"
            parts = []
            for part_id, func, pct in self.m["fans"]:
                part = {"id": part_id, "func": func, "range": 6553600, "state": pct if running else 0}
                if self.own_sample:
                    part["tar_state"] = part["state"]  # the H3C also reports the target
                parts.append(part)
            device["airduct"]["parts"] = parts
            if self.ams_kind == "amsB" and isinstance(device.get("holder"), dict):
                # Rack type in `info2` bits 1-4 (DevNozzleRack.cpp): 1 rack A (the sample's), 2 rack B.
                device["holder"]["info2"] = device["holder"]["info2"] & ~(0xF << 1) | (2 << 1)

        ams = r["ams"]
        if self.ams_kind:
            units, loaded = [], 0
            for unit_id, kind, unit_spools in self.units:
                unit = copy.deepcopy(ams["ams"][0])
                unit["id"] = str(unit_id)
                unit["info"] = AMS_MODULE[kind][2]
                if kind == "amsB":
                    for key in ("zone_info", "dry_info", "humidity", "humidity_raw", "temp", "dry_time"):
                        unit.pop(key, None)
                elif kind == "ams3h":
                    # Humidity and temperature per drying zone only (h3c-sample.json), none for the whole unit.
                    for zone in unit.get("zone_info", []):
                        zone["humidity_raw"] = str(random.randint(30, 50))
                        zone["temp"] = f"{random.uniform(24, 31):.1f}"
                elif kind in ("lite", "lite_mixed"):
                    # No sensors in an AMS Lite; it sends these fixed values (a1-sample.json).
                    unit.update({"humidity": "5", "humidity_raw": "0", "temp": "0.0"})
                else:
                    unit["humidity"] = str(random.randint(1, 4))
                    unit["temp"] = f"{random.uniform(24, 31):.1f}"
                    if kind == "ams":
                        unit.pop("humidity_raw", None)  # the first AMS reports only the 1-5 level
                    else:
                        unit["humidity_raw"] = str(random.randint(18, 52))
                slots = SLOTS.get(kind, 4)
                unit["tray"] = [self.tray(template, slot, spool) if spool or kind not in RACK_AMS
                                else {"id": str(slot), "print_status": 0, "state": 160}  # an empty AMS 3H slot
                                for slot, spool in enumerate((unit_spools + [None] * slots)[:slots])]
                units.append(unit)
                for slot, spool in enumerate(unit_spools[:slots]):
                    if spool:
                        loaded |= 1 << tray_bit(unit_id, kind, slot)
            ams["ams"] = units
            present = sum(1 << unit_bit(unit_id, kind) for unit_id, kind, _ in self.units)
            # How the H3C reports tray_now for its AMS 3H isn't sampled (it was idle): it keeps the sample's, and
            # the app reads the slot from the extruder's `snow` as Bambu Studio does.
            tray_now = ams.get("tray_now", "255") if self.ams_kind in RACK_AMS else str(self.active if self.active is not None else 255)
            ams.update({"ams_exist_bits": f"{present:x}", "tray_exist_bits": f"{loaded:x}", "tray_is_bbl_bits": f"{loaded:x}",
                        "tray_read_done_bits": f"{loaded:x}", "tray_now": tray_now})
            external = None
        else:
            ams.update({"ams": [], "ams_exist_bits": "0", "tray_exist_bits": "0", "tray_now": "254" if self.active is not None else "255"})
            external = self.spools[0] if self.spools else None
        if self.own_sample:
            return r  # the H3C's nine virtual slots (247-255), one per rack position, as sampled
        r.pop("vt_tray", None)
        r.pop("vir_slot", None)
        if legacy:
            slot = self.tray(template, 254, external)
            slot["id"] = "254"
            r["vt_tray"] = slot
        else:
            slot = self.tray(template, 255, external)
            slot["id"] = "255"
            r["vir_slot"] = [slot]
        return r

    def lights(self, chamber_mode):
        nodes = {"chamber_light": chamber_mode, "work_light": "flashing"}
        return [{"node": n, "mode": nodes[n]} for n in self.m["lights"]]

    def legacy_fans(self, running=None):
        """The flat 0-15 fan fields every model sends, "0" for fans it doesn't have."""
        running = self.state == "RUNNING" if running is None else running
        return {"cooling_fan_speed": "13" if running else "0",
                "big_fan1_speed": "5" if running and self.m["aux"] else "0",
                "big_fan2_speed": "6" if running and self.m["chamber_fan"] else "0",
                "heatbreak_fan_speed": "15" if self.state in ("RUNNING", "PAUSE", "PREPARE") else "0"}

    # MQTT

    def publish(self, payload):
        self.client.publish(f"device/{self.serial}/report", json.dumps(payload))

    def push_full(self):
        with self.lock:
            report = copy.deepcopy(self.report)
        report.update({"command": "push_status", "msg": 0, "sequence_id": "0"})
        self.publish({"print": report})

    def delta(self, fields):
        with self.lock:
            merge(self.report, fields)
        self.publish({"print": {"command": "push_status", "msg": 1, **fields}})

    def handle(self, data):
        if data.get("pushing", {}).get("command") == "pushall":
            self.push_full()
        elif data.get("info", {}).get("command") == "get_version":
            self.publish(self.version)
        elif "print" in data:
            cmd = data["print"].get("command")
            seq = data["print"].get("sequence_id", "0")
            state = self.report["gcode_state"]
            if cmd == "pause" and state in ("RUNNING", "PREPARE"):
                self.set_state("PAUSE", stage=16)
            elif cmd == "resume" and state == "PAUSE":
                self.set_state("RUNNING", stage=0, hms=[])
            elif cmd == "stop" and state in ("RUNNING", "PAUSE", "PREPARE"):
                self.set_state("FAILED", stage=255, print_error=50348044)  # canceled
            elif cmd == "print_speed":
                self.delta({"spd_lvl": int(data["print"].get("param", "2"))})
            self.publish({"print": {"command": cmd, "sequence_id": seq, "result": "success", "reason": ""}})
        elif data.get("system", {}).get("command") == "ledctrl":
            mode = data["system"].get("led_mode", "on")
            self.delta({"lights_report": self.lights(mode)})
            self.publish({"system": {**data["system"], "result": "success"}})

    def set_state(self, state, stage, **extra):
        self.state = state
        fields = {"gcode_state": state, "stg_cur": stage}
        fields.update(self.legacy_fans())
        fields.update(extra)
        self.delta(fields)

    def nozzle(self, target, current):
        """The printing nozzle's temperature in this model's format."""
        if self.m["format"] == "legacy":
            return {"nozzle_temper": float(current), "nozzle_target_temper": float(target)}
        info = self.report["device"]["extruder"]["info"]
        return {"nozzle_temper": float(current), "nozzle_target_temper": float(target),
                "device": {"extruder": {"info": [{"id": info[0]["id"], "temp": (target << 16) | current}]}}}

    def tick(self):
        """One second."""
        r = self.report
        state = r["gcode_state"]
        target = int(r["nozzle_target_temper"])
        if state == "PREPARE":
            current = min(target, int(r["nozzle_temper"]) + 4)
            fields = self.nozzle(target, current)
            if current >= target:
                self.state = "RUNNING"
                fields.update({"gcode_state": "RUNNING", "stg_cur": 0, "mc_percent": 1})
                fields.update(self.legacy_fans())
            self.delta(fields)
        elif state == "RUNNING":
            self.progress = min(100.0, self.progress + 100.0 / max(1, self.job_minutes * 60) * 6 * SPEED)
            pct = int(self.progress)
            total = r.get("total_layer_num") or 1
            fields = {"mc_percent": pct, "layer_num": int(total * self.progress / 100),
                      "mc_remaining_time": max(0, int(self.job_minutes * (1 - self.progress / 100)))}
            fields.update(self.nozzle(target, target + random.choice([-1, 0, 0, 1])))
            if pct >= 100:
                self.state = "FINISH"
                self.finished_at = time.time()
                fields.update({"gcode_state": "FINISH", "stg_cur": 255, "mc_remaining_time": 0})
                fields.update(self.legacy_fans())
            self.delta(fields)
        elif state == "FINISH" and self.finished_at and time.time() - self.finished_at > 90 and self.job_minutes:
            # Someone took the print off and started it again, so the fleet keeps moving.
            self.progress = 0
            self.finished_at = None
            self.state = "RUNNING"
            fields = {"gcode_state": "RUNNING", "stg_cur": 0, "mc_percent": 0, "layer_num": 0, "mc_remaining_time": self.job_minutes}
            fields.update(self.legacy_fans())
            self.delta(fields)
        elif random.random() < 0.2:
            self.delta({"wifi_signal": f"-{random.randint(45, 70)}dBm"})

    def ssdp(self, count=0):
        """The announcement; with SERIAL_CASE its serial is lowercase (lower) or every other one is (mixed)."""
        lower = SERIAL_CASE == "lower" or (SERIAL_CASE == "mixed" and count % 2 == 1)
        usn = self.serial.lower() if lower else self.serial
        return ("NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nServer: UPnP/1.0\r\n"
                f"Location: {PUBLISHED_HOST}:{self.port}\r\nNT: urn:bambulab-com:device:3dprinter:1\r\n"
                f"USN: {usn}\r\nCache-Control: max-age=1800\r\nDevModel.bambu.com: {self.code}\r\n"
                f"DevName.bambu.com: {self.name}\r\nDevConnect.bambu.com: lan\r\nDevBind.bambu.com: free\r\n"
                "Devseclink.bambu.com: secure\r\nDevVersion.bambu.com: 01.03.00.00\r\nDevCap.bambu.com: 1\r\n\r\n")


def merge(base, delta):
    """Applies a delta the way clients do: nested objects merge, arrays of objects merge by id."""
    for k, v in delta.items():
        if isinstance(v, dict) and isinstance(base.get(k), dict):
            merge(base[k], v)
        elif isinstance(v, list) and isinstance(base.get(k), list) and v and all(isinstance(i, dict) and "id" in i for i in v):
            for item in v:
                match = next((b for b in base[k] if isinstance(b, dict) and b.get("id") == item["id"]), None)
                if match is None:
                    base[k].append(item)
                else:
                    merge(match, item)
        else:
            base[k] = v


def make_certs(printers):
    os.makedirs(CERTS, exist_ok=True)
    run("openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "3650", "-subj", "/CN=Fleet CA",
        "-keyout", f"{CERTS}/ca.key", "-out", f"{CERTS}/ca.pem")
    for p in printers:
        base = f"{CERTS}/{p.serial}"
        run("openssl", "req", "-newkey", "rsa:2048", "-nodes", "-subj", f"/CN={p.serial}", "-keyout", f"{base}.key", "-out", f"{base}.csr")
        run("openssl", "x509", "-req", "-in", f"{base}.csr", "-CA", f"{CERTS}/ca.pem", "-CAkey", f"{CERTS}/ca.key",
            "-CAcreateserial", "-days", "3650", "-out", f"{base}.pem")


def start_broker(printers):
    lines = ["per_listener_settings true", "listener 1883 127.0.0.1", "allow_anonymous true"]
    for p in printers:
        if p.offline:
            continue
        passwd = f"{CERTS}/{p.serial}.passwd"
        with open(passwd, "w") as f:
            f.write(f"bblp:{p.access_code}\n")
        run("mosquitto_passwd", "-U", passwd)
        lines += [f"listener {p.port} 0.0.0.0", f"cafile {CERTS}/ca.pem", f"certfile {CERTS}/{p.serial}.pem",
                  f"keyfile {CERTS}/{p.serial}.key", "tls_version tlsv1.2", "allow_anonymous false", f"password_file {passwd}"]
    with open(f"{CERTS}/mosquitto.conf", "w") as f:
        f.write("\n".join(lines) + "\n")
    # Mosquitto drops to its own user; these throwaway keys only need to be readable.
    for name in os.listdir(CERTS):
        os.chmod(os.path.join(CERTS, name), 0o644)
    log = open(f"{CERTS}/mosquitto.log", "w")
    return subprocess.Popen(["mosquitto", "-c", f"{CERTS}/mosquitto.conf"], stdout=log, stderr=subprocess.STDOUT)


def write_settings(printers):
    """OUT/BambuStudio.conf as Bambu Studio saves LAN printers."""
    key = str(uuid.uuid4())
    typed = (lambda serial: serial) if SERIAL_CASE == "upper" else str.lower
    conf = {
        "app": {"slicer_uuid": key},
        "access_code": {},
        "user_access_code": {typed(p.serial): p.access_code for p in printers},
        "user_access_dev_ip": {typed(p.serial): encode_ip(f"{PUBLISHED_HOST}:{p.port}", key) for p in printers},
    }
    if SERIAL_CASE == "mixed":
        # The cloud's copy, out of date, under the serial's other spelling: tried first, refused, then the
        # working code must still be there to fall back on.
        conf["access_code"] = {p.serial: p.stale_code for p in printers}
        conf["ip_address"] = {p.serial: f"{PUBLISHED_HOST}:{p.port}" for p in printers}
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, "BambuStudio.conf")
    with open(path, "w") as f:
        json.dump(conf, f, indent=4)
        f.write("\n# MD5 checksum 00000000000000000000000000000000\n")
    return path


def load_samples():
    # Next to fleet.py in the container, in ../emulator in the repository.
    folder = HERE if os.path.exists(os.path.join(HERE, "p2s-sample.json")) else os.path.join(HERE, "..", "emulator")
    return {name: json.load(open(os.path.join(folder, f"{name}-sample.json"))) for name in ("p2s", "a1", "h3c")}


def dump(folder):
    """`fleet.py --dump <dir>`: each model's full report, mid-print, as <code>.json, without starting anything.
    Tests/BambuBarTests/ModelReports holds them; ModelReportTests runs them through the app's parser."""
    os.makedirs(folder, exist_ok=True)
    samples = load_samples()
    seen = set()
    for i, spec in enumerate(PRINTERS):
        name, code = spec[0], spec[1]
        # One per model, and one per other AMS a model comes with (N10-amsB.json).
        key = f"{code}-{spec[9]}" if len(spec) > 9 else code
        if key in seen:
            continue
        seen.add(key)
        printer = Printer(i, spec[:2] + ("RUNNING", "Fixture", 50, 120) + spec[6:], samples, None)
        report = copy.deepcopy(printer.report)
        report.update({"command": "push_status", "msg": 0, "sequence_id": "0"})
        with open(os.path.join(folder, f"{key}.json"), "w") as f:
            json.dump({"model": code, "serial": printer.serial, "print": report}, f, indent=1, sort_keys=True)
            f.write("\n")
    print(f"{len(seen)} model reports in {folder}")


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--dump":
        return dump(sys.argv[2])
    samples = load_samples()
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="fleet")
    printers = [Printer(i, spec, samples, client) for i, spec in enumerate(PRINTERS)]
    by_serial = {p.serial: p for p in printers}
    make_certs(printers)
    broker = start_broker(printers)
    time.sleep(0.8)
    if broker.poll() is not None:
        print(open(f"{CERTS}/mosquitto.log").read(), flush=True)
        raise SystemExit("mosquitto didn't start")

    def on_message(_client, _userdata, message):
        serial = message.topic.split("/")[1]
        try:
            data = json.loads(message.payload)
        except ValueError:
            return
        if serial in by_serial and not by_serial[serial].offline:
            by_serial[serial].handle(data)

    client.on_message = on_message
    client.on_connect = lambda c, *_: c.subscribe("device/+/request")
    client.connect("127.0.0.1", 1883)
    client.loop_start()
    path = write_settings(printers)

    print(f"Mock fleet: {len(printers)} printers, settings in {path}", flush=True)
    for p in printers:
        print(f"  {p.name:<17} {p.product:<10} {p.serial}  {PUBLISHED_HOST}:{p.port}  code {p.access_code}"
              f"  {'offline' if p.offline else p.state.lower()}", flush=True)

    ssdp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM) if SSDP_TARGET else None
    tick = 0
    while broker.poll() is None:
        time.sleep(1)
        tick += 1
        for p in printers:
            if not p.offline:
                p.tick()
            if ssdp and tick % 2 == 1 and not p.offline:
                try:
                    ssdp.sendto(p.ssdp(tick // 2).encode(), (SSDP_TARGET, SSDP_PORT))
                except OSError:
                    pass


if __name__ == "__main__":
    main()
