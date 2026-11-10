#!/usr/bin/env python3
"""A pretend Bambu Lab P2S for developing Bambu Bar without a printer.

Runs a TLS MQTT broker (mosquitto) on 127.0.0.1:8883 that accepts user `bblp` with access code
`12345678`, answers `pushall` with a real P2S report (p2s-sample.json, from ha-bambulab) and then
streams deltas like the printer does: progress, layers, temperatures, fans. Pause, resume, stop,
speed and the chamber light work.

    scripts/emulator/printer.py            # then:
    "build/App.noindex/Bambu Bar.app/Contents/MacOS/Bambu Bar" --connect 127.0.0.1 22E00EMULATOR0 12345678 --insecure-tls

Needs mosquitto (brew install mosquitto) and openssl."""
import copy, json, os, random, signal, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
SERIAL = "22E00EMULATOR0"
CODE = "12345678"
PORT = int(os.environ.get("PORT", "8883"))
CERTS = os.path.join(HERE, "certs")
MOSQUITTO = next((p for p in ["/opt/homebrew/opt/mosquitto/sbin/mosquitto", "/usr/local/opt/mosquitto/sbin/mosquitto", "/usr/sbin/mosquitto"] if os.path.exists(p)), "mosquitto")
SPEED = float(os.environ.get("SPEED", "1"))  # >1 prints faster


def run(*args, **kw):
    return subprocess.run(args, check=True, capture_output=True, **kw)


def make_certs():
    os.makedirs(CERTS, exist_ok=True)
    if os.path.exists(os.path.join(CERTS, "server.pem")):
        return
    run("openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "3650", "-subj", "/CN=Emulator CA",
        "-keyout", os.path.join(CERTS, "ca.key"), "-out", os.path.join(CERTS, "ca.pem"))
    run("openssl", "req", "-newkey", "rsa:2048", "-nodes", "-subj", f"/CN={SERIAL}",
        "-keyout", os.path.join(CERTS, "server.key"), "-out", os.path.join(CERTS, "server.csr"))
    run("openssl", "x509", "-req", "-in", os.path.join(CERTS, "server.csr"), "-CA", os.path.join(CERTS, "ca.pem"),
        "-CAkey", os.path.join(CERTS, "ca.key"), "-CAcreateserial", "-days", "3650", "-out", os.path.join(CERTS, "server.pem"))


def start_broker():
    passwd = os.path.join(CERTS, "passwd")
    with open(passwd, "w") as f:
        f.write(f"bblp:{CODE}\n")
    run("/opt/homebrew/bin/mosquitto_passwd" if os.path.exists("/opt/homebrew/bin/mosquitto_passwd") else "mosquitto_passwd", "-U", passwd)
    conf = os.path.join(CERTS, "mosquitto.conf")
    with open(conf, "w") as f:
        f.write(f"""per_listener_settings true
listener {PORT} 127.0.0.1
cafile {CERTS}/ca.pem
certfile {CERTS}/server.pem
keyfile {CERTS}/server.key
tls_version tlsv1.2
allow_anonymous false
password_file {passwd}
listener 18830 127.0.0.1
allow_anonymous true
""")
    return subprocess.Popen([MOSQUITTO, "-c", conf], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


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


class Printer:
    def __init__(self):
        sample = json.load(open(os.path.join(HERE, "p2s-sample.json")))
        self.report = sample["pushall"]["print"]
        self.version = sample["get_version"]
        self.report.update({"gcode_state": "PREPARE", "stg_cur": 7, "mc_percent": 0, "layer_num": 0, "mc_remaining_time": 95,
                            "subtask_name": "Planetary Gearbox", "fun": "60029FD183FF9CB7"})  # Developer Mode on
        self.report["device"]["extruder"]["info"][0]["temp"] = (220 << 16) | 150
        self.report["device"]["bed"]["info"]["temp"] = (55 << 16) | 52
        self.progress = 0.0
        self.lock = threading.Lock()

    def publish(self, payload):
        subprocess.run(["mosquitto_pub", "-h", "127.0.0.1", "-p", "18830", "-t", f"device/{SERIAL}/report", "-m", json.dumps(payload)])

    def push_full(self):
        with self.lock:
            report = copy.deepcopy(self.report)
        report.update({"command": "push_status", "msg": 0})
        self.publish({"print": report})

    def delta(self, fields):
        with self.lock:
            merge(self.report, fields)
        self.publish({"print": {"command": "push_status", "msg": 1, **fields}})

    def handle(self, message):
        try:
            data = json.loads(message)
        except ValueError:
            return
        if data.get("pushing", {}).get("command") == "pushall":
            self.push_full()
        elif data.get("info", {}).get("command") == "get_version":
            self.publish(self.version)
        elif "print" in data:
            cmd = data["print"].get("command")
            seq = data["print"].get("sequence_id", "0")
            state = self.report["gcode_state"]
            if cmd == "pause" and state in ("RUNNING", "PREPARE"):
                self.delta({"gcode_state": "PAUSE", "stg_cur": 16})
            elif cmd == "resume" and state == "PAUSE":
                self.delta({"gcode_state": "RUNNING", "stg_cur": 0})
            elif cmd == "stop":
                self.delta({"gcode_state": "FAILED", "stg_cur": 255, "print_error": 50348044})
            elif cmd == "print_speed":
                self.delta({"spd_lvl": int(data["print"].get("param", "2"))})
            self.publish({"print": {"command": cmd, "sequence_id": seq, "result": "success", "reason": ""}})
        elif data.get("system", {}).get("command") == "ledctrl":
            mode = data["system"].get("led_mode", "on")
            self.delta({"lights_report": [{"node": "chamber_light", "mode": mode}, {"node": "work_light", "mode": "flashing"}]})
            self.publish({"system": {**data["system"], "result": "success"}})

    def tick(self):
        """One second of printing."""
        r = self.report
        state = r["gcode_state"]
        nozzle = r["device"]["extruder"]["info"][0]["temp"]
        cur, tgt = nozzle & 0xFFFF, nozzle >> 16
        if state == "PREPARE":
            cur = min(tgt, cur + 6)
            bed = r["device"]["bed"]["info"]["temp"]
            bed_cur, bed_tgt = bed & 0xFFFF, bed >> 16
            bed_cur = min(bed_tgt, bed_cur + 1)
            fields = {"device": {"extruder": {"info": [{"id": 0, "temp": (tgt << 16) | cur}]},
                                 "bed": {"info": {"temp": (bed_tgt << 16) | bed_cur}}},
                      "nozzle_temper": float(cur), "bed_temper": float(bed_cur)}
            if cur >= tgt and bed_cur >= bed_tgt:
                fields.update({"gcode_state": "RUNNING", "stg_cur": 0, "mc_percent": 1})
            self.delta(fields)
        elif state == "RUNNING":
            self.progress = min(100.0, self.progress + 0.25 * SPEED)
            pct = int(self.progress)
            total = r.get("total_layer_num", 305)
            jitter = (tgt << 16) | (tgt + random.choice([-1, 0, 0, 1]))
            fields = {"mc_percent": pct, "layer_num": int(total * self.progress / 100),
                      "mc_remaining_time": max(0, int(95 * (1 - self.progress / 100))),
                      "device": {"extruder": {"info": [{"id": 0, "temp": jitter}]},
                                 "airduct": {"parts": [{"id": 16, "state": random.choice([80, 90, 100])}]}}}
            if pct >= 100:
                fields.update({"gcode_state": "FINISH", "stg_cur": 255, "mc_remaining_time": 0})
            self.delta(fields)
        else:
            self.delta({"wifi_signal": f"-{random.randint(48, 56)}dBm"})


def main():
    make_certs()
    broker = start_broker()
    time.sleep(0.6)
    printer = Printer()
    sub = subprocess.Popen(["mosquitto_sub", "-h", "127.0.0.1", "-p", "18830", "-t", f"device/{SERIAL}/request"],
                           stdout=subprocess.PIPE, text=True)

    def listen():
        for line in sub.stdout:
            printer.handle(line.strip())

    threading.Thread(target=listen, daemon=True).start()

    def stop(*_):
        sub.terminate()
        broker.terminate()
        sys.exit(0)

    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)
    print(f"Emulated P2S {SERIAL} on 127.0.0.1:{PORT}, access code {CODE}", flush=True)
    while True:
        time.sleep(1)
        printer.tick()


if __name__ == "__main__":
    main()
