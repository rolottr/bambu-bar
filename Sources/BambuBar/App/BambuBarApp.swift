import AppKit
import SwiftUI

struct BambuBarApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    private let store = PrinterStore.shared

    var body: some Scene {
        MenuBarExtra {
            MenuPanelHost()
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
            #if !APP_STORE
            Button("Manage License…") { MainWindow.show(.about) }
            Button("Support this project") { NSWorkspace.shared.open(PolarConfiguration.supportURL(from: .appMenu)) }
            #endif
            if UpdateChecker.isAvailable {
                Button("Check for Updates…") { UpdateChecker.shared.showUpdate() }
                    .disabled(!UpdateChecker.shared.canCheckForUpdates)
            }
            Button("Setup Guide…") { MainWindow.showSetupGuide() }
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
            ForEach(AppSection.allCases.filter { $0 != .fleet }) { section in
                Button(section.title) { MainWindow.show(section) }
                    .keyboardShortcut(section.shortcut)
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
            if store.showPrinterActions {
                Divider()
                Button("Pause") { store.pause() }
                    .disabled(store.status?.state != .printing && store.status?.state != .preparing)
                Button("Resume") { store.resume() }
                    .disabled(store.status?.state != .paused)
                Button("Stop…") { store.stopPrint() }
                    .disabled(!(store.status?.state.isActive ?? false))
            }
            Divider()
            if store.status?.chamberLight2On != nil {
                Button(store.chamberLightOn ? "Turn Left Off" : "Turn Left On") { store.toggleLight(.left) }
                    .keyboardShortcut("l")
                    .disabled(!store.isOnline)
                Button((store.chamberLight2On ?? false) ? "Turn Right Off" : "Turn Right On") { store.toggleLight(.right) }
                    .keyboardShortcut("l", modifiers: [.shift])
                    .disabled(!store.isOnline)
            } else {
                Button(store.chamberLightOn ? "Turn Chamber Light Off" : "Turn Chamber Light On") { store.toggleLight() }
                    .keyboardShortcut("l")
                    .disabled(!store.isOnline || store.status?.chamberLightOn == nil)
            }
            // Only with the camera on, in Settings and for this printer.
            if let session = store.current, session.showsCamera {
                Button("Show Camera") { CameraWindow.show(session) }
                    .keyboardShortcut("k")
            }
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
        if let dump = Probe.dumpStatusArguments {
            Probe.runDumpStatus(output: dump.output, inputs: dump.inputs)
            return
        }
        if let probe = Probe.cameraArguments {
            Probe.runCamera(host: probe.host, serial: probe.serial, code: probe.code, seconds: probe.seconds,
                            transport: probe.transport)
            return
        }
        Appearance.shared.applyMode()
        Navigation.shared.launchedInBackground = background
        let queuedURLs = SingleInstance.handOverIfRunning(background: background)
        SingleInstance.listen { MainWindow.show() }
        WidgetUpdate.refreshIfUpdated()
        PrinterStore.shared.start()
        #if !APP_STORE
        if !LaunchContext.isDeveloperRun {
            LicenseStore.shared.onAccessChange = { _ in
                UpdateChecker.shared.supporterStatusChanged()
            }
        }
        LicenseStore.shared.start()
        #endif
        UpdateChecker.shared.onOpenSettings = { MainWindow.show(.settings) }
        UpdateChecker.shared.start()
        observeAppWindows()
        if let dir = StoreMedia.directory {
            StoreMedia.run(into: dir)
            return
        }
        if let dir = ReadmeFrames.directory {
            ReadmeFrames.run(into: dir)
            return
        }
        if let dir = SnapshotRunner.directory {
            MainWindow.show()
            SnapshotRunner.run(into: dir)
            return
        }
        if !queuedURLs.isEmpty {
            application(NSApp, open: queuedURLs)
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
            MainActor.assumeIsolated {
                PrinterStore.shared.appOpened()
            }
        }
        center.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated {
                MainWindow.clampIfOpen()
                CameraWindow.clampAll()
            }
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
        // If a language restart is in flight, hand incoming URLs to the replacement instance.
        if SingleInstance.queueURLsIfRelaunching(urls, forPredecessor: ProcessInfo.processInfo.processIdentifier) {
            return
        }
        // bambubar://printer/<serial> from a widget, or bambubar://open/<section> to show a page.
        if let url = urls.first {
            if url.host == "printer", let serial = url.pathComponents.dropFirst().first {
                PrinterStore.shared.select(serial)
                MainWindow.show(.overview)
                return
            }
            if url.host == "open", let page = url.pathComponents.dropFirst().first {
                if let section = AppSection(rawValue: page) {
                    MainWindow.show(section)
                } else {
                    MainWindow.show()
                }
                return
            }
        }
        MainWindow.show(.overview)
    }

    func applicationWillTerminate(_ notification: Notification) {
        PrinterStore.shared.stop()
    }
}

enum AppSection: String, CaseIterable, Identifiable {
    case fleet, overview, filament, menuBar, notifications, widgets, settings, activity, about
    var id: String { rawValue }

    var title: String {
        switch self {
        case .fleet: return String(localized: "All Printers")
        case .overview: return String(localized: "Printer")
        case .filament: return String(localized: "Filament")
        case .widgets: return String(localized: "Widgets")
        case .menuBar: return String(localized: "Menu Bar", comment: "Sidebar page")
        case .notifications: return String(localized: "Notifications")
        case .settings: return String(localized: "Settings")
        case .activity: return String(localized: "Activity")
        case .about: return String(localized: "About")
        }
    }

    /// Keep existing shortcuts stable when adding sidebar pages.
    var shortcut: KeyEquivalent {
        switch self {
        case .fleet: return "0"
        case .overview: return "1"
        case .filament: return "2"
        case .widgets: return "3"
        case .settings: return "4"
        case .about: return "5"
        case .menuBar: return "7"
        case .notifications: return "8"
        case .activity: return "9"
        }
    }

    var icon: String {
        switch self {
        case .fleet: return "square.grid.2x2.fill"
        case .overview: return "printer.fill"
        case .filament: return "circle.circle.fill"
        case .widgets: return "widget.medium"
        case .menuBar: return "menubar.rectangle"
        case .notifications: return "bell.badge.fill"
        case .settings: return "gearshape.fill"
        case .activity: return "waveform.path.ecg"
        case .about: return "info.circle.fill"
        }
    }
}

/// Shared navigation so menus and the panel can open a specific page.
@MainActor
@Observable
final class Navigation {
    static let shared = Navigation()
    private static let onboardingShownKey = "onboardingSplashShown"

    var section: AppSection = .overview
    /// A discovered printer picked in the menu bar panel, to prefill the setup form.
    var setupCandidate: DiscoveredPrinter?
    /// The Printer page shows the setup form for another printer.
    var addingPrinter = false
    /// Re-openable Setup Guide sheet.
    var showOnboardingSplash = false
    var onboardingFirstRun = false
    @ObservationIgnored var launchedInBackground = false

    private(set) var onboardingShown: Bool

    private init() {
        _onboardingShown = UserDefaults.standard.bool(forKey: Self.onboardingShownKey)
    }

    func openOnboardingSplash(firstRun: Bool = false) {
        onboardingFirstRun = firstRun
        showOnboardingSplash = true
    }

    func markOnboardingShown() {
        guard !onboardingShown else { return }
        onboardingShown = true
        if !LaunchContext.isDeveloperRun {
            UserDefaults.standard.set(true, forKey: Self.onboardingShownKey)
        }
    }

    /// Shows the Setup Guide once after the user finishes setting up their first printer
    /// (never on login/background launches or developer runs).
    func presentFirstRunOnboardingIfNeeded() {
        guard !onboardingShown, !launchedInBackground, !LaunchContext.isDeveloperRun, !PrinterStore.shared.isDemo else { return }
        openOnboardingSplash(firstRun: true)
    }
}

/// The main window, hosted in AppKit so the Dock icon, menus and the panel can all reopen it.
@MainActor
enum MainWindow {
    // Fits four 150-point AMS cards, spacing, page padding, and the widest sidebar.
    static let minimumContentSize = NSSize(width: 960, height: 600)
    private static let defaultContentSize = NSSize(width: 980, height: 700)
    private static var window: NSWindow?
    private static var closeObserver: NSObjectProtocol?

    static func show(_ section: AppSection? = nil, setup: DiscoveredPrinter? = nil) {
        if let section { Navigation.shared.section = section }
        if let setup { Navigation.shared.setupCandidate = setup }
        if window == nil {
            let w = NSWindow(
                contentRect: NSRect(origin: .zero, size: defaultContentSize),
                styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                backing: .buffered, defer: false)
            w.title = "Bambu Bar"
            w.titlebarAppearsTransparent = true
            w.isReleasedWhenClosed = false
            w.toolbarStyle = .unified
            w.contentViewController = NSHostingController(
                rootView: MainView()
                    .environment(PrinterStore.shared)
                    .environment(Navigation.shared)
                    .environment(LoginItem.shared))
            w.setContentSize(defaultContentSize)
            w.center()
            w.setFrameAutosaveName("BambuBarMain")
            w.contentMinSize = minimumContentSize
            // Previously saved windows may be smaller than the new minimum.
            let restoredSize = w.contentRect(forFrameRect: w.frame).size
            w.setContentSize(NSSize(width: max(restoredSize.width, minimumContentSize.width),
                                    height: max(restoredSize.height, minimumContentSize.height)))
            WindowFrameClamp.clamp(w, minSize: minimumContentSize)
            window = w
            // A closed window would keep its views updating with every report (and the printer model
            // animating), so it's let go and built again on the next open.
            closeObserver = NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w,
                                                                   queue: .main) { _ in
                MainActor.assumeIsolated {
                    if let closeObserver { NotificationCenter.default.removeObserver(closeObserver) }
                    closeObserver = nil
                    window = nil
                    DispatchQueue.main.async { w.contentViewController = nil }
                }
            }
        } else if let w = window {
            WindowFrameClamp.clamp(w, minSize: minimumContentSize)
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    static func clampIfOpen() {
        guard let window else { return }
        WindowFrameClamp.clamp(window, minSize: minimumContentSize)
    }

    static func showSetupGuide() {
        show()
        Navigation.shared.openOnboardingSplash(firstRun: false)
    }

    static var current: NSWindow? { window }

    /// Opens the setup form to add another printer.
    static func addPrinter() {
        Navigation.shared.addingPrinter = true
        show(.overview)
    }
}
