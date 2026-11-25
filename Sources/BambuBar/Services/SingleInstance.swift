import AppKit
import Darwin
import Foundation
import Observation

/// One Bambu Bar at a time: a second copy hands over to the earlier one and quits, so there's
/// one menu bar icon and one connection per printer. Simultaneous launches break ties deterministically
/// by `(launchDate, processIdentifier)`, and language restarts hand over cleanly via `RelaunchTicketStore`.
@MainActor
enum SingleInstance {
    nonisolated static let showWindow = Notification.Name("dev.rolottr.BambuBar.showWindow")

    struct Instance: Equatable, Sendable {
        var processIdentifier: Int32
        var launchDate: Date?
    }

    /// The instance `current` should hand over to: the earliest other launch, and only when it started
    /// before `current` (breaking equal-date ties by process ID), so two simultaneous launches never both exit.
    nonisolated static func handOverTarget(_ current: Instance, others: [Instance]) -> Instance? {
        func order(_ instance: Instance) -> (Date, Int32) {
            (instance.launchDate ?? .distantPast, instance.processIdentifier)
        }
        guard let earliest = others.min(by: { order($0) < order($1) }), order(earliest) < order(current) else {
            return nil
        }
        return earliest
    }

    /// The earlier running copy of this app, if this launch should hand over to it.
    static func earlierInstance(bundleIdentifier: String) -> NSRunningApplication? {
        guard !bundleIdentifier.isEmpty else { return nil }
        let current = NSRunningApplication.current
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: bundleIdentifier).filter {
            $0 != current && !$0.isTerminated
        }
        guard let target = handOverTarget(
            Instance(processIdentifier: current.processIdentifier, launchDate: current.launchDate),
            others: others.map { Instance(processIdentifier: $0.processIdentifier, launchDate: $0.launchDate) }
        ) else { return nil }
        return others.first { $0.processIdentifier == target.processIdentifier }
    }

    /// Quits this copy if another earlier copy is running, or waits cleanly for a language-restart predecessor.
    /// Returns any URLs handed over by a relaunch ticket.
    @discardableResult
    static func handOverIfRunning(
        background: Bool,
        arguments: [String] = CommandLine.arguments,
        ticketStore: RelaunchTicketStore = .shared,
        predecessorTimeout: TimeInterval = 15
    ) -> [URL] {
        guard !LaunchContext.isDeveloperRun else { return [] }
        if let ticket = ticketStore.pending(arguments: arguments) {
            let deadline = Date().addingTimeInterval(predecessorTimeout)
            while isProcessRunning(ticket.predecessor) {
                guard ticketStore.isPending(ticket), Date() < deadline else {
                    exit(0)
                }
                Thread.sleep(forTimeInterval: 0.05)
            }
            return (try? ticketStore.consume(ticket)) ?? []
        }
        guard let other = earlierInstance(bundleIdentifier: Bundle.main.bundleIdentifier ?? "") else { return [] }
        let forwardedURLs = arguments.dropFirst().compactMap { arg -> URL? in
            guard let url = URL(string: arg), url.scheme == ticketStore.urlScheme || url.isFileURL else { return nil }
            return url
        }
        let queuedForRelaunch = queueURLsIfRelaunching(
            forwardedURLs,
            forPredecessor: other.processIdentifier,
            ticketStore: ticketStore
        )
        if !background && !queuedForRelaunch {
            DistributedNotificationCenter.default().postNotificationName(showWindow, object: nil, deliverImmediately: true)
            other.activate()
        }
        exit(0)
    }

    /// Queues URLs onto an active relaunch ticket when `predecessor` is mid-restart so the replacement
    /// opens them after taking over.
    @discardableResult
    nonisolated static func queueURLsIfRelaunching(
        _ urls: [URL],
        forPredecessor predecessor: Int32,
        ticketStore: RelaunchTicketStore = .shared
    ) -> Bool {
        guard !urls.isEmpty, let ticket = ticketStore.activeTicket(forPredecessor: predecessor) else { return false }
        return ticketStore.queue(urls, for: ticket)
    }

    /// Shows the window when a later copy hands over.
    @discardableResult
    static func listen(onShow: @escaping @MainActor () -> Void) -> NSObjectProtocol {
        DistributedNotificationCenter.default().addObserver(forName: showWindow, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { onShow() }
        }
    }

    private static func isProcessRunning(_ pid: Int32) -> Bool {
        guard pid > 0 else { return false }
        if kill(pid, 0) == 0 { return true }
        return errno == EPERM
    }
}

// MARK: - Window Frame Clamping

/// Clamps window frames to a visible screen area (excluding the menu bar and Dock) so windows never
/// appear off-screen, behind the Dock, or larger than a smaller display.
enum WindowFrameClamp {
    nonisolated static func clamped(
        _ frame: NSRect,
        minSize: NSSize,
        visibleFrame: NSRect,
        aspectRatio: CGFloat? = nil,
        contentChrome: NSSize = .zero
    ) -> NSRect {
        guard visibleFrame.width > 0, visibleFrame.height > 0 else { return frame }
        var result = frame
        if let ratio = aspectRatio, ratio > 0 {
            let maxContentW = max(1, visibleFrame.width - contentChrome.width)
            let maxContentH = max(1, visibleFrame.height - contentChrome.height)
            let minContentW = max(1, minSize.width - contentChrome.width)
            let currentContentW = max(1, result.width - contentChrome.width)
            var contentW = min(max(currentContentW, minContentW), maxContentW)
            var contentH = (contentW / ratio).rounded()
            if contentH > maxContentH {
                contentH = maxContentH
                contentW = (contentH * ratio).rounded()
            }
            if contentW > maxContentW {
                contentW = maxContentW
                contentH = (contentW / ratio).rounded()
            }
            result.size.width = min(max(1, contentW) + contentChrome.width, visibleFrame.width)
            result.size.height = min(max(1, contentH) + contentChrome.height, visibleFrame.height)
        } else {
            result.size.width = min(max(result.width, minSize.width), visibleFrame.width)
            result.size.height = min(max(result.height, minSize.height), visibleFrame.height)
        }
        result.origin.x = min(max(result.minX, visibleFrame.minX), visibleFrame.maxX - result.width)
        result.origin.y = min(max(result.minY, visibleFrame.minY), visibleFrame.maxY - result.height)
        return result
    }

    /// Selects the screen whose visible frame has the largest non-zero intersection area with `frame`,
    /// falling back to `preferred` or the first screen.
    nonisolated static func bestVisibleFrame(for frame: NSRect, screens: [NSRect], preferred: NSRect? = nil) -> NSRect? {
        guard !screens.isEmpty else { return nil }
        var best: NSRect?
        var bestArea: CGFloat = 0
        for visible in screens {
            let intersection = visible.intersection(frame)
            guard !intersection.isNull, !intersection.isEmpty else { continue }
            let area = intersection.width * intersection.height
            if area > bestArea {
                bestArea = area
                best = visible
            }
        }
        return best ?? preferred ?? screens.first
    }

    nonisolated static func clamped(
        _ frame: NSRect,
        minSize: NSSize,
        screens: [NSRect],
        preferred: NSRect? = nil,
        aspectRatio: CGFloat? = nil,
        contentChrome: NSSize = .zero
    ) -> NSRect {
        guard let target = bestVisibleFrame(for: frame, screens: screens, preferred: preferred) else { return frame }
        return clamped(frame, minSize: minSize, visibleFrame: target, aspectRatio: aspectRatio, contentChrome: contentChrome)
    }

    @MainActor
    static func clampedToScreens(
        _ frame: NSRect,
        minSize: NSSize,
        preferredScreen: NSScreen? = nil,
        aspectRatio: CGFloat? = nil,
        contentChrome: NSSize = .zero
    ) -> NSRect {
        let screens = NSScreen.screens
        guard !screens.isEmpty else { return frame }
        let preferred = preferredScreen?.visibleFrame ?? NSScreen.main?.visibleFrame
        return clamped(
            frame,
            minSize: minSize,
            screens: screens.map(\.visibleFrame),
            preferred: preferred,
            aspectRatio: aspectRatio,
            contentChrome: contentChrome
        )
    }

    @MainActor
    static func clamp(_ window: NSWindow, minSize: NSSize) {
        guard !window.styleMask.contains(.fullScreen),
              !window.isMiniaturized,
              !window.inLiveResize else { return }
        let minFrameSize = window.frameRect(forContentRect: NSRect(origin: .zero, size: minSize)).size
        let contentRect = window.contentRect(forFrameRect: window.frame)
        let chrome = NSSize(
            width: max(0, window.frame.width - contentRect.width),
            height: max(0, window.frame.height - contentRect.height)
        )
        let ratio: CGFloat? = (window.contentAspectRatio.width > 0 && window.contentAspectRatio.height > 0)
            ? window.contentAspectRatio.width / window.contentAspectRatio.height
            : nil
        let target = clampedToScreens(
            window.frame,
            minSize: minFrameSize,
            preferredScreen: window.screen,
            aspectRatio: ratio,
            contentChrome: chrome
        )
        if target != window.frame {
            window.setFrame(target, display: true)
        }
    }
}

// MARK: - Activity Log

/// Bounded ring buffer of printer, connection, camera, issue, and user-action events.
/// While `isMonitoring` is false (the Activity page is hidden), recording writes only to the
/// private backing array with zero `@Observable` notifications or UI updates.
@MainActor
@Observable
final class ActivityLog {
    enum Category: String, CaseIterable, Codable, Identifiable, Sendable {
        case connection, job, issue, camera, action
        var id: String { rawValue }

        var title: String {
            switch self {
            case .connection: return String(localized: "Connection", comment: "Activity filter")
            case .job: return String(localized: "Print", comment: "Activity filter")
            case .issue: return String(localized: "Issues", comment: "Activity filter")
            case .camera: return String(localized: "Camera", comment: "Activity filter")
            case .action: return String(localized: "Actions", comment: "Activity filter")
            }
        }

        var symbol: String {
            switch self {
            case .connection: return "wifi"
            case .job: return "printer.fill"
            case .issue: return "exclamationmark.triangle.fill"
            case .camera: return "video.fill"
            case .action: return "hand.tap.fill"
            }
        }
    }

    enum Outcome: String, Codable, Equatable, Sendable {
        case applied, sent, failed

        var title: String {
            switch self {
            case .applied: return String(localized: "Applied", comment: "Activity outcome")
            case .sent: return String(localized: "Sent", comment: "Activity outcome")
            case .failed: return String(localized: "Failed", comment: "Activity outcome")
            }
        }
    }

    struct Entry: Identifiable, Codable, Equatable, Sendable {
        let id: UUID
        let timestamp: Date
        let category: Category
        let printerName: String
        let summary: String
        let outcome: Outcome?

        init(id: UUID = UUID(), timestamp: Date = Date(), category: Category, printerName: String,
             summary: String, outcome: Outcome? = nil) {
            self.id = id
            self.timestamp = timestamp
            self.category = category
            self.printerName = printerName
            self.summary = summary
            self.outcome = outcome
        }
    }

    struct CapabilitySummary: Identifiable, Codable, Equatable, Sendable {
        var id: String { "\(printerName)-\(serialPrefix)" }
        let printerName: String
        let modelName: String
        let serialPrefix: String
        let isOnline: Bool
        let amsCount: Int
        let hasChamberSensor: Bool
        let hasChamberHeater: Bool
        let hasChamberLight: Bool
        let hasDualLight: Bool
        let cameraEnabled: Bool
        let nozzleCount: Int
        let hasAuxFan: Bool
        let hasChamberFan: Bool
    }

    struct ExportPayload: Codable, Equatable, Sendable {
        let exportedAt: Date
        let appVersion: String
        let capabilities: [CapabilitySummary]
        let events: [Entry]
    }

    enum ConnectionState: Equatable, Sendable {
        case idle, connecting, online, offline, issue(String)
    }

    let capacity: Int
    @ObservationIgnored private var buffer: [Entry] = []
    @ObservationIgnored private(set) var isMonitoring = false
    /// Tracks the last logged connection state per printer so repeated offline ↔ connecting
    /// reconnect retries do not flood the ring buffer.
    @ObservationIgnored private var connectionStates: [String: ConnectionState] = [:]

    /// Observed by `ActivityPage` only while mounted. Empty whenever the page is hidden.
    private(set) var visibleEntries: [Entry] = []

    init(capacity: Int = 500) {
        self.capacity = max(1, capacity)
    }

    /// Full backing count (for diagnostics and tests without triggering observation).
    var count: Int { buffer.count }

    /// Snapshot of all buffered entries, newest last.
    var allEntries: [Entry] { buffer }

    /// Records a printer connection-state transition while suppressing repeated `.offline ↔ .connecting`
    /// and `.issue ↔ .connecting` reconnect cycles. Logs the initial failure/offline transition and the
    /// eventual `.online` recovery.
    func recordConnectionTransition(
        printerID: String,
        printerName: String,
        state: ConnectionState,
        summary: String,
        timestamp: Date = Date()
    ) {
        let previous = connectionStates[printerID]
        guard previous != state else { return }
        if state == .connecting {
            switch previous {
            case .offline, .issue:
                // Suppress reconnect retry noise while a printer stays offline or blocked by a persistent issue.
                return
            default:
                break
            }
        }
        connectionStates[printerID] = state
        record(category: .connection, printerName: printerName, summary: summary, timestamp: timestamp)
    }

    func record(category: Category, printerName: String, summary: String, outcome: Outcome? = nil, timestamp: Date = Date()) {
        let entry = Entry(timestamp: timestamp, category: category, printerName: printerName, summary: summary, outcome: outcome)
        buffer.append(entry)
        if buffer.count > capacity {
            buffer.removeFirst(buffer.count - capacity)
        }
        if isMonitoring {
            visibleEntries = buffer
        }
    }

    func startMonitoring() {
        isMonitoring = true
        visibleEntries = buffer
    }

    func stopMonitoring() {
        isMonitoring = false
        if !visibleEntries.isEmpty {
            visibleEntries = []
        }
    }

    func clear() {
        buffer.removeAll()
        if isMonitoring {
            visibleEntries = []
        }
    }

    // Cached redaction patterns (compiled once per process rather than per row per render).
    nonisolated private static let ipv4Regex = try! NSRegularExpression(
        pattern: #"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b"#
    )
    nonisolated private static let ipv6Regex: NSRegularExpression = {
        let h = "[0-9A-Fa-f]{1,4}"
        let core = [
            "(?:\(h):){7}\(h)",
            "::(?:[Ff]{4}:)?(?:\\d{1,3}\\.){3}\\d{1,3}",
            "(?:\(h):){1,6}:\(h)",
            "(?:\(h):){1,5}(?::\(h)){1,2}",
            "(?:\(h):){1,4}(?::\(h)){1,3}",
            "(?:\(h):){1,3}(?::\(h)){1,4}",
            "(?:\(h):){1,2}(?::\(h)){1,5}",
            "\(h):(?::\(h)){1,6}",
            ":(?::\(h)){1,7}",
            "(?:\(h):){1,7}:",
        ].joined(separator: "|")
        let zone = "(?:%[0-9A-Za-z_.-]+)?"
        let bracketed = "\\[(?:\(core))\(zone)\\](?::\\d{1,5})?"
        let bare = "(?:\(core))\(zone)"
        return try! NSRegularExpression(pattern: "(?<![0-9A-Za-z_:])(?:\(bracketed)|\(bare))(?![0-9A-Za-z_:])")
    }()
    nonisolated private static let serialRegex = try! NSRegularExpression(
        pattern: #"\b[0-9A-Z]{3}[0-9A-Z]{12}\b"#
    )

    /// Replaces any IPv4/IPv6 addresses, explicit hosts, full serial numbers, and access codes with safe placeholders.
    nonisolated static func redact(_ text: String, serials: [String] = [], hosts: [String] = [], accessCodes: [String] = []) -> String {
        var output = text
        for code in accessCodes where !code.isEmpty {
            output = output.replacingOccurrences(of: code, with: "<redacted>", options: .caseInsensitive)
        }
        for serial in serials where serial.count >= 6 {
            output = output.replacingOccurrences(of: serial, with: "<serial>", options: .caseInsensitive)
        }
        for host in hosts where !host.isEmpty {
            output = output.replacingOccurrences(of: host, with: "<host>", options: .caseInsensitive)
        }
        // IPv6 literals first (including IPv4-mapped addresses) while preserving HH:MM:SS timestamps.
        output = ipv6Regex.stringByReplacingMatches(
            in: output,
            range: NSRange(output.startIndex..., in: output),
            withTemplate: "<ip>"
        )
        // IPv4 (with optional :port)
        output = ipv4Regex.stringByReplacingMatches(
            in: output,
            range: NSRange(output.startIndex..., in: output),
            withTemplate: "<ip>"
        )
        // Bambu 15-character alphanumeric serials (e.g. 22E00A123456789)
        output = serialRegex.stringByReplacingMatches(
            in: output,
            range: NSRange(output.startIndex..., in: output),
            withTemplate: "<serial>"
        )
        return output
    }

    func exportJSON(
        capabilities: [CapabilitySummary],
        appVersion: String,
        serials: [String] = [],
        hosts: [String] = [],
        accessCodes: [String] = [],
        exportedAt: Date = Date()
    ) throws -> Data {
        let sanitizedEvents = buffer.map { entry in
            Entry(
                id: entry.id,
                timestamp: entry.timestamp,
                category: entry.category,
                printerName: Self.redact(entry.printerName, serials: serials, hosts: hosts, accessCodes: accessCodes),
                summary: Self.redact(entry.summary, serials: serials, hosts: hosts, accessCodes: accessCodes),
                outcome: entry.outcome
            )
        }
        let sanitizedCaps = capabilities.map { cap in
            CapabilitySummary(
                printerName: Self.redact(cap.printerName, serials: serials, hosts: hosts, accessCodes: accessCodes),
                modelName: cap.modelName,
                serialPrefix: String(cap.serialPrefix.prefix(3)).uppercased(),
                isOnline: cap.isOnline,
                amsCount: cap.amsCount,
                hasChamberSensor: cap.hasChamberSensor,
                hasChamberHeater: cap.hasChamberHeater,
                hasChamberLight: cap.hasChamberLight,
                hasDualLight: cap.hasDualLight,
                cameraEnabled: cap.cameraEnabled,
                nozzleCount: cap.nozzleCount,
                hasAuxFan: cap.hasAuxFan,
                hasChamberFan: cap.hasChamberFan
            )
        }
        let payload = ExportPayload(
            exportedAt: exportedAt,
            appVersion: appVersion,
            capabilities: sanitizedCaps,
            events: sanitizedEvents
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(payload)
    }
}
