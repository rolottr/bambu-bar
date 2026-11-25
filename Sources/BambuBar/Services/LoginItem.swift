import AppKit
import Observation
import ServiceManagement

/// Open at Login, backed by `SMAppService.mainApp`.
@MainActor
@Observable
final class LoginItem {
    static let shared = LoginItem()

    enum Status: Equatable {
        case enabled, requiresApproval, disabled
    }

    private static let offerShownKey = "loginOfferShown"

    private(set) var status: Status = .disabled
    private(set) var error: String?
    private(set) var offerShown = false

    private init() {
        _offerShown = UserDefaults.standard.bool(forKey: Self.offerShownKey)
        _status = Self.status(for: SMAppService.mainApp.status)
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
    }

    var isEnabled: Bool { status == .enabled || status == .requiresApproval }

    /// Whether the one-time post-connect offer may appear: not already shown, not enabled, and not a developer run.
    var shouldOffer: Bool {
        !offerShown && !isEnabled && !LaunchContext.isDeveloperRun
    }

    func refresh() {
        let next = Self.status(for: SMAppService.mainApp.status)
        if status != next { status = next }
    }

    nonisolated static func status(for service: SMAppService.Status) -> Status {
        switch service {
        case .enabled: .enabled
        case .requiresApproval: .requiresApproval
        // macOS can report notFound until this copy first registers.
        case .notRegistered, .notFound: .disabled
        @unknown default: .disabled
        }
    }

    func setEnabled(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            error = nil
            if on { answerOffer() }
        } catch {
            self.error = error.localizedDescription
        }
        refresh()
    }

    func answerOffer() {
        guard !offerShown else { return }
        offerShown = true
        if !LaunchContext.isDeveloperRun {
            UserDefaults.standard.set(true, forKey: Self.offerShownKey)
        }
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}

// MARK: - Onboarding Model

/// Capabilities used to decide which Setup Guide cards apply to the connected setup.
struct OnboardingCapabilities: Equatable, Sendable {
    var supportsCamera: Bool = true
    var hasMultiplePrinters: Bool = false
}

/// Features presented in fixed order inside the re-openable Setup Guide.
enum OnboardingFeature: String, CaseIterable, Identifiable, Sendable {
    case menuBar, widgets, notifications, camera, controls, fleet, done
    var id: String { rawValue }

    var symbol: String {
        switch self {
        case .menuBar: return "menubar.rectangle"
        case .widgets: return "widget.medium"
        case .notifications: return "bell.badge.fill"
        case .camera: return "video.fill"
        case .controls: return "slider.horizontal.3"
        case .fleet: return "square.grid.2x2.fill"
        case .done: return "checkmark.circle.fill"
        }
    }

    var shortTitle: String {
        switch self {
        case .menuBar: return String(localized: "Menu Bar", comment: "Setup guide chip")
        case .widgets: return String(localized: "Widgets", comment: "Setup guide chip")
        case .notifications: return String(localized: "Alerts", comment: "Setup guide chip")
        case .camera: return String(localized: "Camera", comment: "Setup guide chip")
        case .controls: return String(localized: "Controls", comment: "Setup guide chip")
        case .fleet: return String(localized: "Fleet", comment: "Setup guide chip")
        case .done: return String(localized: "Ready", comment: "Setup guide chip")
        }
    }

    var title: String {
        switch self {
        case .menuBar: return String(localized: "Menu Bar & Live Progress")
        case .widgets: return String(localized: "Desktop Widgets")
        case .notifications: return String(localized: "Print Notifications")
        case .camera: return String(localized: "Live Camera")
        case .controls: return String(localized: "Print Controls")
        case .fleet: return String(localized: "Multi-Printer Fleet")
        case .done: return String(localized: "Ready in the Menu Bar")
        }
    }

    var toggleLabel: String? {
        switch self {
        case .menuBar: return String(localized: "Show progress and time left in the menu bar")
        case .widgets: return nil
        case .notifications: return String(localized: "Notify when prints finish, pause, or fail")
        case .camera: return String(localized: "Enable live camera")
        case .controls: return String(localized: "Show pause, stop, and speed controls")
        case .fleet: return String(localized: "List all printers in the menu bar panel")
        case .done: return nil
        }
    }

    static func cards(for capabilities: OnboardingCapabilities?) -> [OnboardingFeature] {
        allCases.filter { feature in
            guard let capabilities else { return true }
            switch feature {
            case .camera: return capabilities.supportsCamera
            case .fleet: return capabilities.hasMultiplePrinters
            default: return true
            }
        }
    }
}

/// Snapshot of the real app settings that the Setup Guide can change.
struct OnboardingSettings: Equatable, Sendable {
    var showProgressInMenuBar: Bool
    var notifyFinished: Bool
    var notifyFailed: Bool
    var notifyPaused: Bool
    var cameraEnabled: Bool
    var showPrinterActions: Bool
    var panelShowsAllPrinters: Bool
}

/// Draft edited inside the Setup Guide. Nothing changes in the app until Finish is clicked; Skip discards the draft.
struct OnboardingDraft: Equatable, Sendable {
    var showProgressInMenuBar: Bool
    var notificationsEnabled: Bool
    var notificationsTouched: Bool = false
    var cameraEnabled: Bool
    var showPrinterActions: Bool
    var panelShowsAllPrinters: Bool

    init(from settings: OnboardingSettings) {
        showProgressInMenuBar = settings.showProgressInMenuBar
        notificationsEnabled = settings.notifyFinished || settings.notifyFailed || settings.notifyPaused
        notificationsTouched = false
        cameraEnabled = settings.cameraEnabled
        showPrinterActions = settings.showPrinterActions
        panelShowsAllPrinters = settings.panelShowsAllPrinters
    }

    func value(for feature: OnboardingFeature) -> Bool? {
        switch feature {
        case .menuBar: return showProgressInMenuBar
        case .notifications: return notificationsEnabled
        case .camera: return cameraEnabled
        case .controls: return showPrinterActions
        case .fleet: return panelShowsAllPrinters
        case .widgets, .done: return nil
        }
    }

    mutating func set(_ on: Bool, for feature: OnboardingFeature) {
        switch feature {
        case .menuBar: showProgressInMenuBar = on
        case .notifications:
            notificationsEnabled = on
            notificationsTouched = true
        case .camera: cameraEnabled = on
        case .controls: showPrinterActions = on
        case .fleet: panelShowsAllPrinters = on
        case .widgets, .done: break
        }
    }

    mutating func toggle(_ feature: OnboardingFeature) {
        guard let current = value(for: feature) else { return }
        set(!current, for: feature)
    }

    /// Produces the updated settings when Finish is chosen. Untouched notification flags are preserved.
    func applied(to current: OnboardingSettings) -> OnboardingSettings {
        var next = current
        next.showProgressInMenuBar = showProgressInMenuBar
        if notificationsTouched {
            next.notifyFinished = notificationsEnabled
            next.notifyFailed = notificationsEnabled
            next.notifyPaused = notificationsEnabled
        }
        next.cameraEnabled = cameraEnabled
        next.showPrinterActions = showPrinterActions
        next.panelShowsAllPrinters = panelShowsAllPrinters
        return next
    }
}
