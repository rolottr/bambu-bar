import AppKit
import Foundation

/// How this process was started (single source of truth shared by Keychain, Licensing,
/// LoginItem, SingleInstance, and UpdateChecker).
enum LaunchContext {
    /// Flags indicating a developer, demo, capture, or diagnostic run that must not trigger
    /// first-run offers, update checks, Polar calls, or real persistence.
    static let developerRunFlags: Set<String> = [
        "--demo",
        "--emulator",
        "--showcase",
        "--snapshot",
        "--readme-frames",
        "--store-media",
        "--probe",
        "--probe-camera",
        "--connect",
        "--dump-status",
    ]

    /// True when the process arguments include any developer/demo/probe/capture flag.
    static func isDeveloperRun(arguments: [String] = CommandLine.arguments) -> Bool {
        arguments.contains { developerRunFlags.contains($0) }
    }

    static var isDeveloperRun: Bool { isDeveloperRun() }

    /// True when Keychain I/O must remain strictly in-memory so developer/demo runs never touch
    /// the user's real Keychain. `--probe` and `--probe-camera` with an explicit `keychain` code
    /// argument are allowed to read a saved printer's access code.
    static func usesMemoryKeychain(arguments: [String] = CommandLine.arguments) -> Bool {
        guard isDeveloperRun(arguments: arguments) else { return false }
        for flag in ["--probe", "--probe-camera"] {
            if let index = arguments.firstIndex(of: flag),
               index + 3 < arguments.count,
               arguments[index + 3] == "keychain" {
                return false
            }
        }
        return true
    }

    /// `--background` starts the app the way a login launch does: menu bar only, no window, no Dock icon.
    static var requestsBackground: Bool { CommandLine.arguments.contains("--background") }

    /// True when macOS opened the app from the Open at Login list. Only meaningful during
    /// `applicationDidFinishLaunching`, while the open-application event is current.
    @MainActor
    static func wasLaunchedAtLogin() -> Bool {
        guard let event = NSAppleEventManager.shared().currentAppleEvent else { return false }
        return event.eventID == kAEOpenApplication
            && event.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
    }
}
