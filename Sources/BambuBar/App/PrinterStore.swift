import AppKit
import Observation
import OSLog
import WidgetKit

/// What the menu bar shows next to the icon.
enum MenuBarStyle: String, CaseIterable, Identifiable {
    case icon, percent, remaining, both
    var id: String { rawValue }
    var title: String {
        switch self {
        case .icon: return String(localized: "Icon only", comment: "Menu bar style")
        case .percent: return String(localized: "Progress", comment: "Menu bar style")
        case .remaining: return String(localized: "Time left", comment: "Menu bar style")
        case .both: return String(localized: "Progress and time left", comment: "Menu bar style")
        }
    }

    /// The text beside the icon during a print, in this style; nil for the icon alone.
    func text(for s: PrinterStatus) -> String? {
        let pct = "\(s.progress)%"
        let left = s.remainingMinutes > 0 ? Format.duration(minutes: s.remainingMinutes) : nil
        switch self {
        case .icon: return nil
        case .percent: return s.state == .paused ? s.state.title : pct
        case .remaining: return s.state == .paused ? s.state.title : (left ?? pct)
        case .both:
            if s.state == .paused { return "\(pct) · \(s.state.title)" }
            if s.state == .preparing { return s.progress > 0 ? pct : s.state.title }
            return left.map { "\(pct) · \($0)" } ?? pct
        }
    }
}

/// What the menu bar panel shows under the job, as UserDefaults keys (Menu Bar page). The panel and
/// that page read them with @AppStorage; nothing else needs them.
enum PanelContent {
    static let temperatures = "panelShowsTemperatures"
    static let chamber = "panelShowsChamber"
    static let fans = "panelShowsFans"
    static let filament = "panelShowsFilament"
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
    /// From the last scan of this Mac's slicers.
    private var scannedPrinters: [SlicerPrinter] = []
    /// Sandboxed (App Store) and not yet given the slicer's folder: setup asks for it.
    private(set) var slicerAccessNeeded = SlicerImport.needsAccess
    /// A short-lived message after a command ("The printer refused…").
    private(set) var toast: Toast?
    private(set) var isDemo = false

    /// Bounded diagnostics event buffer (updates live UI only while ActivityPage is visible).
    let activity = ActivityLog()

    /// `nil` = automatic.
    var menuBarSerial: String? {
        didSet {
            guard menuBarSerial != oldValue else { return }
            if !isDemo { defaults.set(menuBarSerial, forKey: Keys.menuBarPrinter) }
            writeSnapshot(force: true)
        }
    }
    var menuBarStyle: MenuBarStyle {
        didSet {
            guard menuBarStyle != oldValue else { return }
            if !isDemo { defaults.set(menuBarStyle.rawValue, forKey: Keys.menuBarStyle) }
        }
    }
    var notifyFinished: Bool {
        didSet {
            guard notifyFinished != oldValue else { return }
            if !isDemo { defaults.set(notifyFinished, forKey: Keys.notifyFinished) }
            requestNotificationsIfNeeded()
        }
    }
    var notifyFailed: Bool {
        didSet {
            guard notifyFailed != oldValue else { return }
            if !isDemo { defaults.set(notifyFailed, forKey: Keys.notifyFailed) }
            requestNotificationsIfNeeded()
        }
    }
    var notifyPaused: Bool {
        didSet {
            guard notifyPaused != oldValue else { return }
            if !isDemo { defaults.set(notifyPaused, forKey: Keys.notifyPaused) }
            requestNotificationsIfNeeded()
        }
    }
    var notifyStarted: Bool {
        didSet {
            guard notifyStarted != oldValue else { return }
            if !isDemo { defaults.set(notifyStarted, forKey: Keys.notifyStarted) }
            requestNotificationsIfNeeded()
        }
    }
    var confirmStop: Bool {
        didSet {
            guard confirmStop != oldValue else { return }
            if !isDemo { defaults.set(confirmStop, forKey: Keys.confirmStop) }
        }
    }
    var showPrinterActions: Bool {
        didSet {
            guard showPrinterActions != oldValue else { return }
            if !isDemo { defaults.set(showPrinterActions, forKey: Keys.showPrinterActions) }
        }
    }
    /// The camera, for every printer (Settings → Camera); each printer has its own switch too. Off by default.
    var cameraEnabled: Bool {
        didSet {
            guard cameraEnabled != oldValue else { return }
            if !isDemo { defaults.set(cameraEnabled, forKey: Keys.cameraEnabled) }
            cameraSettingsChanged()
        }
    }
    /// Keep the camera streaming for the whole of a print, so it shows at once. Off: only while it's on screen.
    var cameraStaysOnWhilePrinting: Bool {
        didSet {
            guard cameraStaysOnWhilePrinting != oldValue else { return }
            if !isDemo { defaults.set(cameraStaysOnWhilePrinting, forKey: Keys.cameraStaysOnWhilePrinting) }
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
    /// The status each printer had when the widgets were last asked to reload: what they show, for printers without
    /// a recent receipt from the extension.
    private var lastWidgetStatus: [String: PrinterStatus] = [:]
    /// What the widgets last built their timelines from, read from the extension's receipts at most every 30 s.
    private var widgetReceipts: [String: WidgetReceipt] = [:]
    private var widgetReceiptsRead = Date.distantPast
    /// Reload requests in a row that no widget answered by building a timeline: macOS dropped them, or no widget is
    /// on the desktop. Retries back off with it.
    private var unansweredWidgetReloads = 0
    /// The last request a widget answered. A printer whose receipt predates it has no widget that rebuilt with the
    /// others: removed, or never placed.
    private var answeredWidgetReload = Date.distantPast
    /// Reload requests over the last day, to cap the optional ones.
    private var widgetRequests: [Date] = []
    private static let widgetLog = Logger(subsystem: "dev.rolottr.BambuBar", category: "widget")
    private var lastSnapshotWrite = Date.distantPast
    private var lastSavedKey = ""
    private var lastWidgetKey = ""
    private var isStarted = false
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
        static let showPrinterActions = "showPrinterActions"
        static let cameraStaysOnWhilePrinting = "cameraStaysOnWhilePrinting"
        static let cameraEnabled = "cameraEnabled"
    }

    private var lastRecordedCameraState: [String: CameraFeed.State] = [:]

    private init() {
        // Assign @Observable backing storage directly so init() fires zero didSet side effects
        // (zero UserDefaults writes, zero notification prompts, zero widget snapshot writes).
        _menuBarStyle = MenuBarStyle(rawValue: defaults.string(forKey: Keys.menuBarStyle) ?? "") ?? .both
        _notifyFinished = defaults.object(forKey: Keys.notifyFinished) as? Bool ?? true
        _notifyFailed = defaults.object(forKey: Keys.notifyFailed) as? Bool ?? true
        _notifyPaused = defaults.object(forKey: Keys.notifyPaused) as? Bool ?? true
        _notifyStarted = defaults.object(forKey: Keys.notifyStarted) as? Bool ?? false
        _confirmStop = defaults.object(forKey: Keys.confirmStop) as? Bool ?? true
        _showPrinterActions = defaults.object(forKey: Keys.showPrinterActions) as? Bool ?? true
        _cameraStaysOnWhilePrinting = defaults.bool(forKey: Keys.cameraStaysOnWhilePrinting)
        _cameraEnabled = defaults.bool(forKey: Keys.cameraEnabled)
        _menuBarSerial = defaults.string(forKey: Keys.menuBarPrinter)
        _selectedSerial = defaults.string(forKey: Keys.selected)
    }

    // MARK: - Lifecycle

    func start() {
        guard !isStarted else { return }
        isStarted = true
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
           let saved = try? JSONDecoder().decode([PrinterConfig].self, from: data) {
            let configs = PrinterConfig.deduplicated(saved)
            guard configs != saved else { return configs }
            // A serial saved in another case keeps its access code under its new spelling.
            for config in saved where config.serial != config.serial.uppercased() {
                let upper = config.serial.uppercased()
                if Keychain.accessCode(for: upper) == nil, let code = Keychain.accessCode(for: config.serial) {
                    Keychain.setAccessCode(code, for: upper)
                }
            }
            if let data = try? JSONEncoder().encode(configs) { defaults.set(data, forKey: Keys.printers) }
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
            self?.recordPhase(session, phase: phase)
            // Reconnect attempts don't move the widget's link (WidgetReloadPolicy.link), so they cost no reloads; a
            // printer coming online, rejecting the code or being removed reloads at once.
            self?.writeSnapshot(force: phase == .online || phase == .unauthorized || phase == .idle)
        }
        session.onStatus = { [weak self] session, old, new, canceled in
            self?.recordStatusTransition(session, from: old, to: new)
            self?.trackJob(session, from: old, to: new, canceled: canceled)
            self?.writeSnapshot(force: old?.state != new.state)
        }
        session.onAck = { [weak self] session, command, ok, reason in self?.acknowledged(session, command: command, ok: ok, reason: reason) }
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
            var config = config
            // Setting a known printer up again keeps the codes ignored for it.
            config.ignoredIssues = config.ignoredIssues ?? existing.config.ignoredIssues
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
        for printer in SlicerImport.merge(printers).sorted(by: { name(for: $0).localizedStandardCompare(name(for: $1)) == .orderedAscending })
        where session(printer.serial) == nil {
            guard let host = host(for: printer), let code = printer.accessCodes.first else { continue }
            let seen = discovered.first { $0.serial == printer.serial }
            let config = PrinterConfig(host: host, serial: printer.serial, name: name(for: printer),
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
        return PrinterConfig.placeholderName(serial: printer.serial, modelCode: seen?.modelCode ?? "")
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

    /// Never shows this issue's code for the printer again, until it's removed on the Printer page settings.
    func ignore(_ issue: PrinterIssue, for serial: String) {
        guard let session = session(serial) else { return }
        session.ignore(issue)
        saveConfigs()
        writeSnapshot(force: true)
    }

    /// Shows an ignored code again (Printer page settings); it's back with the printer's next report.
    func unignore(_ id: String, for serial: String) {
        guard let session = session(serial) else { return }
        session.unignore(id)
        saveConfigs()
        writeSnapshot(force: true)
    }

    /// Turns one printer's camera on or off (Printer page settings).
    func setCameraEnabled(_ on: Bool, for serial: String) {
        guard let session = session(serial), session.config.cameraEnabled != on else { return }
        session.config.cameraEnabled = on
        saveConfigs()
        activity.record(category: .camera, printerName: session.config.name,
                        summary: on ? String(localized: "Camera enabled") : String(localized: "Camera disabled"),
                        outcome: .applied)
        cameraSettingsChanged()
    }

    /// A camera switch changed: a camera now off stops streaming and its window closes.
    private func cameraSettingsChanged() {
        for session in sessions {
            if !session.showsCamera { CameraWindow.close(session.id) }
            session.camera.update()
        }
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
        if let session = session(printer.serial), !isDemo {
            var changed = false
            // Follow a printer when DHCP hands it a new address.
            if session.config.host != printer.host {
                session.config.host = printer.host
                session.restart()
                changed = true
            }
            // Adopt the network-announced name once it arrives, when the stored one is still a name Bambu Bar made
            // up (the serial, or "P2S 495": the printer was added before it announced itself). A name set by
            // hand is left alone.
            if session.config.hasGeneratedName,
               let announced = announcedName(for: printer.serial), announced != session.config.name {
                session.config.name = announced
                changed = true
            }
            if changed {
                saveConfigs()
                writeSnapshot(force: true)
            }
        }
        // Correct a saved model id from the printer's own announcement (setups before 1.2 saved "N7" by default).
        if let session = session(printer.serial), !printer.modelCode.isEmpty, session.config.modelCode != printer.modelCode,
           PrinterModel.find(code: printer.modelCode) != nil, !isDemo {
            session.config.modelCode = printer.modelCode
            saveConfigs()
            writeSnapshot(force: true)
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
                self.scannedPrinters = scanned
                self.mergeSlicerPrinters()
                self.slicerAccessNeeded = SlicerImport.needsAccess
            }
        }
    }

    /// Reads a `BambuStudio.conf` / `OrcaSlicer.conf` the person picked or dropped (say, copied from the Mac
    /// that runs the slicer). Returns how many printers it knew; they're kept for this session only.
    @discardableResult
    func importSlicerSettings(from url: URL) -> Int {
        let found = SlicerImport.read(picked: url)
        if !found.isEmpty && !isDemo {
            SlicerImport.rememberAccess(to: url)
            slicerAccessNeeded = SlicerImport.needsAccess
        }
        // The file just picked replaces what an earlier one said about the same printers.
        for printer in found {
            importedPrinters.removeAll { $0.serial == printer.serial }
            importedPrinters.insert(printer, at: 0)
        }
        mergeSlicerPrinters()
        return found.count
    }

    /// Picked files first, then this Mac's slicers; a printer known to both keeps the codes of both to try.
    private func mergeSlicerPrinters() {
        let all = SlicerImport.merge(importedPrinters + scannedPrinters)
        if all != slicerPrinters { slicerPrinters = all }
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
    var printerName: String { current?.config.name ?? String(localized: "Printer") }
    var chamberLightOn: Bool { current?.chamberLightOn ?? false }
    var chamberLight2On: Bool? { current?.chamberLight2On }
    var speed: SpeedLevel { current?.speed ?? .standard }
    var accessCode: String? { current?.accessCode }
    var linkSummary: String { current?.linkSummary ?? String(localized: "No printer set up") }
    var linkDetail: String? { current?.linkDetail }

    /// The current printer's most recent print (for "Last: …" and "finished 3h ago").
    var lastJob: JobRecord? { history(for: current?.id).first }

    func history(for serial: String?) -> [JobRecord] {
        guard let serial else { return [] }
        return history.filter { $0.printerSerial == serial }
    }

    // MARK: - Commands (current printer)

    func pause() {
        guard let current, ensureControllable(current, action: String(localized: "Pause print")) else { return }
        current.pause()
        activity.record(category: .action, printerName: current.config.name, summary: String(localized: "Pause print"), outcome: .sent)
        showToast(String(localized: "Pausing…"))
    }

    func resume() {
        guard let current, ensureControllable(current, action: String(localized: "Resume print")) else { return }
        current.resume()
        activity.record(category: .action, printerName: current.config.name, summary: String(localized: "Resume print"), outcome: .sent)
        showToast(String(localized: "Resuming…"))
    }

    func stopPrint() {
        guard let current, ensureControllable(current, action: String(localized: "Stop print")) else { return }
        if confirmStop {
            let job = current.status?.jobName ?? ""
            let alert = NSAlert()
            alert.messageText = job.isEmpty ? String(localized: "Stop this print?") : String(localized: "Stop “\(job)”?")
            let warning = String(localized: "The print can't be resumed after it's stopped.")
            alert.informativeText = hasMultiplePrinters ? String(localized: "On \(current.config.name).", comment: "Which printer") + " " + warning : warning
            alert.alertStyle = .warning
            alert.addButton(withTitle: String(localized: "Stop Print"))
            alert.addButton(withTitle: String(localized: "Keep Printing"))
            alert.buttons.first?.hasDestructiveAction = true
            NSApp.activate(ignoringOtherApps: true)
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        current.stopPrint()
        activity.record(category: .action, printerName: current.config.name, summary: String(localized: "Stop print"), outcome: .sent)
        showToast(String(localized: "Stopping…"))
    }

    func toggleLight(_ light: ChamberLight = .left) {
        guard let current else { return }
        let targetOn = light == .left ? !current.chamberLightOn : !(current.chamberLight2On ?? false)
        current.toggleLight(light)
        let label = light == .left
            ? (targetOn ? String(localized: "Chamber light on") : String(localized: "Chamber light off"))
            : (targetOn ? String(localized: "Right light on") : String(localized: "Right light off"))
        activity.record(category: .action, printerName: current.config.name, summary: label, outcome: .sent)
    }

    func setSpeed(_ level: SpeedLevel) {
        guard let current, ensureControllable(current, action: String(localized: "Speed → \(level.title)")) else { return }
        current.setSpeed(level)
        activity.record(category: .action, printerName: current.config.name, summary: String(localized: "Speed → \(level.title)"), outcome: .sent)
    }

    /// Print commands need the printer in Developer Mode; say so instead of sending something it will refuse.
    private func ensureControllable(_ session: PrinterSession, action: String) -> Bool {
        if session.status?.controlsLocked == true {
            activity.record(category: .action, printerName: session.config.name, summary: action, outcome: .failed)
            showToast(String(localized: "Turn on LAN Only and Developer Mode on the printer to control prints from Bambu Bar."), error: true)
            return false
        }
        return true
    }

    private func acknowledged(_ session: PrinterSession, command: String, ok: Bool, reason: String?) {
        if ok {
            activity.record(category: .action, printerName: session.config.name, summary: command, outcome: .applied)
            return
        }
        activity.record(category: .action, printerName: session.config.name,
                        summary: sanitizeActivityText(reason.map { "\(command): \($0)" } ?? command), outcome: .failed)
        let detail = (reason ?? "").lowercased().contains("verify")
            ? String(localized: "The printer only accepts commands in Developer Mode.")
            : (reason.map { String(localized: "The printer refused: \($0)", comment: "The printer's reason, in English") } ?? String(localized: "The printer refused the command."))
        showToast(hasMultiplePrinters ? "\(session.config.name): \(detail)" : detail, error: true)
    }

    // MARK: - Activity & Onboarding

    private func sanitizeActivityText(_ text: String) -> String {
        let serials = sessions.map(\.id) + discovered.map(\.serial)
        let hosts = sessions.map(\.config.host) + discovered.map(\.host)
        let codes = sessions.compactMap(\.cameraAccessCode)
        return ActivityLog.redact(text, serials: serials, hosts: hosts, accessCodes: codes)
    }

    private func recordPhase(_ session: PrinterSession, phase: PrinterConnection.Phase) {
        let state: ActivityLog.ConnectionState
        let summary: String
        switch phase {
        case .idle:
            state = .idle
            summary = String(localized: "Disconnected")
        case .connecting:
            state = .connecting
            summary = String(localized: "Connecting…")
        case .online:
            state = .online
            summary = String(localized: "Connected")
        case .offline(let reason):
            state = .offline
            summary = reason.isEmpty ? String(localized: "Offline") : String(localized: "Offline: \(reason)")
        case .unauthorized:
            state = .issue("unauthorized")
            summary = String(localized: "Access code rejected")
        case .certificate:
            state = .issue("certificate")
            summary = String(localized: "Certificate error")
        case .localNetworkDenied:
            state = .issue("localNetworkDenied")
            summary = String(localized: "Local network access denied")
        }
        activity.recordConnectionTransition(
            printerID: session.id,
            printerName: sanitizeActivityText(session.config.name),
            state: state,
            summary: sanitizeActivityText(summary)
        )
    }

    private func recordStatusTransition(_ session: PrinterSession, from old: PrinterStatus?, to new: PrinterStatus) {
        if old?.state != new.state {
            let detail = new.jobName.isEmpty ? new.stateTitle : "\(new.stateTitle) · \(new.jobName)"
            activity.record(category: .job, printerName: session.config.name, summary: detail)
        }
        let oldIDs = Set((old?.issues ?? []).map(\.id))
        let newIDs = Set(new.issues.map(\.id))
        for issue in new.issues where !oldIDs.contains(issue.id) {
            activity.record(category: .issue, printerName: session.config.name, summary: "\(issue.id): \(issue.message)")
        }
        for issue in (old?.issues ?? []) where !newIDs.contains(issue.id) {
            activity.record(category: .issue, printerName: session.config.name, summary: String(localized: "Cleared \(issue.id)"))
        }
    }

    func recordCameraState(_ state: CameraFeed.State, for session: PrinterSession) {
        guard lastRecordedCameraState[session.id] != state else { return }
        lastRecordedCameraState[session.id] = state
        let summary: String
        let outcome: ActivityLog.Outcome?
        switch state {
        case .idle: summary = String(localized: "Stream stopped"); outcome = nil
        case .connecting: summary = String(localized: "Stream connecting…"); outcome = nil
        case .live: summary = session.camera.detail.map { String(localized: "Stream live · \($0)") } ?? String(localized: "Stream live"); outcome = .applied
        case .unavailable(let reason): summary = reason; outcome = nil
        case .failed(let reason, _): summary = reason; outcome = .failed
        }
        activity.record(category: .camera, printerName: sanitizeActivityText(session.config.name),
                        summary: sanitizeActivityText(summary), outcome: outcome)
    }

    func capabilitySummaries() -> [ActivityLog.CapabilitySummary] {
        sessions.map { session in
            let model = session.config.model
            let status = session.status
            return ActivityLog.CapabilitySummary(
                printerName: session.config.name,
                modelName: session.config.modelName,
                serialPrefix: String(session.id.prefix(3)).uppercased(),
                isOnline: session.isOnline,
                amsCount: status?.ams.count ?? 0,
                hasChamberSensor: model?.hasChamberSensor ?? (status?.chamber != nil),
                hasChamberHeater: model?.chamberHeaterMax != nil,
                hasChamberLight: model?.hasChamberLight ?? (status?.chamberLightOn != nil),
                hasDualLight: status?.chamberLight2On != nil,
                cameraEnabled: session.showsCamera,
                nozzleCount: model?.nozzleCount ?? (status?.secondNozzle != nil ? 2 : 1),
                hasAuxFan: model?.hasAuxFan ?? (status?.auxFan != nil),
                hasChamberFan: model?.hasChamberFan ?? (status?.exhaustFan != nil || status?.circulationFan != nil)
            )
        }
    }

    func exportActivityJSON() throws -> Data {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
        let serials = sessions.map(\.id) + discovered.map(\.serial)
        let hosts = sessions.map(\.config.host) + discovered.map(\.host)
        let codes = sessions.compactMap(\.cameraAccessCode)
        return try activity.exportJSON(
            capabilities: capabilitySummaries(),
            appVersion: version,
            serials: serials,
            hosts: hosts,
            accessCodes: codes
        )
    }

    var onboardingCapabilities: OnboardingCapabilities? {
        guard isConfigured else { return nil }
        let supportsCamera = !isDemo && Self.emulatorSettings == nil
        return OnboardingCapabilities(supportsCamera: supportsCamera, hasMultiplePrinters: hasMultiplePrinters)
    }

    func onboardingSettings() -> OnboardingSettings {
        OnboardingSettings(
            showProgressInMenuBar: menuBarStyle != .icon,
            notifyFinished: notifyFinished,
            notifyFailed: notifyFailed,
            notifyPaused: notifyPaused,
            cameraEnabled: cameraEnabled,
            showPrinterActions: showPrinterActions,
            panelShowsAllPrinters: defaults.bool(forKey: "panelShowsAllPrinters")
        )
    }

    func applyOnboarding(_ draft: OnboardingDraft) {
        let updated = draft.applied(to: onboardingSettings())
        menuBarStyle = updated.showProgressInMenuBar ? (menuBarStyle == .icon ? .both : menuBarStyle) : .icon
        notifyFinished = updated.notifyFinished
        notifyFailed = updated.notifyFailed
        notifyPaused = updated.notifyPaused
        cameraEnabled = updated.cameraEnabled
        if updated.cameraEnabled, let current, current.config.cameraEnabled != true {
            setCameraEnabled(true, for: current.id)
        }
        showPrinterActions = updated.showPrinterActions
        if !isDemo {
            defaults.set(updated.panelShowsAllPrinters, forKey: "panelShowsAllPrinters")
        }
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
        let name = new.jobName.isEmpty ? String(localized: "Untitled print") : new.jobName
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
                notifier.post(title: prefix + String(localized: "Print started"), body: name, id: "start.\(serial)")
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
            notifier.post(title: prefix + String(localized: "Print paused"), body: "\(name): \(new.detailText)", id: "pause.\(serial)")
        }
        if notify, let old {
            let fresh = new.issues.filter { issue in issue.severity <= .serious && !old.issues.contains { $0.id == issue.id } }
            if let issue = fresh.first, notifyFailed || notifyPaused {
                notifier.post(title: "\(session.config.name): \(issue.severity.title)", body: issue.message, id: "\(issue.id).\(serial)")
            }
        }
    }

    private func notifyEnd(_ job: JobRecord, status: PrinterStatus, prefix: String, serial: String) {
        switch job.outcome {
        case .finished where notifyFinished:
            let body = job.duration.map {
                String(localized: "\(job.name) finished in \(Format.duration(seconds: $0)). It's ready to take off the plate.", comment: "Job name, then how long it took")
            } ?? String(localized: "\(job.name) finished. It's ready to take off the plate.")
            notifier.post(title: prefix + String(localized: "Print finished"), body: body, id: "end.\(serial)")
        case .failed where notifyFailed:
            let reason = status.issues.first?.message ?? String(localized: "the printer stopped the job.", comment: "After the job name and a colon")
            notifier.post(title: prefix + String(localized: "Print failed"), body: "\(job.name): \(reason)", id: "end.\(serial)")
        case .canceled where notifyFailed:
            notifier.post(title: prefix + String(localized: "Print canceled"), body: job.name, id: "end.\(serial)")
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

    /// A sample from the Notifications page, to see where notifications appear and how they sound (as on Windows).
    func sendTestNotification() {
        notifier.post(title: String(localized: "Notifications are on"),
                      body: String(localized: "This is how Bambu Bar tells you a print finished, failed or needs you."), id: "test")
    }

    // MARK: - Widget

    func snapshot(of session: PrinterSession) -> SharedSnapshot {
        let connection: WidgetReloadPolicy.Connection
        switch session.phase {
        case .online: connection = .online
        case .connecting: connection = .connecting
        case .unauthorized: connection = .unauthorized
        // A certificate that doesn't check out isn't retried, so no later phase would correct a recent "online".
        case .idle, .certificate: connection = .idle
        case .offline, .localNetworkDenied: connection = .offline
        }
        // A printer that can't be reached retries every few seconds; its widget link stays put through the
        // attempts, so they don't each change the reload key (WidgetReloadPolicy).
        let link = WidgetReloadPolicy.link(connection, hasStatus: session.status != nil, lastHeard: session.lastHeard,
                                           failedSinceOnline: session.failedSinceOnline, now: Date())
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

    /// Writes the widgets' snapshot and asks WidgetKit to reload when the widgets would otherwise show something wrong.
    /// macOS rations reloads (a daily allowance per widget, spent by every request from an app in the background)
    /// and drops the rest without a word, after which a widget plays out its old timeline for hours. So reloads go
    /// to what a glance would catch: a new state, job or problem, and a print running off the timeline the widget
    /// projects. Routine progress needs none, since the widget projects it, nor does a multicolour print's spool
    /// changing every few layers, or the stage that cycles with it (the widget's tint and active swatch may lag a
    /// swap until the next reload; the stage isn't shown while printing).
    /// The extension's receipts say what the widgets really show, so a dropped reload is asked for again, spaced out.
    private func writeSnapshot(force: Bool, opened: Bool = false) {
        // Demo and developer sessions must never show up in the real widgets.
        guard !isDemo else { return }
        let fleet = fleet
        let now = Date()
        let key = fleet.printers.map { snap -> String in
            let s = snap.status
            let stage = s?.state == .printing ? -8 : s?.stageID ?? -9
            return "\(snap.serial)|\(snap.link)|\(snap.printerName)|\(s?.state.rawValue ?? "-")|\(stage)|\(s?.jobName ?? "")|\(s?.issues.count ?? 0)|\(s?.ams.flatMap(\.spools).map(\.color).joined() ?? "")"
        }.joined(separator: "#") + "|\(fleet.menuBarSerial ?? "")"
        let elapsed = now.timeIntervalSince(lastWidgetReload)
        widgetRequests.removeAll { now.timeIntervalSince($0) > 86400 }
        // Corrections and keep-alives stop at a daily cap, leaving the allowance to what changes on the printer.
        let mayCorrect = widgetRequests.count < Self.widgetDailyCorrections
        // Opened: someone opened the panel or the window, likely to compare. Reloads asked for while the app is in
        // front don't count against the allowance; still, skip them when the widgets already show this.
        let reload = WidgetReloadPolicy.shouldReload(
            keyChanged: key != lastWidgetKey, force: force, opened: opened, sinceLastReload: elapsed,
            unanswered: unansweredWidgetReloads, correctionAllowed: mayCorrect,
            correctionDue: {
                opened ? widgetsBehind(fleet, now: now, progress: 0.02, minutes: 3)
                    : widgetsBehind(fleet, now: now, progress: 0.03, minutes: 5) || widgetsAging(fleet, now: now)
            })
        // Reports arrive every second; the file only needs to be fresh when the widget next reads it.
        if reload || force || key != lastSavedKey || now.timeIntervalSince(lastSnapshotWrite) > 20 {
            SharedStore.save(fleet)
            lastSnapshotWrite = now
            lastSavedKey = key
        }
        guard reload else { return }
        // Was the previous request answered? A receipt built since says so. One only seconds old (a connect forces
        // two) may still be on its way, so it isn't judged.
        let receipts = readWidgetReceipts(now: now, fresh: true)
        if lastWidgetReload != .distantPast, elapsed > 60 {
            if receipts.values.contains(where: { $0.builtAt >= lastWidgetReload }) {
                unansweredWidgetReloads = 0
            } else {
                unansweredWidgetReloads += 1
                let asked = lastWidgetReload.formatted(date: .omitted, time: .standard)
                Self.widgetLog.info("No widget answered the reload asked for at \(asked, privacy: .public) (\(self.unansweredWidgetReloads) in a row)")
            }
        }
        lastWidgetKey = key
        lastWidgetReload = now
        widgetRequests.append(now)
        lastWidgetStatus = Dictionary(fleet.printers.compactMap { snap in snap.status.map { (snap.serial, $0) } },
                                      uniquingKeysWith: { first, _ in first })
        WidgetCenter.shared.reloadAllTimelines()
    }

    /// How old the figures a printing widget shows may get before the app reloads it; inside
    /// `WidgetState.projectionWindow` with room for WidgetKit to take its time. About 30 reloads a day for a printer
    /// that never stops, well inside the allowance.
    private static let widgetKeepAlive: TimeInterval = 45 * 60

    /// Requests a day beyond which the app only asks for reloads when something changes on a printer. Widgets get
    /// about 40 to 70 a day, and their own fallback refreshes come out of the same allowance.
    private static let widgetDailyCorrections = 40

    /// Receipts older than this are left over (the app wasn't running, say); the app then goes by what it asked for.
    private static let widgetReceiptLifetime: TimeInterval = 6 * 3600

    /// The extension's receipts still current, read from the file at most every 30 s (reports come every second).
    private func readWidgetReceipts(now: Date, fresh: Bool = false) -> [String: WidgetReceipt] {
        if fresh || now.timeIntervalSince(widgetReceiptsRead) > 30 {
            widgetReceipts = WidgetReceipts.load().filter { now.timeIntervalSince($0.value.builtAt) < Self.widgetReceiptLifetime }
            widgetReceiptsRead = now
            if lastWidgetReload != .distantPast, widgetReceipts.values.contains(where: { $0.builtAt >= lastWidgetReload }) {
                answeredWidgetReload = lastWidgetReload
            }
        }
        return widgetReceipts
    }

    /// What the widgets show for a printer, as far as the app can tell; nil when no widget shows it.
    private struct ShownOnWidget {
        /// nil when unknown.
        var link: SharedSnapshot.Link?
        var status: PrinterStatus?
        var savedAt: Date
    }

    private func shownOnWidget(_ serial: String, now: Date) -> ShownOnWidget? {
        let receipts = readWidgetReceipts(now: now)
        // No receipts yet (an extension from before 2.2.4, or none built since): go by what the app last asked for.
        guard !receipts.isEmpty else {
            return ShownOnWidget(link: nil, status: lastWidgetStatus[serial], savedAt: lastWidgetReload)
        }
        // Every reload rebuilds every widget; a printer whose receipt didn't follow the others' has no widget, and
        // chasing it would spend the other widgets' allowance.
        guard let receipt = receipts[serial], receipt.builtAt >= answeredWidgetReload else { return nil }
        return ShownOnWidget(link: receipt.link, status: receipt.status, savedAt: receipt.savedAt)
    }

    /// Whether a reload would change what the widgets show for any printer: its connection, state or job, or, while
    /// printing, its progress by at least `progress` (a fraction) or its finish time by `minutes`. Projections are
    /// compared with projections (not with the reported percentage): while a print levels or heats at 0 %, both
    /// climb alike and a reload changes nothing.
    private func widgetsBehind(_ fleet: SharedFleet, now: Date, progress: Double, minutes: Double) -> Bool {
        fleet.printers.contains { snap in
            guard let widget = shownOnWidget(snap.serial, now: now) else { return false }
            if let link = widget.link, link != snap.link { return true }
            if let a = widget.status, let b = snap.status, widget.link != nil, a.state != b.state || a.jobName != b.jobName {
                return true
            }
            guard snap.link == .online, let s = snap.status, s.state == .printing,
                  let shown = widget.status, shown.state == .printing else { return false }
            // The widget stops projecting at the end of its window.
            let shownAt = min(now, widget.savedAt.addingTimeInterval(WidgetState.projectionWindow))
            if abs(shown.projectedProgress(at: shownAt) - s.projectedProgress(at: now)) >= progress { return true }
            switch (shown.estimatedEnd, s.estimatedEnd) {
            case let (a?, b?): return abs(a.timeIntervalSince(b)) >= minutes * 60
            case (nil, _?): return true
            default: return false
            }
        }
    }

    /// Whether a widget's figures for a running print are about to leave its projection window.
    private func widgetsAging(_ fleet: SharedFleet, now: Date) -> Bool {
        fleet.printers.contains { snap in
            guard snap.link == .online, snap.status?.state == .printing,
                  let widget = shownOnWidget(snap.serial, now: now) else { return false }
            return now.timeIntervalSince(widget.savedAt) > Self.widgetKeepAlive
        }
    }

    /// The menu bar panel or the window opened. Only while the app is in front, where reloads are free.
    func appOpened() {
        guard isStarted, NSApp.isActive else { return }
        writeSnapshot(force: false, opened: true)
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
