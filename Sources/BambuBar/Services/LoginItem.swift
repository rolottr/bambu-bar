import AppKit
import Observation
import ServiceManagement

/// Open at Login, backed by `SMAppService.mainApp`.
@MainActor
@Observable
final class LoginItem {
    static let shared = LoginItem()

    enum Status: Equatable {
        case enabled, requiresApproval, disabled, unavailable
    }

    private(set) var status: Status = .disabled
    private(set) var error: String?

    private init() { refresh() }

    var isEnabled: Bool { status == .enabled || status == .requiresApproval }

    func refresh() {
        status = switch SMAppService.mainApp.status {
        case .enabled: .enabled
        case .requiresApproval: .requiresApproval
        case .notRegistered: .disabled
        case .notFound: .unavailable
        @unknown default: .disabled
        }
    }

    func setEnabled(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
        refresh()
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}

/// How this process was started.
@MainActor
enum LaunchContext {
    /// True when macOS opened the app from the Open at Login list. Only meaningful during
    /// `applicationDidFinishLaunching`, while the open-application event is current.
    static func wasLaunchedAtLogin() -> Bool {
        guard let event = NSAppleEventManager.shared().currentAppleEvent else { return false }
        return event.eventID == kAEOpenApplication
            && event.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
    }

    /// `--background` starts the app the way a login launch does: menu bar only, no window, no Dock icon.
    static var requestsBackground: Bool { CommandLine.arguments.contains("--background") }

    /// Demo, showcase, README-frame and probe runs: no update checks.
    static var isDeveloperRun: Bool {
        let arguments = CommandLine.arguments
        return ["--demo", "--showcase", "--readme-frames", "--store-media", "--probe", "--connect", "--emulator", "--dump-status"]
            .contains { arguments.contains($0) }
    }
}
