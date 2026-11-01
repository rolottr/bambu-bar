#!/usr/bin/env python3
"""A mock Bambu Lab printer camera, for Bambu Bar's integration tests (scripts/integration-test.sh).

Serves both of the printers' LAN camera protocols over TLS, with a self-signed certificate named after
CAMERA_SERIAL (Bambu Bar accepts it only with --insecure-tls, or `insecure` in a test):
  322   RTSPS (X1, H2, P2S, X2D): DESCRIBE → SETUP → PLAY with Basic or Digest auth (user `bblp`), then RTP
        interleaved on the same connection, H.264 or H.265.
  6000  the JPEG stream (P1, A1): an 80-byte login, then JPEG frames behind 16-byte headers.

What the camera does is the current scenario, set over plain HTTP on CONTROL_PORT (8080 in the container):
  PUT /scenario/<name>   choose it (for the next connections)
  GET /events            what the camera received, as JSON lines: requests, logins, teardowns
  DELETE /events         forget them

RTSP scenarios (a good one unless it says otherwise):
  h264-basic        Basic auth, H.264: a keyframe (fragmented, FU-A) then delta frames, a keyframe every 10.
  h264-digest       Digest auth with qop=auth, the same stream.
  h265              Digest auth without qop, H.265.
  trickle           h264-digest, every write a few bytes long (responses and packets split anywhere).
  negative-length   the DESCRIBE response says `Content-Length: -1` (crashed Bambu Bar, PR #3 review P1).
  huge-length       the DESCRIBE response says `Content-Length: 9223372036854775807`.
  vp8               the SDP offers VP8 only (was taken for H.264, review P3).
  split-fragment    a keyframe, then a unit whose first fragment and last fragment carry different RTP
                    timestamps, then a delta frame; nothing more (glued into a bogus keyframe, review P2).
  not-streaming     DESCRIBE answers 503, as with LAN Only Liveview off.
  stall             accepts the connection and never answers.
JPEG scenarios:
  jpeg              a frame every 200 ms.
  jpeg-trickle      the same, written 13 bytes at a time.
  jpeg-zero         a frame header that announces 0 bytes.
On the JPEG port an RTSP scenario means `jpeg`. A wrong access code: RTSP answers 401, the JPEG port hangs up
without a word, as the printers do."""
import base64, collections, hashlib, http.server, json, os, re, secrets, socket, ssl, struct, subprocess, threading, time

ACCESS_CODE = os.environ.get("ACCESS_CODE", "12345678")
CAMERA_SERIAL = os.environ.get("CAMERA_SERIAL", "01P00C000000001")
RTSP_PORT = int(os.environ.get("RTSP_PORT", "322"))
JPEG_PORT = int(os.environ.get("JPEG_PORT", "6000"))
CONTROL_PORT = int(os.environ.get("CONTROL_PORT", "8080"))
CERTS = "/tmp/camera-certs"
REALM = "Bambu Camera Simulator"

# NAL units of a 32×32 H.264 and a 64×64 H.265 stream (ffmpeg, SEI removed), as in Tests/BambuBarTests.
H264 = {"sps": "6742c00ad9096c044000000300400000030103c48992", "pps": "68cb83cb20",
        "idr": "658884053c98a0002053275d78", "p": "419a38097960"}
H265 = {"vps": "40010c01ffff01600000030090000003000003001e928090",
        "sps": "42010101600000030090000003000003001ea0208105964a924caf0168080000030008000003001040",
        "pps": "4401c172b42240", "idr": "2801af78ebaf3affac95f2d3c0", "p": "0201d0097e10c6f0f57c"}
# A 64×48 JPEG (ffmpeg).
JPEG = base64.b64decode(
    "/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAyAP/bAEMACAQEBAQEBQUFBQUFBgYGBgYGBgYGBgYGBgcHBwgICAcHBwYGBwcICAgI"
    "CQkJCAgICAkJCgoKDAwLCw4ODhERFP/EAEwAAQEAAAAAAAAAAAAAAAAAAAAGAQEBAAAAAAAAAAAAAAAAAAAABhABAAAAAAAAAAAAAAAAAAAAABEB"
    "AAAAAAAAAAAAAAAAAAAAAP/AABEIADAAQAMBIgACEQADEQD/2gAMAwEAAhEDEQA/AIsBKIkAAAAAAAAAAAAAAAAAAAAAB//Z")

state = {"scenario": "h264-digest"}
events = collections.deque(maxlen=1000)
lock = threading.Lock()


def log(**values):
    values["time"] = round(time.time(), 3)
    with lock:
        events.append(values)
    print(json.dumps(values, sort_keys=True), flush=True)


def md5(text):
    return hashlib.md5(text.encode()).hexdigest()


# MARK: - RTSP

class RTSPSession:
    def __init__(self, conn, scenario):
        self.conn = conn
        self.scenario = scenario
        self.codec = "h265" if scenario == "h265" else "h264"
        self.digest = scenario not in ("h264-basic",)
        self.qop = scenario in ("h264-digest", "trickle", "negative-length", "huge-length", "vp8", "split-fragment")
        self.nonce = secrets.token_hex(8)
        self.sequence = secrets.randbelow(65536)
        self.timestamp = 0
        self.frame = 0
        self.playing = False
        self.script_done = False

    def send(self, data):
        if self.scenario == "trickle":
            for i in range(0, len(data), 3):
                self.conn.sendall(data[i:i + 3])
        else:
            self.conn.sendall(data)

    def respond(self, cseq, status="200 OK", headers=(), body=b"", length=None):
        lines = [f"RTSP/1.0 {status}", f"CSeq: {cseq}", "Server: Bambu Camera Simulator"] + list(headers)
        lines.append(f"Content-Length: {len(body) if length is None else length}")
        self.send(("\r\n".join(lines) + "\r\n\r\n").encode() + body)

    def authorized(self, method, fields):
        header = fields.get("authorization", "")
        if not self.digest:
            return header == "Basic " + base64.b64encode(f"bblp:{ACCESS_CODE}".encode()).decode()
        if not header.lower().startswith("digest "):
            return False
        p = {k: a or b for k, a, b in re.findall(r'(\w+)=(?:"([^"]*)"|([^\s,]*))', header[7:])}
        if p.get("username") != "bblp" or p.get("nonce") != self.nonce or p.get("realm") != REALM:
            return False
        ha1, ha2 = md5(f"bblp:{REALM}:{ACCESS_CODE}"), md5(f"{method}:{p.get('uri', '')}")
        if self.qop:
            expected = md5(f"{ha1}:{self.nonce}:{p.get('nc', '')}:{p.get('cnonce', '')}:auth:{ha2}")
        else:
            expected = md5(f"{ha1}:{self.nonce}:{ha2}")
        return p.get("response") == expected

    def challenge(self):
        if not self.digest:
            return f'WWW-Authenticate: Basic realm="{REALM}"'
        qop = ', qop="auth"' if self.qop else ""
        return f'WWW-Authenticate: Digest realm="{REALM}", nonce="{self.nonce}"{qop}'

    def sdp(self):
        nals = H265 if self.codec == "h265" else H264
        if self.scenario == "vp8":
            media = "m=video 0 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n"
        elif self.codec == "h265":
            sets = {k: base64.b64encode(bytes.fromhex(nals[k])).decode() for k in ("vps", "sps", "pps")}
            media = ("m=video 0 RTP/AVP 96\r\na=rtpmap:96 H265/90000\r\n"
                     f"a=fmtp:96 sprop-vps={sets['vps']}; sprop-sps={sets['sps']}; sprop-pps={sets['pps']}\r\n")
        else:
            sets = ",".join(base64.b64encode(bytes.fromhex(nals[k])).decode() for k in ("sps", "pps"))
            media = f"m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=fmtp:96 packetization-mode=1;sprop-parameter-sets={sets}\r\n"
        return ("v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=Live\r\nt=0 0\r\na=control:*\r\n" + media + "a=control:track1\r\n").encode()

    def handle(self, request):
        lines = request.decode(errors="replace").split("\r\n")
        method, uri = (lines[0].split(" ") + ["", ""])[:2]
        fields = {}
        for line in lines[1:]:
            if ":" in line:
                k, v = line.split(":", 1)
                fields[k.strip().lower()] = v.strip()
        cseq = fields.get("cseq", "0")
        ok = self.authorized(method, fields)
        log(port="rtsp", scenario=self.scenario, method=method, authorization="authorization" in fields, accepted=ok)
        if method == "TEARDOWN":
            self.respond(cseq)
            return False
        if not ok:
            self.respond(cseq, "401 Unauthorized", [self.challenge()])
            return True
        if method == "DESCRIBE":
            if self.scenario == "negative-length":
                self.respond(cseq, headers=["Content-Type: application/sdp"], body=self.sdp(), length=-1)
            elif self.scenario == "huge-length":
                self.respond(cseq, headers=["Content-Type: application/sdp"], body=self.sdp(), length=9223372036854775807)
            elif self.scenario == "not-streaming":
                self.respond(cseq, "503 Service Unavailable")
            else:
                self.respond(cseq, headers=["Content-Type: application/sdp", f"Content-Base: {uri}/"], body=self.sdp())
        elif method == "SETUP":
            self.respond(cseq, headers=["Session: 5A1B2C3D;timeout=60", "Transport: RTP/AVP/TCP;unicast;interleaved=0-1"])
        elif method == "PLAY":
            self.respond(cseq, headers=["Session: 5A1B2C3D", "Range: npt=0.000-"])
            self.playing = True
        else:  # GET_PARAMETER, OPTIONS: keepalives
            self.respond(cseq, headers=["Session: 5A1B2C3D"])
        return True

    # Video

    def rtp(self, payload, timestamp, marker):
        self.sequence = (self.sequence + 1) % 65536
        header = struct.pack("!BBHII", 0x80, (0x80 if marker else 0) | 96, self.sequence, timestamp, 0x5EED)
        packet = header + payload
        self.send(b"$\x00" + struct.pack("!H", len(packet)) + packet)

    def fragments(self, nal, size=6):
        """FU-A (H.264) or FU (H.265) packets carrying one NAL unit."""
        if self.codec == "h264":
            header, body, kind = bytes([(nal[0] & 0xE0) | 28]), nal[1:], nal[0] & 0x1F
        else:
            header, body, kind = bytes([(nal[0] & 0x81) | (49 << 1), nal[1]]), nal[2:], (nal[0] >> 1) & 0x3F
        chunks = [body[i:i + size] for i in range(0, len(body), size)]
        out = []
        for i, chunk in enumerate(chunks):
            flags = (0x80 if i == 0 else 0) | (0x40 if i == len(chunks) - 1 else 0)
            out.append(header + bytes([flags | kind]) + chunk)
        return out

    def next_frame(self):
        nals = H265 if self.codec == "h265" else H264
        self.timestamp = (self.timestamp + 3000) % (1 << 32)
        if self.scenario == "split-fragment":
            idr = bytes.fromhex(nals["idr"])
            if self.frame == 0:
                self.rtp(idr, self.timestamp, True)  # one whole keyframe
            elif self.frame == 1:
                first, *_, last = self.fragments(idr)
                self.rtp(first, self.timestamp, False)  # its start at this frame's timestamp…
                self.rtp(last, (self.timestamp + 3000) % (1 << 32), True)  # …its end at the next one's
                self.timestamp = (self.timestamp + 3000) % (1 << 32)
            elif self.frame == 2:
                self.rtp(bytes.fromhex(nals["p"]), self.timestamp, True)
                self.script_done = True
                log(port="rtsp", scenario=self.scenario, sent="script")
            self.frame += 1
            return
        if self.frame % 10 == 0:
            packets = self.fragments(bytes.fromhex(nals["idr"]))
            for i, packet in enumerate(packets):
                self.rtp(packet, self.timestamp, i == len(packets) - 1)
        else:
            self.rtp(bytes.fromhex(nals["p"]), self.timestamp, True)
        self.frame += 1

    def run(self):
        buf = b""
        self.conn.settimeout(0.1)
        next_frame = time.monotonic()
        while True:
            try:
                data = self.conn.recv(65536)
                if not data:
                    return
                buf += data
            except (socket.timeout, TimeoutError):
                pass
            while b"\r\n\r\n" in buf:
                request, buf = buf.split(b"\r\n\r\n", 1)
                if not self.handle(request):
                    log(port="rtsp", scenario=self.scenario, closed="teardown")
                    return
            if self.playing and not self.script_done and time.monotonic() >= next_frame:
                self.next_frame()
                next_frame = time.monotonic() + 0.1


def serve_rtsp(conn, scenario):
    if scenario == "stall":
        log(port="rtsp", scenario=scenario, stalled=True)
        time.sleep(60)
        return
    RTSPSession(conn, scenario).run()


# MARK: - JPEG

def serve_jpeg(conn, scenario):
    if not scenario.startswith("jpeg"):
        scenario = "jpeg"
    login = b""
    while len(login) < 80:
        data = conn.recv(80 - len(login))
        if not data:
            return
        login += data
    header_ok = login[:16] == struct.pack("<IIII", 0x40, 0x3000, 0, 0)
    user, code = login[16:48].rstrip(b"\0").decode(errors="replace"), login[48:80].rstrip(b"\0").decode(errors="replace")
    accepted = header_ok and user == "bblp" and code == ACCESS_CODE
    log(port="jpeg", scenario=scenario, login=True, accepted=accepted)
    if not accepted:
        return  # the printers hang up without a word
    if scenario == "jpeg-zero":
        conn.sendall(bytes(16))
        time.sleep(2)
        return
    frame = struct.pack("<I", len(JPEG)) + bytes(12) + JPEG
    while True:
        if scenario == "jpeg-trickle":
            for i in range(0, len(frame), 13):
                conn.sendall(frame[i:i + 13])
        else:
            conn.sendall(frame)
        time.sleep(0.2)


# MARK: - Servers

def make_tls():
    os.makedirs(CERTS, exist_ok=True)
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "3650", "-subj",
                    f"/CN={CAMERA_SERIAL}", "-keyout", f"{CERTS}/camera.key", "-out", f"{CERTS}/camera.pem"],
                   check=True, capture_output=True)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(f"{CERTS}/camera.pem", f"{CERTS}/camera.key")
    return context


def listen(port, handler, context):
    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", port))
    server.listen()

    def handle(raw):
        scenario = state["scenario"]
        try:
            with context.wrap_socket(raw, server_side=True) as conn:
                handler(conn, scenario)
        except (OSError, ValueError) as e:
            log(port=port, scenario=scenario, closed=type(e).__name__)

    while True:
        raw, _ = server.accept()
        threading.Thread(target=handle, args=(raw,), daemon=True).start()


class Control(http.server.BaseHTTPRequestHandler):
    def reply(self, code, body):
        data = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_PUT(self):
        name = self.path.removeprefix("/scenario/")
        if not self.path.startswith("/scenario/") or not name:
            return self.reply(404, '{"error": "PUT /scenario/<name>"}')
        state["scenario"] = name
        log(control="scenario", scenario=name)
        self.reply(200, json.dumps({"scenario": name}))

    def do_GET(self):
        if self.path == "/events":
            with lock:
                body = "\n".join(json.dumps(e, sort_keys=True) for e in events)
            return self.reply(200, body)
        if self.path in ("/", "/scenario"):
            return self.reply(200, json.dumps({"scenario": state["scenario"], "serial": CAMERA_SERIAL}))
        self.reply(404, "{}")

    def do_DELETE(self):
        if self.path != "/events":
            return self.reply(404, "{}")
        with lock:
            events.clear()
        self.reply(200, "{}")

    def log_message(self, *args):
        pass


def main():
    context = make_tls()
    threading.Thread(target=listen, args=(RTSP_PORT, serve_rtsp, context), daemon=True).start()
    threading.Thread(target=listen, args=(JPEG_PORT, serve_jpeg, context), daemon=True).start()
    print(f"Mock camera {CAMERA_SERIAL}: RTSPS on {RTSP_PORT}, JPEG on {JPEG_PORT}, control on {CONTROL_PORT}, "
          f"access code {ACCESS_CODE}", flush=True)
    http.server.ThreadingHTTPServer(("0.0.0.0", CONTROL_PORT), Control).serve_forever()


if __name__ == "__main__":
    main()
