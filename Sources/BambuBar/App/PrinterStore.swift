import AppKit
import Observation
import WidgetKit

/// What the menu bar shows next to the icon.
enum MenuBarStyle: String, CaseIterable, Identifiable {
    case icon, percent, remaining, both
    var id: String { rawValue }
    var title: String {
        switch self {
        case .icon: return "Icon only"
        case .percent: return "Progress"
        case .remaining: return "Time left"
        case .both: return "Progress and time left"
        }
    }
}

/// The app's single source of truth: the printers, which one is in view, job history and settings.
///
/// The window and the menu bar panel show the *selected* printer; the members below without a session
/// argument (`status`, `pause()`…) act on it. The menu bar icon shows `menuBarSession`, which follows
/// the Settings choice ("Automatic" picks whichever printer is busy).
@MainActor
@Observable
final class PrinterStore {
    static let shared = PrinterStore()

    private(set) var sessions: [PrinterSession] = []
    private(set) var selectedSerial: String?
    private(set) var history: [JobRecord] = []
    private(set) var discovered: [DiscoveredPrinter] = []
    /// The search for printers can't get out: macOS's Local Network permission is off or not answered yet.
    private(set) var discoveryBlocked = false
    /// Printers whose access code a slicer already knows (this Mac's Bambu Studio or OrcaSlicer, or a settings
    /// file imported from another Mac this session).
    private(set) var slicerPrinters: [SlicerPrinter] = []
    private var importedPrinters: [SlicerPrinter] = []
    /// A short-lived message after a command ("The printer refused…").
    private(set) var toast: Toast?
    private(set) var isDemo = false

    /// `nil` = automatic.
    var menuBarSerial: String? {
        didSet { defaults.set(menuBarSerial, forKey: Keys.menuBarPrinter); writeSnapshot(force: true) }
    }
    var menuBarStyle: MenuBarStyle {
        didSet { defaults.set(menuBarStyle.rawValue, forKey: Keys.menuBarStyle) }
    }
    var notifyFinished: Bool { didSet { defaults.set(notifyFinished, forKey: Keys.notifyFinished); requestNotificationsIfNeeded() } }
    var notifyFailed: Bool { didSet { defaults.set(notifyFailed, forKey: Keys.notifyFailed); requestNotificationsIfNeeded() } }
    var notifyPaused: Bool { didSet { defaults.set(notifyPaused, forKey: Keys.notifyPaused); requestNotificationsIfNeeded() } }
    var notifyStarted: Bool { didSet { defaults.set(notifyStarted, forKey: Keys.notifyStarted); requestNotificationsIfNeeded() } }
    var confirmStop: Bool { didSet { defaults.set(confirmStop, forKey: Keys.confirmStop) } }
    /// Keep the camera streaming for the whole of a print, so it shows at once. Off: only while it's on screen.
    var cameraStaysOnWhilePrinting: Bool {
        didSet {
            defaults.set(cameraStaysOnWhilePrinting, forKey: Keys.cameraStaysOnWhilePrinting)
            for session in sessions { session.camera.viewersChanged() }
        }
    }

    struct Toast: Equatable, Identifiable {
        let id = UUID()
        var message: String
        var isError: Bool
    }

    private let defaults = UserDefaults.standard
    private let discovery = PrinterDiscovery()
    private let notifier = Notifier()
    private var lastWidgetReload = Date.distantPast
    private var lastSnapshotWrite = Date.distantPast
    private var lastSavedKey = ""
    private var lastWidgetKey = ""
    private var toastTask: Task<Void, Never>?
    private var insecureTLS = CommandLine.arguments.contains("--insecure-tls")

    /// `--emulator <BambuStudio.conf> [--add-all]`: the mock fleet from scripts/fleet. Its printers come from the
    /// settings file it writes, as if Bambu Studio had saved them; nothing is saved, not even the access codes.
    static var emulatorSettings: URL? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: "--emulator"), i + 1 < a.count else { return nil }
        return URL(fileURLWithPath: (a[i + 1] as NSString).expandingTildeInPath)
    }

    static var developerConnection: (host: String, serial: String, code: String)? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: "--connect"), i + 3 < a.count else { return nil }
        return (a[i + 1], a[i + 2], a[i + 3])
    }

    private enum Keys {
        static let printers = "printers"
        static let legacyPrinter = "printerConfig"
        static let selected = "selectedPrinter"
        static let menuBarPrinter = "menuBarPrinter"
        static let menuBarStyle = "menuBarStyle"
        static let notifyFinished = "notifyFinished"
        static let notifyFailed = "notifyFailed"
        static let notifyPaused = "notifyPaused"
        static let notifyStarted = "notifyStarted"
        static let confirmStop = "confirmStop"
        static let cameraStaysOnWhilePrinting = "cameraStaysOnWhilePrinting"
    }

    private init() {
        menuBarStyle = MenuBarStyle(rawValue: defaults.string(forKey: Keys.menuBarStyle) ?? "") ?? .both
        notifyFinished = defaults.object(forKey: Keys.notifyFinished) as? Bool ?? true
        notifyFailed = defaults.object(forKey: Keys.notifyFailed) as? Bool ?? true
        notifyPaused = defaults.object(forKey: Keys.notifyPaused) as? Bool ?? true
        notifyStarted = defaults.object(forKey: Keys.notifyStarted) as? Bool ?? false
        confirmStop = defaults.object(forKey: Keys.confirmStop) as? Bool ?? true
        cameraStaysOnWhilePrinting = defaults.bool(forKey: Keys.cameraStaysOnWhilePrinting)
        menuBarSerial = defaults.string(forKey: Keys.menuBarPrinter)
        selectedSerial = defaults.string(forKey: Keys.selected)
    }

    // MARK: - Lifecycle

    func start() {
        discovery.onFound = { [weak self] printer in self?.found(printer) }
        discovery.onBlocked = { [weak self] blocked in self?.discoveryBlockedChanged(blocked) }

        if let scenario = DemoDriver.requestedScenario {
            startDemo(scenario)
            return
        }
        if let dev = Self.developerConnection {
            // `--connect <host> <serial> <code>`: a throwaway session (the local emulator, a borrowed printer).
            // Nothing is saved: not the printer, not the access code, not the history.
            isDemo = true
            // The model follows the serial's prefix (a P2S if it's not one Bambu Lab uses).
            let model = PrinterModel.find(code: "", serial: dev.serial) ?? PrinterModel.find(code: "N7")
            let session = add(PrinterConfig(host: dev.host, serial: dev.serial, name: "\(model?.name ?? "P2S") Emulator",
                                            modelCode: model?.code ?? "N7"))
            session.start(accessCode: dev.code, insecure: insecureTLS)
            writeSnapshot(force: true)
            return
        }

        if let settings = Self.emulatorSettings {
            isDemo = true
            insecureTLS = true
            discovery.start()
            importSlicerSettings(from: settings)
            if CommandLine.arguments.contains("--add-all") {
                // Waits a little for the fleet's announcements, which carry the printers' names and models.
                Task { [weak self] in
                    for _ in 0..<16 {
                        guard let self else { return }
                        if self.slicerPrinters.allSatisfy({ p in self.discovered.contains { $0.serial == p.serial } }) { break }
                        try? await Task.sleep(for: .milliseconds(500))
                    }
                    guard let self else { return }
                    self.addAll(self.slicerPrinters)
                }
            }
            return
        }

        for config in loadConfigs() {
            let session = add(config)
            if let code = Keychain.accessCode(for: config.serial) {
                session.start(accessCode: code, insecure: insecureTLS)
            }
        }
        history = HistoryStore.load()
        // History from the single-printer versions belongs to the printer that was set up then.
        if let first = sessions.first, history.contains(where: { $0.printerSerial == nil }) {
            for i in history.indices where history[i].printerSerial == nil { history[i].printerSerial = first.id }
            HistoryStore.save(history)
        }
        if selectedSerial == nil || session(selectedSerial!) == nil { selectedSerial = sessions.first?.id }
        discovery.start()
        reloadSlicerPrinters()
        writeSnapshot(force: true)
    }

    func stop() {
        for session in sessions { session.stop() }
        discovery.stop()
    }

    private func loadConfigs() -> [PrinterConfig] {
        if let data = defaults.data(forKey: Keys.printers),
           let configs = try? JSONDecoder().decode([PrinterConfig].self, from: data) {
            return configs
        }
        // Versions before multi-printer support saved one printer under another key.
        if let data = defaults.data(forKey: Keys.legacyPrinter),
           let config = try? JSONDecoder().decode(PrinterConfig.self, from: data) {
            defaults.removeObject(forKey: Keys.legacyPrinter)
            if let data = try? JSONEncoder().encode([config]) { defaults.set(data, forKey: Keys.printers) }
            return [config]
        }
        return []
    }

    private func saveConfigs() {
        guard !isDemo else { return }
        if let data = try? JSONEncoder().encode(sessions.map(\.config)) { defaults.set(data, forKey: Keys.printers) }
    }

    @discardableResult
    private func add(_ config: PrinterConfig) -> PrinterSession {
        let session = PrinterSession(config: config)
        session.onPhase = { [weak self] session, phase in
            // Reconnect attempts flip between offline and connecting; only settled states earn a widget reload.
            self?.writeSnapshot(force: phase == .online || phase == .unauthorized || phase == .idle)
        }
        session.onStatus = { [weak self] session, old, new, canceled in
            self?.trackJob(session, from: old, to: new, canceled: canceled)
            self?.writeSnapshot(force: old?.state != new.state)
        }
        session.onAck = { [weak self] session, command, ok, reason in self?.acknowledged(session, ok: ok, reason: reason) }
        session.onHeartbeat = { [weak self] _ in
            // A quiet, unchanging printer is still alive; keep the widget's snapshot from looking stale.
            guard let self, Date().timeIntervalSince(self.lastSnapshotWrite) > 300 else { return }
            self.writeSnapshot(force: false)
        }
        sessions.append(session)
        return session
    }

    // MARK: - Printers

    func session(_ serial: String) -> PrinterSession? { sessions.first { $0.id == serial } }

    /// The printer the window and panel show.
    var current: PrinterSession? { selectedSerial.flatMap(session) ?? sessions.first }

    func select(_ serial: String) {
        guard session(serial) != nil, selectedSerial != serial else { return }
        selectedSerial = serial
        if !isDemo { defaults.set(serial, forKey: Keys.selected) }
    }

    /// The printer the menu bar icon follows: the one chosen in Settings, or automatically the busiest.
    var menuBarSession: PrinterSession? {
        if let menuBarSerial, let chosen = session(menuBarSerial) { return chosen }
        func rank(_ s: PrinterSession) -> Int {
            guard let status = s.status, s.isOnline else { return 0 }
            if status.mostSevereIssue.map({ $0.severity <= .serious }) == true && status.state.isActive { return 4 }
            switch status.state {
            case .paused: return 4
            case .printing, .preparing: return 3
            case .failed, .finished: return 1
            case .idle: return 0
            }
        }
        let best = sessions.max { rank($0) < rank($1) }
        if let best, rank(best) >= 3 { return best }
        return current
    }

    var hasMultiplePrinters: Bool { sessions.count > 1 }

    /// Saves a printer (new, or a new address/code for a known one) and connects to it.
    func connect(to config: PrinterConfig, accessCode: String, alternatives: [String] = []) {
        let code = accessCode.trimmingCharacters(in: .whitespacesAndNewlines)
        if !isDemo { Keychain.setAccessCode(code, for: config.serial) }
        let session: PrinterSession
        if let existing = self.session(config.serial) {
            existing.config = config
            session = existing
        } else {
            session = add(config)
        }
        saveConfigs()
        select(config.serial)
        session.start(accessCode: code, alternatives: alternatives, insecure: insecureTLS)
        writeSnapshot(force: true)
    }

    /// Slicer printers that can be added right away: not set up yet, with an address from the network or the
    /// slicer's settings.
    var readySlicerPrinters: [SlicerPrinter] {
        slicerPrinters.filter { session($0.serial) == nil && (host(for: $0) != nil) }
    }

    private func host(for printer: SlicerPrinter) -> String? {
        discovered.first { $0.serial == printer.serial }?.host ?? printer.host
    }

    /// Adds every printer a slicer knows in one go, named and typed from their network announcements when
    /// they've been seen, else after their model and serial ("P2S 495").
    func addAll(_ printers: [SlicerPrinter]) {
        var added: [PrinterSession] = []
        for printer in printers.sorted(by: { name(for: $0).localizedStandardCompare(name(for: $1)) == .orderedAscending })
        where session(printer.serial) == nil {
            guard let host = host(for: printer), let code = printer.accessCodes.first else { continue }
            let seen = discovered.first { $0.serial == printer.serial }
            let config = PrinterConfig(host: host, serial: printer.serial.uppercased(), name: name(for: printer),
                                       modelCode: seen?.modelCode ?? "")
            if !isDemo { Keychain.setAccessCode(code, for: config.serial) }
            let session = add(config)
            session.start(accessCode: code, alternatives: Array(printer.accessCodes.dropFirst()), insecure: insecureTLS)
            added.append(session)
        }
        guard !added.isEmpty else { return }
        saveConfigs()
        if selectedSerial == nil || session(selectedSerial!) == nil { select(added[0].id) }
        writeSnapshot(force: true)
    }

    private func name(for printer: SlicerPrinter) -> String {
        let seen = discovered.first { $0.serial == printer.serial }
        if let name = seen?.name, !name.isEmpty, name != printer.serial { return name }
        let model = PrinterModel.find(code: seen?.modelCode ?? "", serial: printer.serial)
        return "\(model?.name ?? "Printer") \(printer.serial.suffix(3))"
    }

    /// The name the printer announces on the network (set in Bambu Handy or Bambu Studio), when it has been seen.
    func announcedName(for serial: String) -> String? {
        guard let name = discovered.first(where: { $0.serial == serial })?.name, !name.isEmpty, name != serial else { return nil }
        return name
    }

    func rename(_ serial: String, to name: String) {
        guard let session = session(serial) else { return }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        session.config.name = trimmed.isEmpty ? serial : trimmed
        saveConfigs()
        writeSnapshot(force: true)
    }

    func forget(_ serial: String) {
        guard let session = session(serial) else { return }
        session.stop()
        Keychain.removeAccessCode(for: serial)
        sessions.removeAll { $0.id == serial }
        if selectedSerial == serial { selectedSerial = sessions.first?.id }
        if menuBarSerial == serial { menuBarSerial = nil }
        saveConfigs()
        writeSnapshot(force: true)
    }

    func refresh() {
        discovery.search()
        current?.refresh()
    }

    func searchForPrinters() {
        discovery.search()
        reloadSlicerPrinters()
    }

    private func found(_ printer: DiscoveredPrinter) {
        if let i = discovered.firstIndex(where: { $0.serial == printer.serial }) {
            discovered[i] = printer
        } else {
            discovered.append(printer)
        }
        // Follow a printer when DHCP hands it a new address.
        if let session = session(printer.serial), session.config.host != printer.host, !isDemo {
            session.config.host = printer.host
            saveConfigs()
            session.restart()
        }
    }

    /// Local network access is off, as far as the search or any connection can tell.
    var localNetworkBlocked: Bool { discoveryBlocked || sessions.contains { $0.phase == .localNetworkDenied } }

    private func discoveryBlockedChanged(_ blocked: Bool) {
        discoveryBlocked = blocked
        // Access just came back: reconnect now rather than at the end of the back-off.
        if !blocked { for session in sessions where session.phase == .localNetworkDenied { session.refresh() } }
    }

    /// The serial of the printer at `host` (an IP address, or `host:port` for a mock printer), read from its certificate.
    func identifyPrinter(at host: String) async -> Result<String, PrinterIdentity.Failure> {
        let endpoint = PrinterConfig(host: host, serial: "", name: "", modelCode: "").endpoint
        return await PrinterIdentity.serial(host: endpoint.host, port: endpoint.port, insecure: insecureTLS)
    }

    // MARK: - Access codes from slicers

    /// Rescans this Mac's slicer settings.
    func reloadSlicerPrinters() {
        Task.detached(priority: .utility) {
            let scanned = SlicerImport.scan()
            await MainActor.run { [weak self] in
                guard let self else { return }
                var all = self.importedPrinters
                for printer in scanned where !all.contains(where: { $0.serial == printer.serial }) { all.append(printer) }
                if all != self.slicerPrinters { self.slicerPrinters = all }
            }
        }
    }

    /// Reads a `BambuStudio.conf` / `OrcaSlicer.conf` the person picked or dropped (say, copied from the Mac
    /// that runs the slicer). Returns how many printers it knew; they're kept for this session only.
    @discardableResult
    func importSlicerSettings(from url: URL) -> Int {
        let found = SlicerImport.read(picked: url)
        for printer in found {
            importedPrinters.removeAll { $0.serial == printer.serial }
            importedPrinters.insert(printer, at: 0)
        }
        var all = importedPrinters
        for printer in slicerPrinters where !all.contains(where: { $0.serial == printer.serial }) { all.append(printer) }
        slicerPrinters = all
        return found.count
    }

    func slicerPrinter(serial: String) -> SlicerPrinter? {
        slicerPrinters.first { $0.serial.caseInsensitiveCompare(serial) == .orderedSame }
    }

    /// A slicer code for the current printer that differs from the one that was just rejected.
    var replacementCode: SlicerPrinter? {
        guard let current, let printer = slicerPrinter(serial: current.id),
              printer.accessCodes.contains(where: { $0 != current.accessCode }) else { return nil }
        return printer
    }

    func useReplacementCode() {
        guard let current, let printer = replacementCode else { return }
        let codes = printer.accessCodes.filter { $0 != current.accessCode }
        connect(to: current.config, accessCode: codes[0], alternatives: Array(codes.dropFirst()))
    }

    // MARK: - The current printer

    var isConfigured: Bool { !sessions.isEmpty }
    var config: PrinterConfig? { current?.config }
    var phase: PrinterConnection.Phase { current?.phase ?? .idle }
    var status: PrinterStatus? { current?.status }
    var isOnline: Bool { current?.isOnline ?? false }
    var printerName: String { current?.config.name ?? "Printer" }
    var chamberLightOn: Bool { current?.chamberLightOn ?? false }
    var speed: SpeedLevel { current?.speed ?? .standard }
    var accessCode: String? { current?.accessCode }
    var linkSummary: String { current?.linkSummary ?? "No printer set up" }
    var linkDetail: String? { current?.linkDetail }

    /// The current printer's most recent print (for "Last: …" and "finished 3h ago").
    var lastJob: JobRecord? { history(for: current?.id).first }

    func history(for serial: String?) -> [JobRecord] {
        guard let serial else { return [] }
        return history.filter { $0.printerSerial == serial }
    }

    // MARK: - Commands (current printer)

    func pause() {
        guard let current, ensureControllable(current) else { return }
        current.pause()
        showToast("Pausing…")
    }

    func resume() {
        guard let current, ensureControllable(current) else { return }
        current.resume()
        showToast("Resuming…")
    }

    func stopPrint() {
        guard let current, ensureControllable(current) else { return }
        if confirmStop {
            let job = current.status?.jobName ?? ""
            let alert = NSAlert()
            alert.messageText = "Stop “\(job.isEmpty ? "this print" : job)”?"
            alert.informativeText = (hasMultiplePrinters ? "On \(current.config.name). " : "") + "The print can't be resumed after it's stopped."
            alert.alertStyle = .warning
            alert.addButton(withTitle: "Stop Print")
            alert.addButton(withTitle: "Keep Printing")
            alert.buttons.first?.hasDestructiveAction = true
            NSApp.activate(ignoringOtherApps: true)
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        current.stopPrint()
        showToast("Stopping…")
    }

    func toggleLight() { current?.toggleLight() }

    func setSpeed(_ level: SpeedLevel) {
        guard let current, ensureControllable(current) else { return }
        current.setSpeed(level)
    }

    /// Print commands need the printer in Developer Mode; say so instead of sending something it will refuse.
    private func ensureControllable(_ session: PrinterSession) -> Bool {
        if session.status?.controlsLocked == true {
            showToast("Turn on LAN Only and Developer Mode on the printer to control prints from Bambu Bar.", error: true)
            return false
        }
        return true
    }

    private func acknowledged(_ session: PrinterSession, ok: Bool, reason: String?) {
        guard !ok else { return }
        let detail = (reason ?? "").lowercased().contains("verify")
            ? "The printer only accepts commands in Developer Mode."
            : (reason.map { "The printer refused: \($0)" } ?? "The printer refused the command.")
        showToast(hasMultiplePrinters ? "\(session.config.name): \(detail)" : detail, error: true)
    }

    func showToast(_ message: String, error: Bool = false) {
        toast = Toast(message: message, isError: error)
        toastTask?.cancel()
        toastTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(error ? 6 : 3))
            guard !Task.isCancelled else { return }
            self?.toast = nil
        }
    }

    // MARK: - Recent prints and notifications

    private func trackJob(_ session: PrinterSession, from old: PrinterStatus?, to new: PrinterStatus, canceled: Bool) {
        let serial = session.id
        // Simulated printers never post real notifications.
        let notify = session.hasBaseline && !isDemo
        let name = new.jobName.isEmpty ? "Untitled print" : new.jobName
        let spool = new.activeSpool
        // Notification titles name the printer once there's more than one.
        let prefix = hasMultiplePrinters ? "\(session.config.name): " : ""
        let running = history.firstIndex { $0.printerSerial == serial && $0.outcome == .running }

        // Start of a job, or the first report of one already running.
        if new.state.isActive && (old == nil || !(old!.state.isActive)) {
            let current = running.map { history[$0] }
            let sameJob = current?.name == name
                && (current?.taskID == nil || new.taskID == nil || current?.taskID == new.taskID)
            if !sameJob {
                // Anything still marked running on this printer ended while the app wasn't looking.
                for i in history.indices where history[i].printerSerial == serial && history[i].outcome == .running {
                    history[i].outcome = .ended
                }
                let elapsed = estimateElapsed(new)
                history.insert(JobRecord(name: name, startedAt: Date().addingTimeInterval(-elapsed), endedAt: nil,
                                         outcome: .running, totalLayers: new.totalLayers,
                                         filamentColor: spool?.color, filamentMaterial: spool?.material,
                                         taskID: new.taskID, printerSerial: serial), at: 0)
                HistoryStore.save(history)
            }
            if notify, old != nil, notifyStarted {
                notifier.post(title: prefix + "Print started", body: name, id: "start.\(serial)")
            }
        }
        if let index = history.firstIndex(where: { $0.printerSerial == serial && $0.outcome == .running }) {
            var job = history[index]
            // Keep details fresh while it runs (the name arrives late on printer-started jobs).
            job.name = name
            if new.totalLayers > 0 { job.totalLayers = new.totalLayers }
            if let spool { job.filamentColor = spool.color; job.filamentMaterial = spool.material }
            if !new.state.isActive {
                job.endedAt = Date()
                switch new.state {
                case .finished: job.outcome = .finished
                case .failed: job.outcome = canceled ? .canceled : .failed
                // Straight back to idle without FINISH: stopped from the printer, or the report was missed.
                default: job.outcome = canceled ? .canceled : .ended
                }
                if notify { notifyEnd(job, status: new, prefix: prefix, serial: serial) }
            }
            if job != history[index] {
                history[index] = job
                HistoryStore.save(history)
            }
        }
        if notify, let old, old.state == .printing || old.state == .preparing, new.state == .paused, notifyPaused {
            notifier.post(title: prefix + "Print paused", body: "\(name): \(new.detailText)", id: "pause.\(serial)")
        }
        if notify, let old {
            let fresh = new.issues.filter { issue in issue.severity <= .serious && !old.issues.contains { $0.id == issue.id } }
            if let issue = fresh.first, notifyFailed || notifyPaused {
                notifier.post(title: "\(session.config.name): \(issue.severity.title)", body: issue.message, id: "\(issue.id).\(serial)")
            }
        }
    }

    private func notifyEnd(_ job: JobRecord, status: PrinterStatus, prefix: String, serial: String) {
        let duration = job.duration.map { " in \(Format.duration(seconds: $0))" } ?? ""
        switch job.outcome {
        case .finished where notifyFinished:
            notifier.post(title: prefix + "Print finished", body: "\(job.name) finished\(duration). It's ready to take off the plate.", id: "end.\(serial)")
        case .failed where notifyFailed:
            notifier.post(title: prefix + "Print failed", body: "\(job.name): \(status.issues.first?.message ?? "the printer stopped the job.")", id: "end.\(serial)")
        case .canceled where notifyFailed:
            notifier.post(title: prefix + "Print canceled", body: job.name, id: "end.\(serial)")
        default:
            break
        }
    }

    private func estimateElapsed(_ s: PrinterStatus) -> TimeInterval {
        guard s.progress > 0, s.progress < 100, s.remainingMinutes > 0 else { return 0 }
        return Double(s.remainingMinutes * 60) * Double(s.progress) / Double(100 - s.progress)
    }

    private func requestNotificationsIfNeeded() {
        if notifyFinished || notifyFailed || notifyPaused || notifyStarted { notifier.requestAuthorization() }
    }

    // MARK: - Widget

    func snapshot(of session: PrinterSession) -> SharedSnapshot {
        let link: SharedSnapshot.Link
        switch session.phase {
        case .online: link = .online
        case .connecting: link = session.status == nil ? .connecting : .online
        case .unauthorized: link = .unauthorized
        case .idle, .offline, .certificate, .localNetworkDenied: link = .offline
        }
        return SharedSnapshot(serial: session.id, printerName: session.config.name, model: session.config.modelName,
                              link: link, status: session.status,
                              lastJob: history.first { $0.printerSerial == session.id && $0.outcome != .running },
                              savedAt: Date())
    }

    /// The current printer as the widget would show it (for the gallery in Settings).
    var snapshot: SharedSnapshot { current.map(snapshot(of:)) ?? .unconfigured() }

    var fleet: SharedFleet {
        SharedFleet(printers: sessions.map(snapshot(of:)), menuBarSerial: menuBarSession?.id, savedAt: Date())
    }

    /// Writes the widgets' snapshot and asks WidgetKit to reload when something a person would notice changed.
    /// Widget timelines extrapolate progress on their own, so routine progress needs only occasional reloads.
    private func writeSnapshot(force: Bool) {
        // Demo and developer sessions must never show up in the real widgets.
        guard !isDemo else { return }
        let fleet = fleet
        let key = fleet.printers.map { snap -> String in
            let s = snap.status
            return "\(snap.serial)|\(snap.link)|\(snap.printerName)|\(s?.state.rawValue ?? "-")|\(s?.stageID ?? -9)|\((s?.progress ?? 0) / 5)|\(s?.jobName ?? "")|\(s?.issues.count ?? 0)|\(s?.activeSpoolID ?? -1)|\(s?.ams.flatMap(\.spools).map(\.color).joined() ?? "")"
        }.joined(separator: "#") + "|\(fleet.menuBarSerial ?? "")"
        // Reports arrive every second; the file only needs to be fresh when the widget next reads it.
        if force || key != lastSavedKey || Date().timeIntervalSince(lastSnapshotWrite) > 20 {
            SharedStore.save(fleet)
            lastSnapshotWrite = Date()
            lastSavedKey = key
        }
        let elapsed = Date().timeIntervalSince(lastWidgetReload)
        guard key != lastWidgetKey, force || elapsed > 120 else { return }
        lastWidgetKey = key
        lastWidgetReload = Date()
        WidgetCenter.shared.reloadAllTimelines()
    }

    // MARK: - Demo

    private func startDemo(_ scenario: DemoDriver.Scenario) {
        isDemo = true
        guard scenario != .unconfigured else {
            writeSnapshot(force: true)
            return
        }
        // Not saved: a demo run must not replace the real printers' settings.
        var printers = [(PrinterConfig(host: "192.168.1.50", serial: "22E00A000000000", name: "P2S Workshop", modelCode: "N7"), scenario)]
        if CommandLine.arguments.contains("--fleet") {
            printers.append((PrinterConfig(host: "192.168.1.51", serial: "00M00A000000000", name: "X1C Garage", modelCode: "BL-P001"), .idle))
            printers.append((PrinterConfig(host: "192.168.1.52", serial: "039000000000000", name: "A1 Desk", modelCode: "N2S"), .paused))
            printers.append((PrinterConfig(host: "192.168.1.53", serial: "094000000000000", name: "H2D Studio", modelCode: "O1D"), .printing))
            printers.append((PrinterConfig(host: "192.168.1.54", serial: "030000000000000", name: "A1 mini Shelf", modelCode: "N1"), .finished))
            printers.append((PrinterConfig(host: "192.168.1.55", serial: "01P000000000000", name: "P1S Office", modelCode: "C12"), .printing))
        }
        for (index, (config, scenario)) in printers.enumerated() {
            add(config).startDemo(scenario, variant: index)
        }
        selectedSerial = sessions.first?.id
        history = DemoDriver.sampleHistory(for: sessions.map(\.id))
        writeSnapshot(force: true)
    }
}

/// The last few prints of every printer, newest first, in Application Support. Only what the app saw while
/// running; it backs "Last: …", finish times and end-of-print notifications, not a print log.
enum HistoryStore {
    /// Demo and developer sessions never touch the real history.
    private static var isEphemeral: Bool {
        CommandLine.arguments.contains("--demo") || CommandLine.arguments.contains("--connect") || CommandLine.arguments.contains("--emulator")
    }

    private static var url: URL? {
        guard let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first?
            .appendingPathComponent("Bambu Bar", isDirectory: true) else { return nil }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("history.json")
    }

    static func load() -> [JobRecord] {
        guard !isEphemeral, let url, let data = try? Data(contentsOf: url) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([JobRecord].self, from: data)) ?? []
    }

    static func save(_ jobs: [JobRecord]) {
        guard !isEphemeral, let url else { return }
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        // A handful per printer is all the UI uses.
        var kept: [JobRecord] = []
        var perPrinter: [String: Int] = [:]
        for job in jobs {
            let key = job.printerSerial ?? ""
            guard perPrinter[key, default: 0] < 5 else { continue }
            perPrinter[key, default: 0] += 1
            kept.append(job)
        }
        try? encoder.encode(kept).write(to: url, options: .atomic)
    }
}
