import AppKit
import Foundation

/// How Bambu Bar talks to a printer.
enum PrinterKind: String, Codable, Sendable {
    /// Bambu Lab's LAN MQTT, with the access code.
    case bambu
    /// A Klipper printer through Moonraker's WebSocket API (Creality K1 with Moonraker installed, Voron…).
    case moonraker
}

/// Where to find the printer. The access code lives in the Keychain, not here.
struct PrinterConfig: Codable, Equatable {
    var host: String
    /// For Bambu printers their serial; for Moonraker printers an id made from the address ("klipper-192.168.1.60").
    var serial: String
    var name: String
    /// SSDP model code ("N7" for the P2S).
    var modelCode: String
    /// Nil in configs saved before Moonraker support, which are all Bambu printers.
    var kind: PrinterKind? = nil

    var printerKind: PrinterKind { kind ?? .bambu }
    var needsAccessCode: Bool { printerKind == .bambu }

    var modelName: String {
        if printerKind == .moonraker { return model?.name ?? String(localized: "Klipper printer") }
        return PrinterModel.name(for: modelCode, serial: serial)
    }
    /// "Bambu Lab P2S", "Creality K1 Max", "Klipper printer".
    var fullModelName: String {
        if let model { return model.fullName }
        return printerKind == .bambu ? "Bambu Lab \(modelName)" : modelName
    }
    var model: PrinterModel? {
        // Moonraker ids aren't Bambu serials; don't let their prefix pick a Bambu model.
        PrinterModel.find(code: modelCode, serial: printerKind == .bambu ? serial : "")
    }

    /// Bambu printers listen on 8883, Moonraker on 7125; `host:port` reaches another port (a mock printer, scripts/fleet).
    var endpoint: (host: String, port: UInt16) {
        let parts = host.split(separator: ":")
        guard parts.count == 2, let port = UInt16(parts[1]) else { return (host, printerKind == .moonraker ? 7125 : 8883) }
        return (String(parts[0]), port)
    }

    /// The session id for a Moonraker printer at `host`.
    static func moonrakerID(host: String) -> String { "klipper-\(host.lowercased())" }
}

/// Keeps one MQTT session to the printer alive: connects, re-requests a full report, merges the deltas
/// into a status, and reconnects with back-off when the printer drops off the network or the Mac sleeps.
@MainActor
final class PrinterConnection {
    enum Phase: Equatable {
        case idle
        case connecting
        case online
        case offline(String)
        case unauthorized
        case certificate(String)
        /// macOS keeps Bambu Bar off the local network; retried, so it reconnects once access is turned on.
        case localNetworkDenied
    }

    var onPhase: ((Phase) -> Void)?
    var onStatus: ((PrinterStatus, ReportDocument) -> Void)?
    /// Any message from the printer, changed or not.
    var onHeartbeat: (() -> Void)?
    /// A command acknowledgement: command name, success, reason.
    var onAck: ((String, Bool, String?) -> Void)?

    private(set) var phase: Phase = .idle {
        didSet { if phase != oldValue { onPhase?(phase) } }
    }

    private var config: PrinterConfig?
    private var accessCode = ""
    private var insecure = false
    private var client: MQTTClient?
    private var moonraker: MoonrakerClient?
    private var document = ReportDocument()
    private var lastStatus: PrinterStatus?
    private var lastMessage = Date.distantPast
    private var lastPushall = Date.distantPast
    private var attempt = 0
    private var retry: Timer?
    private var watchdog: Timer?
    private var sequence = 0
    private var observers: [NSObjectProtocol] = []

    init() {
        let center = NSWorkspace.shared.notificationCenter
        observers.append(center.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.reconnectSoon(after: 2) }
        })
        observers.append(center.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.suspend() }
        })
    }

    func start(_ config: PrinterConfig, accessCode: String, insecure: Bool = false) {
        stop()
        self.config = config
        self.accessCode = accessCode
        self.insecure = insecure
        attempt = 0
        open()
        watchdog = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.checkHealth() }
        }
    }

    func stop() {
        retry?.invalidate()
        watchdog?.invalidate()
        retry = nil
        watchdog = nil
        closeClient()
        config = nil
        lastStatus = nil
        document.reset()
        phase = .idle
    }

    /// Asks for a full report now (after wake, or when the person clicks refresh).
    func refresh() {
        if phase == .online, let moonraker { moonraker.subscribe() }
        else if phase == .online { requestFullReport() } else if config != nil { reconnectSoon(after: 0) }
    }

    // MARK: - Commands

    func pause() { if let moonraker { moonraker.pause() } else { sendPrint(["command": "pause"]) } }
    func resume() { if let moonraker { moonraker.resume() } else { sendPrint(["command": "resume"]) } }
    func stopPrint() { if let moonraker { moonraker.cancel() } else { sendPrint(["command": "stop"]) } }
    func setSpeed(_ level: SpeedLevel) {
        if let moonraker { moonraker.setSpeed(percent: level.percent) } else { sendPrint(["command": "print_speed", "param": "\(level.rawValue)"]) }
    }

    func setChamberLight(_ on: Bool) {
        if let moonraker { moonraker.setLight(on); return }
        send(["system": ["command": "ledctrl", "led_node": "chamber_light", "led_mode": on ? "on" : "off",
                         "led_on_time": 500, "led_off_time": 500, "loop_times": 0, "interval_time": 0]], key: "system")
    }

    private func sendPrint(_ body: [String: Any]) { send(["print": body], key: "print") }

    private func send(_ message: [String: Any], key: String) {
        guard let config, let client, phase == .online else { return }
        sequence += 1
        var message = message
        if var body = message[key] as? [String: Any] {
            body["sequence_id"] = "\(sequence)"
            message[key] = body
        }
        guard let data = try? JSONSerialization.data(withJSONObject: message) else { return }
        client.publish("device/\(config.serial)/request", payload: data)
    }

    // MARK: - Session

    private func open() {
        guard let config else { return }
        closeClient()
        phase = .connecting
        // The watchdog measures silence from here, so it doesn't cut off an attempt that's still connecting.
        lastMessage = Date()
        let endpoint = config.endpoint
        if config.printerKind == .moonraker {
            openMoonraker(host: endpoint.host, port: endpoint.port)
            return
        }
        let client = MQTTClient(.init(host: endpoint.host, port: endpoint.port, password: accessCode, serial: config.serial,
                                      insecure: insecure))
        client.onEvent = { [weak self, weak client] event in
            MainActor.assumeIsolated {
                guard let self, let client, client === self.client else { return }
                self.handle(event)
            }
        }
        self.client = client
        client.connect()
    }

    private func closeClient() {
        client?.onEvent = nil
        client?.disconnect()
        client = nil
        moonraker?.onEvent = nil
        moonraker?.disconnect()
        moonraker = nil
    }

    private func openMoonraker(host: String, port: UInt16) {
        // A Creality K1 prepares a print (self-test, levelling, heating) before Klipper hears of it.
        let moonraker = MoonrakerClient(host: host, port: port, crealityHost: config?.model?.maker == "Creality" ? host : nil)
        moonraker.onEvent = { [weak self, weak moonraker] event in
            MainActor.assumeIsolated {
                guard let self, let moonraker, moonraker === self.moonraker else { return }
                self.handle(event)
            }
        }
        self.moonraker = moonraker
        moonraker.connect()
    }

    private func handle(_ event: MoonrakerClient.Event) {
        switch event {
        case .alive:
            lastMessage = Date()
            onHeartbeat?()
        case .status(let status):
            lastMessage = Date()
            onHeartbeat?()
            if phase != .online {
                attempt = 0
                phase = .online
            }
            publish(status, document: ReportDocument())
        case .commandFailed(let command, let reason):
            onAck?(command, false, reason)
        case .disconnected(let reason):
            moonraker = nil
            phase = .offline(reason)
            scheduleReconnect()
        }
    }

    private func handle(_ event: MQTTClient.Event) {
        guard let config else { return }
        switch event {
        case .connected:
            lastMessage = Date()
            client?.subscribe("device/\(config.serial)/report")
            sendRaw(["info": ["sequence_id": "0", "command": "get_version"]])
            requestFullReport()
            // Stay "connecting" until the first report arrives, so a silent broker doesn't look online.
        case .message(_, let payload):
            lastMessage = Date()
            onHeartbeat?()
            guard let json = Self.decode(payload) else { return }
            if let p = json["print"] as? [String: Any], let command = p["command"] as? String,
               command != "push_status", let result = p["result"] as? String {
                onAck?(command, result.lowercased() == "success", p["reason"] as? String)
            }
            guard document.apply(json) else { return }
            guard document.hasFullReport || document.print["gcode_state"] != nil else { return }
            if phase != .online {
                // Back-off resets only once the printer is really talking, not on a CONNACK alone.
                attempt = 0
                phase = .online
            }
            publishStatus()
        case .disconnected(let failure):
            client = nil
            switch failure {
            case .refused(let code) where code == 4 || code == 5:
                // A wrong access code won't fix itself; wait for the person to change it.
                phase = .unauthorized
                return
            case .certificate(let reason):
                // A certificate that doesn't check out won't fix itself either; don't keep sending it the code.
                phase = .certificate(reason)
                return
            case .unreachable(let reason):
                phase = .offline(reason)
            case .localNetworkDenied:
                phase = .localNetworkDenied
            case .timedOut:
                phase = .offline(String(localized: "The printer stopped answering."))
            case .closed, .malformed, .refused:
                phase = .offline(String(localized: "The connection to the printer closed."))
            }
            scheduleReconnect()
        }
    }

    private func publishStatus() {
        publish(ReportParser.status(from: document, hms: HMSCatalog.shared), document: document)
    }

    private func publish(_ reported: PrinterStatus, document: ReportDocument) {
        var status = reported
        if let last = lastStatus {
            // Keep the timestamp the remaining time was measured at, so the ETA doesn't drift between reports.
            var same = status
            same.updatedAt = last.updatedAt
            if same == last { return }
            if status.remainingMinutes == last.remainingMinutes && status.progress == last.progress && status.state == last.state {
                status.updatedAt = last.updatedAt
            }
        }
        lastStatus = status
        onStatus?(status, document)
    }

    private func requestFullReport() {
        lastPushall = Date()
        sendRaw(["pushing": ["sequence_id": "0", "command": "pushall"]])
    }

    private func sendRaw(_ message: [String: Any]) {
        guard let config, let client, let data = try? JSONSerialization.data(withJSONObject: message) else { return }
        client.publish("device/\(config.serial)/request", payload: data)
    }

    private func checkHealth() {
        guard config != nil else { return }
        let silence = Date().timeIntervalSince(lastMessage)
        switch phase {
        case .online where moonraker != nil:
            // Moonraker sends nothing while nothing changes; a ping tells a quiet printer from a gone one.
            if silence > 75 {
                phase = .offline(String(localized: "The printer stopped answering."))
                reconnectSoon(after: 0)
            } else if silence > 20 {
                moonraker?.ping()
            }
        case .online:
            if silence > 95 {
                phase = .offline(String(localized: "The printer stopped sending updates."))
                reconnectSoon(after: 0)
            } else if silence > 60 || Date().timeIntervalSince(lastPushall) > 300 {
                requestFullReport()
            }
        case .connecting:
            if client != nil || moonraker != nil, silence > 25 { reconnectSoon(after: 0) }
        default:
            break
        }
    }

    private func scheduleReconnect() {
        let delays: [TimeInterval] = [2, 4, 8, 15, 30]
        let delay = delays[min(attempt, delays.count - 1)]
        attempt += 1
        reconnectSoon(after: delay)
    }

    private func reconnectSoon(after delay: TimeInterval) {
        guard config != nil else { return }
        retry?.invalidate()
        retry = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.open() }
        }
    }

    private func suspend() {
        retry?.invalidate()
        closeClient()
    }

    /// Some firmware sends bytes that aren't valid UTF-8; fall back to Latin-1 rather than drop the report.
    private static func decode(_ data: Data) -> [String: Any]? {
        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { return json }
        guard let text = String(data: data, encoding: .isoLatin1), let utf8 = text.data(using: .utf8) else { return nil }
        return try? JSONSerialization.jsonObject(with: utf8) as? [String: Any]
    }
}
