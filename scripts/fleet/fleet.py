#!/usr/bin/env python3
"""A mock fleet of Bambu Lab printers for developing Bambu Bar with many printers at once.

Each printer gets its own TLS MQTT listener (user `bblp`, its own access code) on consecutive ports from
BASE_PORT, answers `pushall` and `get_version` like the real thing and streams deltas while it prints:
progress, layers, temperatures, fans. Pause, resume, stop, speed and the chamber light work. Reports start
from the real P2S report in ../emulator/p2s-sample.json, reshaped per model (AMS kind, second nozzle,
heated chamber, no AMS on the P1P). One printer is listed but never answers, like one that's switched off.

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
import copy, json, os, random, socket, subprocess, threading, time, uuid

import paho.mqtt.client as mqtt

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

# Model code → (product name, serial prefix, AMS kind or None, nozzles, chamber heater max or None)
MODELS = {
    "N7": ("P2S", "22E", "pro2", 1, None),
    "C12": ("P1S", "01P", "ams", 1, None),
    "C11": ("P1P", "01S", None, 1, None),
    "BL-P001": ("X1 Carbon", "00M", "ams", 1, None),
    "C13": ("X1E", "03W", "ams", 1, 60),
    "O1D": ("H2D", "094", "pro2", 2, 65),
    "O1S": ("H2S", "093", "pro2", 1, 65),
    "O1C": ("H2C", "31B", "pro2", 2, 65),
    "N2S": ("A1", "039", "lite", 1, None),
    "N1": ("A1 mini", "030", "lite", 1, None),
    "N6": ("X2D", "20P", "pro2", 2, 65),
}
AMS_MODULE = {"pro2": ("n3f", "AMS 2 Pro", "10001003"), "ams": ("ams", "AMS", "10001001"), "lite": ("ams_f1", "AMS Lite", "10001002"),
              "ht": ("n3s", "AMS HT", "10001004")}

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
    def __init__(self, index, spec, sample, client):
        name, code, state, job, progress, minutes, active, spools = spec[:8]
        self.name, self.code, self.job_minutes, self.spools = name, code, minutes, spools
        product, prefix, self.ams_kind, self.nozzles, self.heater = MODELS[code]
        # (unit id, AMS kind, spools) for every unit: four-slot ones from 0, AMS HTs from 128
        self.units = [(0, self.ams_kind, spools)] if self.ams_kind else []
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
        self.version = self.make_version(sample["get_version"])
        self.report = self.make_report(sample["pushall"]["print"], job)
        self.finished_at = time.time() if state == "FINISH" else None

    # Reports

    def make_version(self, base):
        version = copy.deepcopy(base)
        modules = [m for m in version["info"]["module"] if m["name"] in ("ota", "mc", "th")]
        for m in modules:
            m["sn"] = self.serial
            if m["name"] == "ota":
                m["product_name"] = f"Bambu Lab {self.product}"
        for n, (unit_id, kind, _) in enumerate(self.units):
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
        template = base["ams"]["ams"][0]["tray"][0]
        material = self.material()
        printing = self.state in ("RUNNING", "PAUSE", "PREPARE")
        nozzle_target = NOZZLE.get(material, 220) if printing else 0
        bed_target = BED.get(material, 55) if printing else 0
        total_layers = max(40, self.job_minutes * 2)
        r.update({
            "gcode_state": self.state, "subtask_name": job, "gcode_file": f"{job}.gcode.3mf" if job else "",
            "mc_percent": int(self.progress), "layer_num": int(total_layers * self.progress / 100) if job else 0,
            "total_layer_num": total_layers if job else 0,
            "mc_remaining_time": int(self.job_minutes * (1 - self.progress / 100)) if self.state in ("RUNNING", "PAUSE", "PREPARE") else 0,
            "stg_cur": {"RUNNING": 0, "PAUSE": 16, "PREPARE": 2, "FINISH": 255, "FAILED": 255, "IDLE": 255}[self.state],
            "print_type": "idle" if self.state == "IDLE" else "local",
            "print_error": 0x03008003 if self.state == "FAILED" else 0,  # spaghetti detected
            "hms": [{"attr": 0x07002000, "code": 0x00020001}] if self.state == "PAUSE" else [],  # A1 ran out
            "fun": "60029FD183FF9CB7",  # Developer Mode on, so the app's controls work
            "spd_lvl": 2, "wifi_signal": f"-{random.randint(45, 70)}dBm", "sdcard": True,
            "nozzle_temper": float(nozzle_target or 28), "nozzle_target_temper": float(nozzle_target),
            "bed_temper": float(bed_target or 24), "bed_target_temper": float(bed_target),
            "task_id": str(100000 + self.index), "subtask_id": str(200000 + self.index),
            "lights_report": [{"node": "chamber_light", "mode": "on" if printing else "off"}, {"node": "work_light", "mode": "flashing"}],
        })
        device = r["device"]
        extruder = device["extruder"]["info"][0]
        snow = 0xFFFF if self.active is None or not self.ams_kind else self.active
        if self.ams_kind is None and self.active is not None:
            snow = (255 << 8) | 0  # the external spool
        current = nozzle_target or 28
        extruder.update({"id": 0, "temp": (nozzle_target << 16) | current, "snow": snow})
        device["extruder"]["info"] = [extruder]
        device["extruder"]["state"] = 1
        if self.nozzles == 2:
            # Bambu Studio keys extruders by `id` (0 right, 1 left), not list order, and `state` bits 4-7 name the
            # one printing. The X2D lists its left nozzle first and prints with it here, as a user's X2D did.
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
        chamber_target = 45 if self.heater and material == "PA6-CF" and printing else 0
        device["ctc"]["info"]["temp"] = (chamber_target << 16) | (chamber_target or (34 if printing else 26))
        device["airduct"]["parts"] = [{"id": 16, "func": 0, "range": 6553600, "state": 90 if self.state == "RUNNING" else 0},
                                      {"id": 32, "func": 6, "range": 6553600, "state": 30 if self.state == "RUNNING" else 0},
                                      {"id": 48, "func": 1, "range": 6553600, "state": 40 if self.state == "RUNNING" else 0}]
        ams = r["ams"]
        if self.ams_kind:
            units, loaded = [], 0
            for unit_id, kind, unit_spools in self.units:
                unit = copy.deepcopy(ams["ams"][0])
                unit["id"] = str(unit_id)
                unit["info"] = AMS_MODULE[kind][2]
                unit["humidity_raw"] = str(random.randint(18, 52))
                unit["humidity"] = "1" if kind == "lite" else str(random.randint(1, 4))
                unit["temp"] = f"{random.uniform(24, 31):.1f}"
                slots = 1 if kind == "ht" else 4
                unit["tray"] = [self.tray(template, slot, spool) for slot, spool in enumerate((unit_spools + [None] * slots)[:slots])]
                units.append(unit)
                for slot, spool in enumerate(unit_spools[:slots]):
                    if spool:
                        loaded |= 1 << (16 + unit_id - 128 if unit_id >= 128 else unit_id * 4 + slot)
            ams["ams"] = units
            ams.update({"ams_exist_bits": f"{(1 << len(units)) - 1:x}", "tray_exist_bits": f"{loaded:x}", "tray_is_bbl_bits": f"{loaded:x}",
                        "tray_read_done_bits": f"{loaded:x}", "tray_now": str(self.active if self.active is not None else 255)})
            external = None
        else:
            ams.update({"ams": [], "ams_exist_bits": "0", "tray_exist_bits": "0", "tray_now": "254" if self.active is not None else "255"})
            external = self.spools[0] if self.spools else None
        slot = self.tray(template, 255, external)
        slot["id"] = "255"
        r["vir_slot"] = [slot]
        r.pop("vt_tray", None)
        return r

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
            self.delta({"lights_report": [{"node": "chamber_light", "mode": mode}, {"node": "work_light", "mode": "flashing"}]})
            self.publish({"system": {**data["system"], "result": "success"}})

    def set_state(self, state, stage, **extra):
        self.state = state
        fields = {"gcode_state": state, "stg_cur": stage}
        fields.update(extra)
        self.delta(fields)

    def tick(self):
        """One second."""
        r = self.report
        state = r["gcode_state"]
        info = r["device"]["extruder"]["info"]
        target = info[0]["temp"] >> 16
        if state == "PREPARE":
            current = min(target, (info[0]["temp"] & 0xFFFF) + 4)
            fields = {"device": {"extruder": {"info": [{"id": info[0]["id"], "temp": (target << 16) | current}]}}}
            if current >= target:
                self.state = "RUNNING"
                fields.update({"gcode_state": "RUNNING", "stg_cur": 0, "mc_percent": 1})
            self.delta(fields)
        elif state == "RUNNING":
            self.progress = min(100.0, self.progress + 100.0 / max(1, self.job_minutes * 60) * 6 * SPEED)
            pct = int(self.progress)
            total = r.get("total_layer_num") or 1
            fields = {"mc_percent": pct, "layer_num": int(total * self.progress / 100),
                      "mc_remaining_time": max(0, int(self.job_minutes * (1 - self.progress / 100))),
                      "device": {"extruder": {"info": [{"id": info[0]["id"], "temp": (target << 16) | (target + random.choice([-1, 0, 0, 1]))}]}}}
            if pct >= 100:
                self.state = "FINISH"
                self.finished_at = time.time()
                fields.update({"gcode_state": "FINISH", "stg_cur": 255, "mc_remaining_time": 0})
            self.delta(fields)
        elif state == "FINISH" and self.finished_at and time.time() - self.finished_at > 90 and self.job_minutes:
            # Someone took the print off and started it again, so the fleet keeps moving.
            self.progress = 0
            self.finished_at = None
            self.state = "RUNNING"
            self.delta({"gcode_state": "RUNNING", "stg_cur": 0, "mc_percent": 0, "layer_num": 0,
                        "mc_remaining_time": self.job_minutes})
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


def main():
    sample = json.load(open(os.path.join(HERE, "p2s-sample.json")))
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="fleet")
    printers = [Printer(i, spec, sample, client) for i, spec in enumerate(PRINTERS)]
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
