import AppKit
import SwiftUI

/// `--showcase <dir>` (with `--demo`): puts the menu bar panel and the widget sizes on screen in ordinary
/// windows and writes their window numbers to `<dir>/windows.txt`, so scripts can capture them with
/// `screencapture -l`. Used for README images and visual checks.
@MainActor
enum SnapshotRunner {
    static var directory: URL? {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--showcase"), i + 1 < args.count else { return nil }
        return URL(fileURLWithPath: args[i + 1], isDirectory: true)
    }

    private static var windows: [NSWindow] = []

    static func run(into dir: URL) {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--page"), i + 1 < args.count {
            if args[i + 1] == "add" {
                MainWindow.addPrinter()
            } else if args[i + 1] == "onboarding" {
                MainWindow.showSetupGuide()
            } else if let section = AppSection(rawValue: args[i + 1]) {
                MainWindow.show(section)
            }
        }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let store = PrinterStore.shared
        // Let the demo emit its first status before rendering.
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) {
            var lines: [String] = []
            let panel = host(MenuPanel().environment(store), material: .popover, origin: NSPoint(x: 80, y: 120))
            lines.append("panel \(panel.windowNumber)")
            let widgets = host(
                HStack(alignment: .top, spacing: 18) {
                    WidgetFrame(width: 170, height: 170) { SmallWidgetView(snapshot: store.snapshot, date: Date()) }
                    WidgetFrame(width: 364, height: 170) { MediumWidgetView(snapshot: store.snapshot, date: Date()) }
                    WidgetFrame(width: 364, height: 382) { LargeWidgetView(snapshot: store.snapshot, date: Date()) }
                }
                .padding(24)
                .background(LinearGradient(colors: [Color(red: 0.55, green: 0.42, blue: 0.33), Color(red: 0.22, green: 0.17, blue: 0.16)],
                                           startPoint: .topLeading, endPoint: .bottomTrailing)),
                material: nil, origin: NSPoint(x: 520, y: 120))
            lines.append("widgets \(widgets.windowNumber)")
            if let main = MainWindow.current { lines.append("main \(main.windowNumber)") }
            try? (lines.joined(separator: "\n") + "\n").write(to: dir.appendingPathComponent("windows.txt"), atomically: true, encoding: .utf8)
        }
    }

    private static func host<V: View>(_ view: V, material: NSVisualEffectView.Material?, origin: NSPoint) -> NSWindow {
        let hosting = NSHostingView(rootView: view)
        hosting.layoutSubtreeIfNeeded()
        let size = hosting.fittingSize
        let window = NSWindow(contentRect: NSRect(origin: origin, size: size), styleMask: [.borderless], backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = true
        window.level = .floating
        if let material {
            let effect = NSVisualEffectView(frame: NSRect(origin: .zero, size: size))
            effect.material = material
            effect.state = .active
            effect.blendingMode = .behindWindow
            effect.wantsLayer = true
            effect.layer?.cornerRadius = 18
            effect.layer?.masksToBounds = true
            hosting.frame = effect.bounds
            hosting.autoresizingMask = [.width, .height]
            effect.addSubview(hosting)
            window.contentView = effect
        } else {
            window.contentView = hosting
        }
        window.orderFrontRegardless()
        windows.append(window)
        return window
    }
}
