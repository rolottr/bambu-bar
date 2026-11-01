import CoreMedia
import Foundation

/// Plays the RTSPS live view of X1, H2, P2S and X2D printers (port 322, `bblp` and the access code).
///
/// RTSP runs over the TLS connection and so does the video: RTP is interleaved on the same connection
/// (`$`, channel, 16-bit length, packet), which avoids UDP ports and firewalls. DESCRIBE → SETUP → PLAY,
/// then a keepalive request every 20 seconds and TEARDOWN when done.
final class RTSPCameraClient: CameraClient {
    var onEvent: ((CameraEvent) -> Void)?
    var log: ((String) -> Void)?

    private let config: CameraConfig
    private let queue = DispatchQueue(label: "dev.rolottr.BambuBar.camera.rtsp")
    private let url: String
    private var socket: CameraSocket?
    private var buffer: [UInt8] = []
    private var sequence = 0
    /// Requests awaiting a response, by CSeq.
    private var requests: [Int: (method: String, uri: String, headers: [String])] = [:]
    private var auth: RTSPAuth?
    private var session: String?
    private var trackURI = ""
    private var videoChannel: UInt8 = 0
    private var depacketizer: VideoDepacketizer?
    private var keepAliveMethod = "GET_PARAMETER"
    private var keepAlive: DispatchSourceTimer?
    private var watchdog: DispatchSourceTimer?
    private var playing = false
    private var started = false
    private var frames = 0
    private var lastPacket = Date()
    private var openedAt = Date()
    private var finished = false

    init(_ config: CameraConfig) {
        self.config = config
        url = "rtsps://\(config.host):\(CameraTransport.rtsps.port)/streaming/live/1"
    }

    func start() {
        queue.async { self.open() }
    }

    func stop() {
        queue.async { self.finish(nil) }
    }

    // MARK: - Connection

    private func open() {
        let socket = CameraSocket(host: config.host, port: CameraTransport.rtsps.port, serial: config.serial,
                                  insecure: config.insecure, queue: queue)
        socket.log = log
        socket.onReady = { [weak self] in
            guard let self else { return }
            self.send("DESCRIBE", self.url, headers: ["Accept: application/sdp"])
        }
        socket.onData = { [weak self] data in self?.received(data) }
        socket.onClose = { [weak self] failure in self?.finish(failure) }
        self.socket = socket
        openedAt = Date()
        lastPacket = Date()
        socket.open()

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            if self.playing {
                if Date().timeIntervalSince(self.lastPacket) > 10 { self.finish(.timedOut) }
            } else if Date().timeIntervalSince(self.openedAt) > 20 {
                self.finish(.timedOut)
            }
        }
        timer.resume()
        watchdog = timer
    }

    private func send(_ method: String, _ uri: String, headers: [String] = []) {
        let message = request(method, uri, headers: headers)
        requests[sequence] = (method, uri, headers)
        log?("> \(method) \(uri)")
        socket?.send(message)
    }

    /// The next request, numbered and signed.
    private func request(_ method: String, _ uri: String, headers: [String] = []) -> Data {
        sequence += 1
        var lines = ["\(method) \(uri) RTSP/1.0", "CSeq: \(sequence)", "User-Agent: Bambu Bar"]
        if let header = auth?.header(method: method, uri: uri) { lines.append("Authorization: \(header)") }
        if let session { lines.append("Session: \(session)") }
        lines += headers
        return Data((lines.joined(separator: "\r\n") + "\r\n\r\n").utf8)
    }

    // MARK: - Parsing

    private func received(_ data: Data) {
        buffer.append(contentsOf: data)
        var offset = 0
        parse: while offset < buffer.count, !finished {
            if buffer[offset] == 0x24 {  // '$': an interleaved RTP or RTCP packet
                guard buffer.count - offset >= 4 else { break }
                let channel = buffer[offset + 1]
                let length = Int(buffer[offset + 2]) << 8 | Int(buffer[offset + 3])
                guard buffer.count - offset >= 4 + length else { break }
                if channel == videoChannel, length >= 12 {
                    handleRTP(Array(buffer[(offset + 4)..<(offset + 4 + length)]))
                }
                offset += 4 + length
            } else if buffer[offset] >= 0x41 && buffer[offset] <= 0x5A {  // 'A'…'Z': "RTSP/1.0 200 OK" or a request
                guard let headerEnd = buffer[offset...].firstRange(of: [13, 10, 13, 10])?.lowerBound else {
                    if buffer.count - offset > 64 * 1024 { finish(.malformed("RTSP header too long")) }
                    break parse
                }
                let head = String(decoding: buffer[offset..<headerEnd], as: UTF8.self)
                let message = RTSPMessage(head)
                let bodyStart = headerEnd + 4
                let length = Int(message.header("Content-Length") ?? "0") ?? 0
                guard buffer.count - bodyStart >= length else { break }
                let body = String(decoding: buffer[bodyStart..<(bodyStart + length)], as: UTF8.self)
                offset = bodyStart + length
                if message.isResponse { handleResponse(message, body: body) }
            } else {
                offset += 1  // resynchronise on the next '$' or message
            }
        }
        if offset > 0 { buffer.removeFirst(min(offset, buffer.count)) }
    }

    private func handleResponse(_ response: RTSPMessage, body: String) {
        guard let cseq = Int(response.header("CSeq") ?? ""), let request = requests.removeValue(forKey: cseq) else { return }
        log?("< \(response.statusLine) (\(request.method))")
        if request.method == keepAliveMethod && playing {
            // Some servers don't implement GET_PARAMETER; OPTIONS keeps the session alive too.
            if response.status != 200 && keepAliveMethod == "GET_PARAMETER" { keepAliveMethod = "OPTIONS" }
            return
        }
        if response.status == 401 {
            guard auth?.hasFailed != true else { return finish(.unauthorized) }
            let challenges = response.headers("WWW-Authenticate")
            guard let next = RTSPAuth(challenges: challenges, username: "bblp", password: config.accessCode) else {
                return finish(.unauthorized)
            }
            // Credentials sent once and refused again mean the code is wrong.
            if auth != nil { next.hasFailed = true }
            auth = next
            send(request.method, request.uri, headers: request.headers)
            return
        }
        guard response.status == 200 else {
            if response.status == 404 || response.status == 454 || response.status == 503 {
                return finish(.unreachable("The camera isn't streaming (\(response.statusLine)). Is LAN Only Liveview on?"))
            }
            return finish(.malformed("\(request.method) answered \(response.statusLine)"))
        }
        switch request.method {
        case "DESCRIBE":
            log?("SDP:\n" + body)
            guard let video = SDP.video(in: body) else {
                return finish(.unsupported("The camera offers no H.264 or H.265 video."))
            }
            log?("video: \(video.codec.rawValue), payload type \(video.payloadType), control \(video.control ?? "-")")
            depacketizer = VideoDepacketizer(codec: video.codec, parameterSets: video.parameterSets)
            let base = response.header("Content-Base") ?? response.header("Content-Location") ?? url
            trackURI = SDP.resolve(video.control, base: base)
            send("SETUP", trackURI, headers: ["Transport: RTP/AVP/TCP;unicast;interleaved=0-1"])
        case "SETUP":
            guard let value = response.header("Session") else { return finish(.malformed("SETUP without a session")) }
            session = value.split(separator: ";").first.map { String($0).trimmingCharacters(in: .whitespaces) }
            if let transport = response.header("Transport"),
               let range = transport.range(of: "interleaved="),
               let channel = UInt8(transport[range.upperBound...].prefix { $0.isNumber }) {
                videoChannel = channel
            }
            var timeout = 60
            for part in value.split(separator: ";") where part.trimmingCharacters(in: .whitespaces).hasPrefix("timeout=") {
                timeout = Int(part.split(separator: "=").last ?? "") ?? 60
            }
            send("PLAY", url, headers: ["Range: npt=0.000-"])
            startKeepAlive(every: max(5, min(20, Double(timeout) / 2)))
        case "PLAY":
            playing = true
            lastPacket = Date()
        default:
            break
        }
    }

    private func handleRTP(_ packet: [UInt8]) {
        guard packet[0] >> 6 == 2, let depacketizer else { return }
        lastPacket = Date()
        let csrcs = Int(packet[0] & 0x0F)
        var start = 12 + 4 * csrcs
        if packet[0] & 0x10 != 0 {  // header extension
            guard packet.count >= start + 4 else { return }
            start += 4 + 4 * (Int(packet[start + 2]) << 8 | Int(packet[start + 3]))
        }
        var end = packet.count
        if packet[0] & 0x20 != 0, let padding = packet.last { end -= Int(padding) }
        guard start < end else { return }
        let marker = packet[1] & 0x80 != 0
        let sequence = UInt16(packet[2]) << 8 | UInt16(packet[3])
        let timestamp = UInt32(packet[4]) << 24 | UInt32(packet[5]) << 16 | UInt32(packet[6]) << 8 | UInt32(packet[7])
        for frame in depacketizer.push(Array(packet[start..<end]), sequence: sequence, timestamp: timestamp, marker: marker) {
            frames += 1
            if !started, let format = depacketizer.format {
                started = true
                log?("first keyframe: \(depacketizer.codec.rawValue) \(format.dimensionsText)")
                let size = CMVideoFormatDescriptionGetDimensions(format)
                deliver(.started("\(depacketizer.codec.rawValue) · \(format.dimensionsText)",
                                 size: CGSize(width: Int(size.width), height: Int(size.height))))
            }
            deliver(.video(frame.sample, keyframe: frame.isKeyframe))
        }
    }

    private func startKeepAlive(every interval: TimeInterval) {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + interval, repeating: interval)
        timer.setEventHandler { [weak self] in
            guard let self, self.playing else { return }
            self.send(self.keepAliveMethod, self.url)
        }
        timer.resume()
        keepAlive = timer
    }

    private func finish(_ failure: CameraFailure?) {
        guard !finished else { return }
        finished = true
        keepAlive?.cancel()
        watchdog?.cancel()
        keepAlive = nil
        watchdog = nil
        socket?.onClose = nil
        if failure == nil, session != nil {
            // Tell the printer we're gone; it serves only a few viewers at a time.
            socket?.close(after: request("TEARDOWN", url))
        } else {
            socket?.close()
        }
        socket = nil
        if let failure {
            log?("closed: \(failure)")
            deliver(.failed(failure))
        }
    }

    private func deliver(_ event: CameraEvent) {
        DispatchQueue.main.async { [weak self] in self?.onEvent?(event) }
    }
}
