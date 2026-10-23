import AppKit
import SwiftUI

@main
struct BambuBarApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    private let store = PrinterStore.shared

    var body: some Scene {
        MenuBarExtra {
            MenuPanel()
                .environment(store)
        } label: {
            MenuBarLabel()
                .environment(store)
        }
        .menuBarExtraStyle(.window)
        .commands { AppCommands(store: store) }
    }
}

/// Menus shown while the window is open (otherwise the app lives in the menu bar only).
struct AppCommands: Commands {
    let store: PrinterStore

    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button("About Bambu Bar") { MainWindow.show(.about) }
            Button("Check for Updates…") { Task { await UpdateChecker.shared.checkAndReport() } }
                .disabled(UpdateChecker.shared.state == .checking)
        }
        CommandGroup(replacing: .appSettings) {
            Button("Settings…") { MainWindow.show(.settings) }
                .keyboardShortcut(",")
        }
        CommandGroup(replacing: .newItem) {}
        CommandGroup(before: .sidebar) {
            if store.hasMultiplePrinters {
                Button(AppSection.fleet.title) { MainWindow.show(.fleet) }
                    .keyboardShortcut("0")
            }
            ForEach(Array(AppSection.allCases.filter { $0 != .fleet }.enumerated()), id: \.element) { index, section in
                Button(section.title) { MainWindow.show(section) }
                    .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")))
            }
            Divider()
        }
        CommandMenu("Printer") {
            if store.hasMultiplePrinters {
                ForEach(store.sessions) { session in
                    Button(session.config.name) { store.select(session.id) }
                        .disabled(session.id == store.current?.id)
                }
                Divider()
            }
            Button("Add Printer…") { MainWindow.addPrinter() }
            Button("Refresh") { store.refresh() }
                .keyboardShortcut("r")
            Divider()
            Button("Pause") { store.pause() }
                .disabled(store.status?.state != .printing && store.status?.state != .preparing)
            Button("Resume") { store.resume() }
                .disabled(store.status?.state != .paused)
            Button("Stop…") { store.stopPrint() }
                .disabled(!(store.status?.state.isActive ?? false))
            Divider()
            Button(store.chamberLightOn ? "Turn Chamber Light Off" : "Turn Chamber Light On") { store.toggleLight() }
                .keyboardShortcut("l")
                .disabled(!store.isOnline || store.status?.chamberLightOn == nil)
            Button("Show Camera") { if let session = store.current { CameraWindow.show(session) } }
                .keyboardShortcut("k")
                .disabled(store.current?.isDemo ?? true)
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Info.plist starts the app as a menu bar accessory (no Dock icon). Opened by hand it shows its
        // window, which brings the Dock icon and menus along; opened at login it stays in the menu bar.
        let background = LaunchContext.wasLaunchedAtLogin() || LaunchContext.requestsBackground
        if let probe = Probe.arguments {
            Probe.run(host: probe.host, serial: probe.serial, code: probe.code, seconds: probe.seconds)
            return
        }
        if let probe = Probe.cameraArguments {
            Probe.runCamera(host: probe.host, serial: probe.serial, code: probe.code, seconds: probe.seconds,
                            transport: probe.transport)
            return
        }
        PrinterStore.shared.start()
        UpdateChecker.shared.startAutomaticChecks()
        WidgetUpdate.refreshIfUpdated()
        observeAppWindows()
        if let dir = ReadmeFrames.directory {
            ReadmeFrames.run(into: dir)
            return
        }
        if let dir = SnapshotRunner.directory {
            MainWindow.show()
            SnapshotRunner.run(into: dir)
            return
        }
        if !background { MainWindow.show() }
    }

    /// While a window is open the app has a Dock icon and menus; once the last one closes it steps back
    /// to the menu bar (no Dock icon, not in ⌘-Tab) and keeps running.
    private func observeAppWindows() {
        let center = NotificationCenter.default
        center.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { note in
            MainActor.assumeIsolated {
                guard let window = note.object as? NSWindow, Self.isAppWindow(window),
                      NSApp.activationPolicy() != .regular else { return }
                NSApp.setActivationPolicy(.regular)
                NSApp.activate(ignoringOtherApps: true)
            }
        }
        center.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { note in
            MainActor.assumeIsolated {
                guard let closing = note.object as? NSWindow, Self.isAppWindow(closing) else { return }
                let othersOpen = NSApp.windows.contains { $0 !== closing && $0.isVisible && Self.isAppWindow($0) }
                if !othersOpen { NSApp.setActivationPolicy(.accessory) }
            }
        }
        center.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { LoginItem.shared.refresh() }
        }
    }

    /// The main window; not the menu bar panel, sheets or alerts.
    private static func isAppWindow(_ window: NSWindow) -> Bool {
        !(window is NSPanel) && !window.isSheet && window.styleMask.contains(.titled) && window.canBecomeMain
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        // Opening the app again from Finder or Spotlight while it lives in the menu bar brings the window back.
        MainWindow.show()
        return true
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        // bambubar://printer/<serial> from a widget: show that printer.
        if let url = urls.first, url.host == "printer", let serial = url.pathComponents.dropFirst().first {
            PrinterStore.shared.select(serial)
        }
        MainWindow.show(.overview)
    }

    func applicationWillTerminate(_ notification: Notification) {
        PrinterStore.shared.stop()
    }
}

enum AppSection: String, CaseIterable, Identifiable {
    case fleet, overview, filament, widgets, settings, about
    var id: String { rawValue }

    var title: String {
        switch self {
        case .fleet: return "All Printers"
        case .overview: return "Printer"
        case .filament: return "Filament"
        case .widgets: return "Widgets"
        case .settings: return "Settings"
        case .about: return "About"
        }
    }

    var icon: String {
        switch self {
        case .fleet: return "square.grid.2x2.fill"
        case .overview: return "printer.fill"
        case .filament: return "circle.circle.fill"
        case .widgets: return "widget.medium"
        case .settings: return "gearshape.fill"
        case .about: return "info.circle.fill"
        }
    }
}

/// Shared navigation so menus and the panel can open a specific page.
@MainActor
@Observable
final class Navigation {
    static let shared = Navigation()
    var section: AppSection = .overview
    /// A discovered printer picked in the menu bar panel, to prefill the setup form.
    var setupCandidate: DiscoveredPrinter?
    /// The Printer page shows the setup form for another printer.
    var addingPrinter = false
}

/// The main window, hosted in AppKit so the Dock icon, menus and the panel can all reopen it.
@MainActor
enum MainWindow {
    private static var window: NSWindow?

    static func show(_ section: AppSection? = nil, setup: DiscoveredPrinter? = nil) {
        if let section { Navigation.shared.section = section }
        if let setup { Navigation.shared.setupCandidate = setup }
        if window == nil {
            let w = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 980, height: 700),
                styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                backing: .buffered, defer: false)
            w.title = "Bambu Bar"
            w.titlebarAppearsTransparent = true
            w.isReleasedWhenClosed = false
            w.minSize = NSSize(width: 820, height: 600)
            w.toolbarStyle = .unified
            w.contentViewController = NSHostingController(
                rootView: MainView()
                    .environment(PrinterStore.shared)
                    .environment(Navigation.shared)
                    .environment(LoginItem.shared))
            w.setContentSize(NSSize(width: 980, height: 700))
            w.center()
            w.setFrameAutosaveName("BambuBarMain")
            window = w
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    static var current: NSWindow? { window }

    /// Opens the setup form to add another printer.
    static func addPrinter() {
        Navigation.shared.addingPrinter = true
        show(.overview)
    }
}
