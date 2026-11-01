import Foundation
import Network
import Security

/// A small MQTT 3.1.1 client over TLS 1.2, just what a Bambu printer's broker needs:
/// CONNECT with username/password, SUBSCRIBE, QoS 0 PUBLISH both ways, keepalive pings.
///
/// All work happens on a private serial queue; events are delivered on the main queue.
final class MQTTClient {
    struct Configuration {
        var host: String
        var port: UInt16 = 8883
        var username = "bblp"
        var password: String
        /// The printer serial, which its certificate is named after.
        var serial: String
        var keepAlive: UInt16 = 30
        var connectTimeout: TimeInterval = 12
        /// Accept any certificate (the local printer emulator only).
        var insecure = false
    }

    enum Failure: Error, Equatable {
        /// CONNACK return code: 4 bad username/password, 5 not authorised.
        case refused(UInt8)
        case certificate(String)
        case unreachable(String)
        /// macOS keeps the app off the local network (Privacy & Security → Local Network).
        case localNetworkDenied
        case timedOut
        case closed
        case malformed

        var isAuthentication: Bool {
            if case .refused(let code) = self { return code == 4 || code == 5 }
            return false
        }
    }

    enum Event {
        case connected
        case message(topic: String, payload: Data)
        case disconnected(Failure)
    }

    var onEvent: ((Event) -> Void)?

    private let config: Configuration
    private let queue = DispatchQueue(label: "dev.rolottr.BambuBar.mqtt")
    private var connection: NWConnection?
    private var buffer = Data()
    private var nextPacketID: UInt16 = 1
    private var isOpen = false
    private var finished = false
    private var pingTimer: DispatchSourceTimer?
    private var connectDeadline: DispatchWorkItem?
    private var lastInbound = Date()
    private var trustVerdict: BambuTrust.Verdict?

    init(_ config: Configuration) {
        self.config = config
    }

    func connect() {
        queue.async { self.start() }
    }

    func subscribe(_ topic: String) {
        queue.async {
            var body = Data()
            body.append(uint16: self.allocatePacketID())
            body.append(mqttString: topic)
            body.append(0)  // QoS 0
            self.send(type: 0x82, body: body)
        }
    }

    func publish(_ topic: String, payload: Data) {
        queue.async {
            var body = Data()
            body.append(mqttString: topic)
            body.append(payload)
            self.send(type: 0x30, body: body)
        }
    }

    func disconnect() {
        queue.async {
            if self.isOpen { self.send(type: 0xE0, body: Data()) }
            self.finish(.closed, notify: false)
        }
    }

    // MARK: - Connection

    private func start() {
        let tls = Self.tlsOptions(queue: queue) { [weak self] trust in
            guard let self else { return false }
            if self.config.insecure { return true }
            let verdict = BambuTrust.evaluate(trust, serial: self.config.serial)
            self.trustVerdict = verdict
            return !verdict.isRejected
        }
        let tcp = NWProtocolTCP.Options()
        tcp.connectionTimeout = Int(config.connectTimeout)
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 20
        let params = NWParameters(tls: tls, tcp: tcp)
        let conn = NWConnection(host: NWEndpoint.Host(config.host), port: NWEndpoint.Port(rawValue: config.port)!, using: params)
        connection = conn
        conn.stateUpdateHandler = { [weak self] state in self?.handle(state) }

        let deadline = DispatchWorkItem { [weak self] in
            guard let self, !self.isOpen else { return }
            self.finish(.timedOut)
        }
        connectDeadline = deadline
        queue.asyncAfter(deadline: .now() + config.connectTimeout + 4, execute: deadline)
        conn.start(queue: queue)
    }

    private func handle(_ state: NWConnection.State) {
        switch state {
        case .ready:
            sendConnect()
            receive()
        case .waiting(let error), .failed(let error):
            if connection?.currentPath?.unsatisfiedReason == .localNetworkDenied {
                finish(.localNetworkDenied)
            } else if let verdict = trustVerdict, case .rejected(let reason) = verdict {
                finish(.certificate(reason))
            } else {
                // No route (Wi-Fi down, printer off) or a refused connection: give up so the owner can back off.
                finish(.unreachable(Self.describe(error)))
            }
        case .cancelled:
            finish(.closed)
        default:
            break
        }
    }

    /// TLS as the printers speak it, with the certificate decision left to `verify`.
    static func tlsOptions(queue: DispatchQueue, verify: @escaping (SecTrust) -> Bool) -> NWProtocolTLS.Options {
        let tls = NWProtocolTLS.Options()
        let sec = tls.securityProtocolOptions
        // P2S firmware never answers a TLS 1.3 ClientHello.
        sec_protocol_options_set_min_tls_protocol_version(sec, .TLSv12)
        sec_protocol_options_set_max_tls_protocol_version(sec, .TLSv12)
        // A resumed session skips the certificate, and with it `verify`: the only check that the peer is the
        // printer with the expected serial (and, for PrinterIdentity, the only way to read that serial). Every
        // connection gets a full handshake instead; on a LAN it costs next to nothing.
        sec_protocol_options_set_tls_resumption_enabled(sec, false)
        sec_protocol_options_set_verify_block(sec, { _, trust, complete in
            complete(verify(sec_trust_copy_ref(trust).takeRetainedValue()))
        }, queue)
        return tls
    }

    static func describe(_ error: NWError) -> String {
        switch error {
        case .posix(let code):
            switch code {
            case .ECONNREFUSED: return String(localized: "The printer refused the connection.")
            case .EHOSTUNREACH, .EHOSTDOWN: return String(localized: "The printer isn't reachable on the network.")
            case .ENETDOWN, .ENETUNREACH: return String(localized: "This Mac isn't on a network.")
            case .ETIMEDOUT: return String(localized: "The printer didn't answer.")
            default: return error.localizedDescription
            }
        default:
            return error.localizedDescription
        }
    }

    private func sendConnect() {
        var body = Data()
        body.append(mqttString: "MQTT")
        body.append(4)  // protocol level 3.1.1
        body.append(0xC2)  // username, password, clean session
        body.append(uint16: config.keepAlive)
        body.append(mqttString: "bambubar-\(UUID().uuidString.prefix(12).lowercased())")
        body.append(mqttString: config.username)
        body.append(mqttString: config.password)
        send(type: 0x10, body: body)
    }

    private func receive() {
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { [weak self] data, _, complete, error in
            guard let self, !self.finished else { return }
            if let data, !data.isEmpty {
                self.lastInbound = Date()
                self.buffer.append(data)
                self.drainPackets()
            }
            if let error {
                self.finish(.unreachable(Self.describe(error)))
            } else if complete {
                self.finish(.closed)
            } else {
                self.receive()
            }
        }
    }

    private func drainPackets() {
        while !finished {
            guard buffer.count >= 2 else { return }
            // Remaining length: up to four 7-bit groups.
            var multiplier = 1, length = 0, index = buffer.startIndex + 1, done = false
            for _ in 0..<4 {
                guard index < buffer.endIndex else { return }
                let byte = Int(buffer[index])
                length += (byte & 0x7F) * multiplier
                multiplier *= 128
                index += 1
                if byte & 0x80 == 0 { done = true; break }
            }
            guard done else { return finish(.malformed) }
            guard buffer.endIndex - index >= length else { return }
            let header = buffer[buffer.startIndex]
            let body = Data(buffer[index..<(index + length)])
            buffer.removeSubrange(buffer.startIndex..<(index + length))
            handlePacket(header: header, body: body)
        }
    }

    private func handlePacket(header: UInt8, body: Data) {
        switch header >> 4 {
        case 2:  // CONNACK
            guard body.count >= 2 else { return finish(.malformed) }
            let code = body[body.startIndex + 1]
            if code != 0 { return finish(.refused(code)) }
            isOpen = true
            connectDeadline?.cancel()
            startPings()
            deliver(.connected)
        case 3:  // PUBLISH
            let qos = (header >> 1) & 0x03
            guard body.count >= 2 else { return }
            let topicLength = Int(body[body.startIndex]) << 8 | Int(body[body.startIndex + 1])
            var offset = body.startIndex + 2 + topicLength
            guard offset <= body.endIndex else { return }
            let topic = String(decoding: body[(body.startIndex + 2)..<offset], as: UTF8.self)
            if qos > 0 {
                guard offset + 2 <= body.endIndex else { return }
                let id = body[offset..<(offset + 2)]
                offset += 2
                if qos == 1 { send(type: 0x40, body: Data(id)) }  // PUBACK
            }
            deliver(.message(topic: topic, payload: Data(body[offset...])))
        default:
            break  // SUBACK, PINGRESP, PUBACK
        }
    }

    private func startPings() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        let interval = Double(config.keepAlive)
        timer.schedule(deadline: .now() + interval, repeating: interval)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            if Date().timeIntervalSince(self.lastInbound) > interval * 1.5 + 5 {
                self.finish(.timedOut)
            } else {
                self.send(type: 0xC0, body: Data())
            }
        }
        timer.resume()
        pingTimer = timer
    }

    private func send(type: UInt8, body: Data) {
        guard let connection, !finished else { return }
        var packet = Data([type])
        var length = body.count
        repeat {
            var byte = UInt8(length % 128)
            length /= 128
            if length > 0 { byte |= 0x80 }
            packet.append(byte)
        } while length > 0
        packet.append(body)
        connection.send(content: packet, completion: .contentProcessed { [weak self] error in
            if let error { self?.finish(.unreachable(Self.describe(error))) }
        })
    }

    private func allocatePacketID() -> UInt16 {
        defer { nextPacketID = nextPacketID == .max ? 1 : nextPacketID + 1 }
        return nextPacketID
    }

    private func finish(_ failure: Failure, notify: Bool = true) {
        guard !finished else { return }
        finished = true
        isOpen = false
        pingTimer?.cancel()
        pingTimer = nil
        connectDeadline?.cancel()
        connection?.stateUpdateHandler = nil
        connection?.cancel()
        connection = nil
        if notify { deliver(.disconnected(failure)) }
    }

    private func deliver(_ event: Event) {
        DispatchQueue.main.async { [weak self] in self?.onEvent?(event) }
    }
}

private extension Data {
    mutating func append(uint16 value: UInt16) {
        append(UInt8(value >> 8))
        append(UInt8(value & 0xFF))
    }

    mutating func append(mqttString string: String) {
        let bytes = Array(string.utf8)
        append(uint16: UInt16(bytes.count))
        append(contentsOf: bytes)
    }
}
