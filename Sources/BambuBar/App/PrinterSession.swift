import Foundation
import Observation

/// One configured printer: its connection, live status and the commands in flight.
/// `PrinterStore` owns the sessions and handles what spans printers (history, notifications, widgets).
@MainActor
@Observable
final class PrinterSession: Identifiable {
    /// The serial number, which never changes for a session.
    nonisolated let id: String
    var config: PrinterConfig
    private(set) var phase: PrinterConnection.Phase = .idle
    private(set) var status: PrinterStatus?
    /// Commands sent but not yet reflected in a report, so the controls respond immediately.
    private(set) var pendingLight: Bool?
    private(set) var pendingSpeed: SpeedLevel?
    /// More codes to try if the printer rejects the current one (a slicer may hold an old and a new one).
    var fallbackCodes: [String] = []
    /// False until the first report after connecting, so state already in progress doesn't notify.
    var hasBaseline = false
    /// When the printer last sent anything, to the second ("Updated 3s ago").
    private(set) var lastHeard: Date?

    var onPhase: ((PrinterSession, PrinterConnection.Phase) -> Void)?
    var onStatus: ((PrinterSession, PrinterStatus?, PrinterStatus, Bool) -> Void)?
    var onAck: ((PrinterSession, String, Bool, String?) -> Void)?
    var onHeartbeat: ((PrinterSession) -> Void)?

    @ObservationIgnored private let connection = PrinterConnection()
    @ObservationIgnored private var demo: DemoDriver?
    @ObservationIgnored private var insecure = false
    @ObservationIgnored private var pendingTask: Task<Void, Never>?
    /// The code the connection is using (with `--connect` it never reaches the Keychain).
    @ObservationIgnored private var activeCode: String?
    /// The printer's camera; streams only while a view shows it.
    @ObservationIgnored private(set) var camera: CameraFeed!

    init(config: PrinterConfig) {
        id = config.serial
        self.config = config
        connection.onPhase = { [weak self] phase in self?.phaseChanged(phase) }
        connection.onStatus = { [weak self] status, doc in self?.received(status, canceled: ReportParser.wasCanceled(doc)) }
        connection.onAck = { [weak self] command, ok, reason in
            guard let self else { return }
            if !ok { self.pendingLight = nil; self.pendingSpeed = nil }
            self.onAck?(self, command, ok, reason)
        }
        connection.onHeartbeat = { [weak self] in
            guard let self else { return }
            // Whole seconds are enough for the label and spare the UI an update per message.
            if self.lastHeard.map({ Date().timeIntervalSince($0) >= 1 }) ?? true { self.lastHeard = Date() }
            self.onHeartbeat?(self)
        }
        camera = CameraFeed(session: self)
    }

    // MARK: - Lifecycle

    func start(accessCode: String, alternatives: [String] = [], insecure: Bool) {
        self.insecure = insecure
        fallbackCodes = alternatives.filter { $0 != accessCode }
        status = nil
        hasBaseline = false
        activeCode = accessCode
        connection.start(config, accessCode: accessCode, insecure: insecure)
    }

    /// Runs a simulated printer instead of a connection (`--demo`).
    func startDemo(_ scenario: DemoDriver.Scenario, variant: Int) {
        let driver = DemoDriver(scenario: scenario, variant: variant)
        driver.onStatus = { [weak self] status, canceled in self?.received(status, canceled: canceled) }
        driver.onPhase = { [weak self] phase in self?.phaseChanged(phase) }
        demo = driver
        driver.start()
    }

    /// Shows a scripted status, as if the printer had reported it (README frames).
    func showDemoStatus(_ status: PrinterStatus) {
        received(status, canceled: false)
    }

    func stop() {
        camera.stop()
        connection.stop()
        phase = .idle
    }

    func refresh() { connection.refresh() }

    /// Reconnects with the stored code, e.g. after the printer moved to a new IP address.
    func restart() {
        guard demo == nil, let code = Keychain.accessCode(for: config.serial) else { return }
        activeCode = code
        connection.start(config, accessCode: code, insecure: insecure)
        camera.restart()
    }

    // MARK: - Derived

    /// A live status is on hand: connected, or briefly reconnecting after a drop.
    var isOnline: Bool { status != nil && (phase == .online || phase == .connecting) }
    var chamberLightOn: Bool { pendingLight ?? status?.chamberLightOn ?? false }
    var speed: SpeedLevel { pendingSpeed ?? status?.speed ?? .standard }
    var accessCode: String? { Keychain.accessCode(for: config.serial) }
    var isDemo: Bool { demo != nil }
    var allowsInsecureTLS: Bool { insecure }
    var cameraTransport: CameraTransport { CameraTransport.forModel(config.modelName) }
    var cameraAccessCode: String? { activeCode ?? accessCode }

    var linkSummary: String {
        switch phase {
        case .idle: return "Not connected"
        case .connecting: return "Connecting…"
        case .online: return "Connected"
        case .offline: return "Offline"
        case .unauthorized: return "Access code rejected"
        case .certificate: return "Certificate not trusted"
        case .localNetworkDenied: return "No local network access"
        }
    }

    var linkDetail: String? {
        switch phase {
        case .offline(let reason): return reason
        case .unauthorized: return "Check the access code on the printer's screen (Settings → Network) and enter it again."
        case .certificate(let reason): return "The printer's TLS certificate couldn't be verified: \(reason)"
        case .localNetworkDenied: return LocalNetwork.advice
        default: return nil
        }
    }

    /// "Printing · 64%", "Ready", "Offline" — one line for menus and lists.
    var shortState: String {
        guard let s = status, isOnline else { return linkSummary }
        return s.state.isActive ? "\(s.state.title) · \(s.progress)%" : s.stateTitle
    }

    // MARK: - Commands

    func pause() { if let demo { demo.pause() } else { connection.pause() } }
    func resume() { if let demo { demo.resume() } else { connection.resume() } }
    func stopPrint() { if let demo { demo.stop() } else { connection.stopPrint() } }

    func toggleLight() {
        let on = !chamberLightOn
        pendingLight = on
        if let demo { demo.setLight(on) } else { connection.setChamberLight(on) }
        clearPendingSoon()
    }

    func setSpeed(_ level: SpeedLevel) {
        pendingSpeed = level
        if let demo { demo.setSpeed(level) } else { connection.setSpeed(level) }
        clearPendingSoon()
    }

    private func clearPendingSoon() {
        pendingTask?.cancel()
        pendingTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(6))
            guard !Task.isCancelled else { return }
            self?.pendingLight = nil
            self?.pendingSpeed = nil
        }
    }

    // MARK: - Updates

    private func phaseChanged(_ phase: PrinterConnection.Phase) {
        if phase == .unauthorized, !fallbackCodes.isEmpty {
            // Try the slicer's other stored code before telling anyone.
            let next = fallbackCodes.removeFirst()
            Keychain.setAccessCode(next, for: config.serial)
            activeCode = next
            connection.start(config, accessCode: next, insecure: insecure)
            return
        }
        if phase == .online { fallbackCodes = [] }
        self.phase = phase
        onPhase?(self, phase)
        camera.update()
    }

    private func received(_ reported: PrinterStatus, canceled: Bool) {
        var new = reported
        new.fit(to: config.model)
        let old = status
        status = new
        lastHeard = Date()
        if let light = pendingLight, new.chamberLightOn == light { pendingLight = nil }
        if let level = pendingSpeed, new.speed == level { pendingSpeed = nil }
        onStatus?(self, old, new, canceled)
        hasBaseline = true
        // A print starting or ending can keep the camera on or let it go (Settings → Camera).
        if old?.state.isActive != new.state.isActive { camera.update() }
    }
}
