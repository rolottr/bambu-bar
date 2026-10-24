import Foundation

/// One JSON-RPC session to Moonraker (`ws://<host>:7125/websocket`) for a Klipper printer: lists the printer's
/// objects, subscribes to the ones Bambu Bar shows, merges the deltas and folds them into a `PrinterStatus`.
/// Moonraker trusts clients on the local network by default, so no API key is sent. Events arrive on the main queue.
final class MoonrakerClient: NSObject, URLSessionWebSocketDelegate {
    enum Event {
        /// The printer answered a ping or a request without anything changing.
        case alive
        case status(PrinterStatus)
        /// A command the printer refused: method, reason.
        case commandFailed(String, String?)
        case disconnected(String)
    }

    var onEvent: ((Event) -> Void)?

    private let url: URL
    private var session: URLSession?
    private var task: URLSessionWebSocketTask?
    private var closed = false
    private var nextID = 1
    private var pending: [Int: (Result<Any, RPCError>) -> Void] = [:]

    /// The merged `printer.objects` state, by object name; nil until the first subscription answers.
    private var objects: [String: [String: Any]]?
    private var lightObject: String?
    private var chamberObject: String?
    /// The slicer's estimate for the file being printed, from its metadata.
    private var metadataFile: String?
    private var estimatedSeconds: Double?
    private var resubscribe: DispatchWorkItem?
    /// Klipper reports temperatures several times a second; the status goes out at most once a second.
    private var lastPublish = Date.distantPast
    private var deferredPublish: DispatchWorkItem?

    struct RPCError: Error {
        var message: String
    }

    /// Creality's own status on port 9999, for what Klipper doesn't know yet (see `CrealityStateWatcher`).
    private var creality: CrealityStateWatcher?

    /// `crealityHost`: also listen to a Creality K1's stock status (only listen; nothing is sent there).
    init(host: String, port: UInt16, crealityHost: String? = nil) {
        url = URL(string: "ws://\(host):\(port)/websocket")!
        super.init()
        if let crealityHost {
            creality = CrealityStateWatcher(host: crealityHost)
            creality?.onChange = { [weak self] in self?.publishSoon() }
        }
    }

    func connect() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 15
        let session = URLSession(configuration: config, delegate: self, delegateQueue: .main)
        let task = session.webSocketTask(with: url)
        // The first subscription result carries every object at once.
        task.maximumMessageSize = 8 << 20
        self.session = session
        self.task = task
        task.resume()
        receive()
        creality?.connect()
    }

    func disconnect() {
        closed = true
        creality?.disconnect()
        resubscribe?.cancel()
        deferredPublish?.cancel()
        task?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
        task = nil
        session = nil
        pending.removeAll()
    }

    /// A WebSocket control frame: Moonraker is quiet while nothing changes.
    func ping() {
        task?.sendPing { [weak self] error in
            DispatchQueue.main.async {
                guard let self, !self.closed else { return }
                if let error { self.fail(error.localizedDescription) } else { self.onEvent?(.alive) }
            }
        }
    }

    // MARK: - Probe

    /// Klipper's hostname from `GET /printer/info`, to check Moonraker answers at an address before adding it.
    static func hostname(host: String, port: UInt16) async -> Result<String, RPCError> {
        guard let url = URL(string: "http://\(host):\(port)/printer/info") else {
            return .failure(RPCError(message: String(localized: "That isn't a valid address.")))
        }
        var request = URLRequest(url: url)
        request.timeoutInterval = 8
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            guard (response as? HTTPURLResponse)?.statusCode == 200, let result = json?["result"] as? [String: Any] else {
                let message = (json?["error"] as? [String: Any])?["message"] as? String
                return .failure(RPCError(message: message.map { String(localized: "Moonraker answered, but Klipper isn't ready: \($0)") }
                                         ?? String(localized: "That address didn't answer as Moonraker.")))
            }
            return .success(result["hostname"] as? String ?? "")
        } catch {
            return .failure(RPCError(message: String(localized: "Nothing answered as Moonraker at that address (port \(Int(port))): \(error.localizedDescription)")))
        }
    }

    /// A catalog model for printers whose stock hostname names them ("K1Max-2C5B").
    static func modelCode(forHostname hostname: String) -> String {
        let compact = hostname.lowercased().replacingOccurrences(of: "-", with: "").replacingOccurrences(of: "_", with: "")
        return compact.hasPrefix("k1max") ? "K1MAX" : ""
    }

    // MARK: - Commands

    func pause() { command("printer.print.pause") }
    func resume() { command("printer.print.resume") }
    func cancel() { command("printer.print.cancel") }

    func setSpeed(percent: Int) { gcode("M220 S\(percent)") }

    func setLight(_ on: Bool) {
        guard let lightObject else {
            onEvent?(.commandFailed("light", String(localized: "The printer has no light Bambu Bar can switch.")))
            return
        }
        let parts = lightObject.split(separator: " ", maxSplits: 1).map(String.init)
        guard parts.count == 2 else { return }
        switch parts[0] {
        case "output_pin": gcode("SET_PIN PIN=\(parts[1]) VALUE=\(on ? 1 : 0)")
        default: gcode("SET_LED LED=\(parts[1]) WHITE=\(on ? 1 : 0)")
        }
    }

    private func gcode(_ script: String) { command("printer.gcode.script", ["script": script]) }

    private func command(_ method: String, _ params: [String: Any] = [:]) {
        call(method, params) { [weak self] result in
            if case .failure(let error) = result { self?.onEvent?(.commandFailed(method, error.message)) }
        }
    }

    // MARK: - Subscription

    /// Lists the printer's objects, picks what to watch and subscribes; again after Klipper restarts.
    func subscribe() {
        resubscribe?.cancel()
        call("printer.objects.list") { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(let value):
                let names = ((value as? [String: Any])?["objects"] as? [String]) ?? []
                self.subscribe(to: names)
            case .failure(let error):
                self.klippyUnavailable(error.message)
            }
        }
    }

    private func subscribe(to available: [String]) {
        lightObject = Self.lightObject(in: available)
        chamberObject = Self.chamberObject(in: available)
        var wanted = ["print_stats", "display_status", "virtual_sdcard", "extruder", "heater_bed", "gcode_move", "webhooks", "fan"]
        // Creality K1: the part, auxiliary and chamber fans are plain PWM pins.
        wanted += ["output_pin fan0", "output_pin fan1", "output_pin fan2"]
        wanted += [lightObject, chamberObject].compactMap { $0 }
        var request: [String: Any] = [:]
        for name in wanted where available.contains(name) { request[name] = NSNull() }
        call("printer.objects.subscribe", ["objects": request]) { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(let value):
                self.objects = [:]
                if let status = (value as? [String: Any])?["status"] as? [String: Any] { self.merge(status) }
                self.publish()
            case .failure(let error):
                self.klippyUnavailable(error.message)
            }
        }
    }

    /// Moonraker is up but Klipper isn't (starting, shut down, firmware error): say so and try again shortly.
    private func klippyUnavailable(_ reason: String) {
        var status = PrinterStatus()
        status.issues = [PrinterIssue(id: "KLIPPY", severity: .fatal,
                                      message: String(localized: "Klipper isn't ready: \(reason)"), wikiURL: nil)]
        onEvent?(.status(status))
        let retry = DispatchWorkItem { [weak self] in self?.subscribe() }
        resubscribe = retry
        DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: retry)
    }

    private func merge(_ status: [String: Any]) {
        guard objects != nil else { return }
        for (name, value) in status {
            guard let fields = value as? [String: Any] else { continue }
            objects?[name, default: [:]].merge(fields) { _, new in new }
        }
    }

    private func publishSoon() {
        guard deferredPublish == nil else { return }
        let wait = 1 - Date().timeIntervalSince(lastPublish)
        guard wait > 0 else { publish(); return }
        let work = DispatchWorkItem { [weak self] in self?.publish() }
        deferredPublish = work
        DispatchQueue.main.asyncAfter(deadline: .now() + wait, execute: work)
    }

    private func publish() {
        deferredPublish?.cancel()
        deferredPublish = nil
        // Creality's status alone (before Klipper's objects are in) would read as preparing.
        guard let objects else { return }
        lastPublish = Date()
        let file = objects["print_stats"]?["filename"] as? String ?? ""
        if file != metadataFile {
            metadataFile = file
            estimatedSeconds = nil
            if !file.isEmpty { loadMetadata(for: file) }
        }
        var status = MoonrakerParser.status(from: objects, light: lightObject, chamber: chamberObject,
                                            estimatedSeconds: estimatedSeconds)
        if let report = creality?.report { MoonrakerParser.fill(&status, fromCreality: report) }
        onEvent?(.status(status))
    }

    private func loadMetadata(for file: String) {
        call("server.files.metadata", ["filename": file]) { [weak self] result in
            guard let self, self.metadataFile == file, case .success(let value) = result,
                  let estimate = MoonrakerParser.double((value as? [String: Any])?["estimated_time"]) else { return }
            self.estimatedSeconds = estimate
            self.publish()
        }
    }

    static func lightObject(in names: [String]) -> String? {
        let preferred = ["output_pin LED", "output_pin led", "output_pin caselight", "output_pin chamber_light",
                         "output_pin light", "led chamber_light", "led caselight"]
        if let match = preferred.first(where: names.contains) { return match }
        return names.first { name in
            let lower = name.lowercased()
            return (lower.hasPrefix("output_pin ") || lower.hasPrefix("led ")) && (lower.contains("light") || lower.contains("led"))
        }
    }

    static func chamberObject(in names: [String]) -> String? {
        for prefix in ["temperature_sensor ", "heater_generic ", "temperature_fan "] {
            if let match = names.first(where: { $0.hasPrefix(prefix) && $0.lowercased().contains("chamber") }) { return match }
        }
        return nil
    }

    // MARK: - JSON-RPC

    private func call(_ method: String, _ params: [String: Any] = [:], completion: @escaping (Result<Any, RPCError>) -> Void) {
        guard let task else { return }
        let id = nextID
        nextID += 1
        var message: [String: Any] = ["jsonrpc": "2.0", "method": method, "id": id]
        if !params.isEmpty { message["params"] = params }
        guard let data = try? JSONSerialization.data(withJSONObject: message),
              let text = String(data: data, encoding: .utf8) else { return }
        pending[id] = completion
        task.send(.string(text)) { [weak self] error in
            guard let error else { return }
            DispatchQueue.main.async { self?.fail(error.localizedDescription) }
        }
    }

    private func receive() {
        task?.receive { [weak self] result in
            DispatchQueue.main.async {
                guard let self, !self.closed else { return }
                switch result {
                case .success(let message):
                    switch message {
                    case .string(let text): self.handle(Data(text.utf8))
                    case .data(let data): self.handle(data)
                    @unknown default: break
                    }
                    self.receive()
                case .failure(let error):
                    self.fail(error.localizedDescription)
                }
            }
        }
    }

    private func handle(_ data: Data) {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        if let id = json["id"] as? Int, let completion = pending.removeValue(forKey: id) {
            if let error = json["error"] as? [String: Any] {
                completion(.failure(RPCError(message: error["message"] as? String ?? "error")))
            } else {
                completion(.success(json["result"] ?? NSNull()))
                onEvent?(.alive)
            }
            return
        }
        switch json["method"] as? String {
        case "notify_status_update":
            guard let params = json["params"] as? [Any], let status = params.first as? [String: Any] else { return }
            merge(status)
            publishSoon()
        case "notify_klippy_ready":
            subscribe()
        case "notify_klippy_shutdown", "notify_klippy_disconnected":
            klippyUnavailable(json["method"] as? String == "notify_klippy_shutdown"
                              ? String(localized: "Klipper shut down.") : String(localized: "Klipper disconnected."))
        default:
            break
        }
    }

    private func fail(_ reason: String) {
        guard !closed else { return }
        closed = true
        resubscribe?.cancel()
        deferredPublish?.cancel()
        session?.invalidateAndCancel()
        pending.removeAll()
        onEvent?(.disconnected(reason))
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        guard !closed else { return }
        subscribe()
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask,
                    didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        fail(String(localized: "The connection to the printer closed."))
    }
}

/// Folds Klipper's objects into the status Bambu printers fill in.
enum MoonrakerParser {
    static func status(from objects: [String: [String: Any]], light: String?, chamber: String?,
                       estimatedSeconds: Double?) -> PrinterStatus {
        var s = PrinterStatus()
        let stats = objects["print_stats"] ?? [:]
        switch stats["state"] as? String {
        case "printing": s.state = .printing
        case "paused": s.state = .paused
        case "complete": s.state = .finished
        case "cancelled": s.state = .failed; s.canceled = true
        case "error": s.state = .failed
        default: s.state = .idle
        }

        let file = stats["filename"] as? String ?? ""
        if !file.isEmpty {
            let base = (file as NSString).lastPathComponent
            s.jobName = base.lowercased().hasSuffix(".gcode") ? String(base.dropLast(6)) : base
            s.taskID = file
        }

        // How far into the file the printer has read, as Fluidd and Mainsail show it; M73 from the slicer only
        // when that's missing (some slicers' M73 runs well ahead of the print).
        let fileProgress = double(objects["virtual_sdcard"]?["progress"]) ?? 0
        let displayProgress = double(objects["display_status"]?["progress"]) ?? 0
        let progress = fileProgress > 0 ? fileProgress : displayProgress
        s.progress = s.state == .finished ? 100 : min(100, Int((progress * 100).rounded(.down)))

        if s.state.isActive {
            let printed = double(stats["print_duration"]) ?? 0
            var remaining: Double = 0
            if let estimatedSeconds, estimatedSeconds > printed {
                remaining = estimatedSeconds - printed
            } else if progress > 0.02, printed > 0 {
                remaining = printed / progress - printed
            }
            s.remainingMinutes = Int((remaining / 60).rounded(.up))
        }

        let info = stats["info"] as? [String: Any]
        s.layer = int(info?["current_layer"]) ?? int(objects["virtual_sdcard"]?["layer"]) ?? 0
        s.totalLayers = int(info?["total_layer"]) ?? int(objects["virtual_sdcard"]?["layer_count"]) ?? 0

        s.nozzle = temperature(objects["extruder"])
        s.bed = temperature(objects["heater_bed"])
        if let chamber, let reading = double(objects[chamber]?["temperature"]) {
            s.chamber = reading
            if chamber.hasPrefix("heater_generic ") { s.chamberTarget = double(objects[chamber]?["target"]) }
        }

        if let fan = double(objects["fan"]?["speed"]) {
            s.partFan = percent(fan)
        } else if let fan0 = double(objects["output_pin fan0"]?["value"]) {
            s.partFan = percent(fan0)
        }
        s.auxFan = double(objects["output_pin fan1"]?["value"]).map(percent)
        s.exhaustFan = double(objects["output_pin fan2"]?["value"]).map(percent)

        if let factor = double(objects["gcode_move"]?["speed_factor"]) {
            let feed = Int((factor * 100).rounded())
            s.speed = SpeedLevel.allCases.min { abs($0.percent - feed) < abs($1.percent - feed) } ?? .standard
        }

        if let light {
            let fields = objects[light] ?? [:]
            if let value = double(fields["value"]) {
                s.chamberLightOn = value > 0
            } else if let color = (fields["color_data"] as? [[Double]])?.first {
                s.chamberLightOn = color.contains { $0 > 0 }
            }
        }

        if let webhooks = objects["webhooks"], let state = webhooks["state"] as? String, state != "ready" {
            let message = webhooks["state_message"] as? String ?? state
            s.issues.append(PrinterIssue(id: "KLIPPY", severity: .fatal, message: message, wikiURL: nil))
        } else if s.state == .failed, !s.canceled, let message = stats["message"] as? String, !message.isEmpty {
            s.issues.append(PrinterIssue(id: "PRINT_ERROR", severity: .serious, message: message, wikiURL: nil))
        }
        return s
    }

    /// A Creality K1 runs its self-test, levelling and heating before it hands the file to Klipper, so Klipper
    /// reports standby through all of it. The printer's own status (`state` 1 = printing) knows better.
    static func fill(_ s: inout PrinterStatus, fromCreality report: [String: Any]) {
        guard int(report["state"]) == 1 else { return }
        if s.state == .idle {
            s.state = .preparing
            if let file = report["printFileName"] as? String, !file.isEmpty {
                let base = (file as NSString).lastPathComponent
                s.jobName = base.lowercased().hasSuffix(".gcode") ? String(base.dropLast(6)) : base
                s.taskID = file
            }
        }
        // The printer's own progress, time left and layers, as its screen shows them.
        if let progress = int(report["printProgress"]) { s.progress = min(100, max(0, progress)) }
        if let left = int(report["printLeftTime"]), left > 0 { s.remainingMinutes = Int((Double(left) / 60).rounded(.up)) }
        if let total = int(report["TotalLayer"]), total > 0 {
            s.totalLayers = total
            s.layer = int(report["layer"]) ?? s.layer
        }
    }

    private static func temperature(_ fields: [String: Any]?) -> Temperature {
        Temperature(current: double(fields?["temperature"]) ?? 0, target: double(fields?["target"]) ?? 0)
    }

    private static func percent(_ fraction: Double) -> Int { Int((fraction * 100).rounded()) }

    static func int(_ value: Any?) -> Int? {
        switch value {
        case let v as Int: return v
        case let v as Double: return Int(v)
        case let v as NSNumber: return v.intValue
        default: return nil
        }
    }

    static func double(_ value: Any?) -> Double? {
        switch value {
        case let v as Double: return v
        case let v as Int: return Double(v)
        case let v as NSNumber: return v.doubleValue
        default: return nil
        }
    }
}

/// Listens to a Creality K1's stock WebSocket (`ws://<host>:9999`): it sends its whole status on connect and then
/// the keys that changed. Only listened to, never written; it reconnects quietly, since Moonraker carries the rest.
final class CrealityStateWatcher: NSObject, URLSessionWebSocketDelegate {
    private(set) var report: [String: Any]?
    var onChange: (() -> Void)?

    private let url: URL
    private var session: URLSession?
    private var task: URLSessionWebSocketTask?
    private var stopped = false
    private var retry: DispatchWorkItem?

    init(host: String) {
        url = URL(string: "ws://\(host):9999")!
    }

    func connect() {
        guard !stopped else { return }
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 15
        let session = URLSession(configuration: config, delegate: self, delegateQueue: .main)
        let task = session.webSocketTask(with: url)
        task.maximumMessageSize = 4 << 20
        self.session = session
        self.task = task
        task.resume()
        receive(task)
    }

    func disconnect() {
        stopped = true
        retry?.cancel()
        task?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
        task = nil
        session = nil
    }

    private func receive(_ task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            DispatchQueue.main.async {
                guard let self, !self.stopped, task === self.task else { return }
                switch result {
                case .success(let message):
                    var data: Data?
                    switch message {
                    case .string(let text): data = Data(text.utf8)
                    case .data(let bytes): data = bytes
                    @unknown default: break
                    }
                    if let data, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                        self.report = (self.report ?? [:]).merging(json) { _, new in new }
                        self.onChange?()
                    }
                    self.receive(task)
                case .failure:
                    self.scheduleReconnect()
                }
            }
        }
    }

    private func scheduleReconnect() {
        session?.invalidateAndCancel()
        task = nil
        session = nil
        report = nil
        onChange?()
        let work = DispatchWorkItem { [weak self] in self?.connect() }
        retry = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 10, execute: work)
    }
}
