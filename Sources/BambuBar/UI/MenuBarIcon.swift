import AppKit
import SwiftUI

/// The status item: a small printer glyph whose chamber fills with the print, plus optional text.
/// It follows the menu bar printer (Menu Bar page), not whichever printer the panel happens to show.
struct MenuBarLabel: View {
    @Environment(PrinterStore.self) private var store

    var body: some View {
        let session = store.menuBarSession
        let s = session?.status
        let online = session?.isOnline ?? false
        let active = online && (s?.state.isActive ?? false)
        // Reports arrive every second or so; the label below redraws only when what it shows changes.
        MenuBarLabelContent(icon: MenuBarIcon.image(state: online ? (s?.canceled == true ? .idle : s?.state) : nil,
                                                    progress: Double(s?.progress ?? 0) / 100,
                                                    alert: online && (s?.mostSevereIssue?.severity ?? .info) <= .serious,
                                                    offline: store.isConfigured && !online),
                            text: active ? s.flatMap(store.menuBarStyle.text) : nil,
                            accessibility: accessibilityText(session))
    }

    private func accessibilityText(_ session: PrinterSession?) -> String {
        guard let session, let s = session.status, session.isOnline else {
            return "Bambu Bar, \(session?.linkSummary ?? String(localized: "No printer set up"))"
        }
        let name = store.hasMultiplePrinters ? "\(session.config.name), " : ""
        if s.state.isActive { return "Bambu Bar, \(name)" + String(localized: "\(s.state.title) \(s.progress) percent", comment: "VoiceOver: state, then progress") }
        return "Bambu Bar, \(name)\(s.stateTitle)"
    }
}

private struct MenuBarLabelContent: View, Equatable {
    /// One cached image per look, so the same look compares equal.
    var icon: NSImage
    var text: String?
    var accessibility: String

    var body: some View {
        HStack(spacing: 4) {
            Image(nsImage: icon)
            if let text {
                Text(text).monospacedDigit()
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibility)
    }
}

enum MenuBarIcon {
    private static var cache: [String: NSImage] = [:]

    /// A template image, 18 pt tall: the printer's outline, the part rising inside it with progress,
    /// a pause or alert mark when needed, and a dashed outline while offline.
    static func image(state: JobState?, progress: Double, alert: Bool, offline: Bool) -> NSImage {
        let step = Int((progress * 20).rounded())  // 5 % steps are plenty at this size
        let key = "\(state?.rawValue ?? "none")|\(step)|\(alert)|\(offline)"
        if let cached = cache[key] { return cached }
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { _ in
            draw(state: state, fill: Double(step) / 20, alert: alert, offline: offline)
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Bambu Bar"
        cache[key] = image
        return image
    }

    private static func draw(state: JobState?, fill: Double, alert: Bool, offline: Bool) {
        NSColor.black.set()
        let frame = NSRect(x: 2.25, y: 1.75, width: 13.5, height: 14.5)
        let outline = NSBezierPath(roundedRect: frame, xRadius: 2.6, yRadius: 2.6)
        outline.lineWidth = 1.5
        if offline { outline.setLineDash([2.2, 1.6], count: 2, phase: 0) }
        outline.stroke()
        if offline { return }

        // Gantry with the toolhead.
        let gantryY: CGFloat = 5.2
        NSBezierPath(rect: NSRect(x: frame.minX + 1, y: gantryY - 0.4, width: frame.width - 2, height: 0.8)).fill()
        // Build plate.
        let plateY: CGFloat = 13.4
        NSBezierPath(roundedRect: NSRect(x: frame.minX + 2.2, y: plateY, width: frame.width - 4.4, height: 1.3), xRadius: 0.6, yRadius: 0.6).fill()

        switch state {
        case .paused:
            for x in [6.6, 9.9] as [CGFloat] {
                NSBezierPath(roundedRect: NSRect(x: x, y: 7.2, width: 1.6, height: 4.8), xRadius: 0.8, yRadius: 0.8).fill()
            }
        case .failed:
            mark()
        case .printing, .preparing, .finished:
            let maxH = plateY - gantryY - 2.6
            let h = state == .finished ? maxH : max(0.9, maxH * CGFloat(fill))
            NSBezierPath(roundedRect: NSRect(x: 6.2, y: plateY - h, width: 5.6, height: h), xRadius: 0.9, yRadius: 0.9).fill()
            if state != .finished {
                // Toolhead on top of the part.
                let headY = max(gantryY + 0.6, plateY - h - 2.3)
                NSBezierPath(roundedRect: NSRect(x: 7.3, y: headY, width: 3.4, height: 1.8), xRadius: 0.5, yRadius: 0.5).fill()
            }
        default:
            NSBezierPath(roundedRect: NSRect(x: frame.minX + 1.6, y: gantryY + 0.6, width: 3.2, height: 1.8), xRadius: 0.5, yRadius: 0.5).fill()
        }
        if alert && state != .failed {
            // Small badge in the corner, cut out of the outline.
            let badge = NSRect(x: 11.2, y: 0.2, width: 6.6, height: 6.6)
            NSGraphicsContext.current?.compositingOperation = .clear
            NSBezierPath(ovalIn: badge.insetBy(dx: -1.2, dy: -1.2)).fill()
            NSGraphicsContext.current?.compositingOperation = .sourceOver
            NSBezierPath(ovalIn: badge).fill()
            NSGraphicsContext.current?.compositingOperation = .clear
            NSBezierPath(roundedRect: NSRect(x: badge.midX - 0.6, y: badge.minY + 1.3, width: 1.2, height: 2.6), xRadius: 0.6, yRadius: 0.6).fill()
            NSBezierPath(ovalIn: NSRect(x: badge.midX - 0.65, y: badge.minY + 4.4, width: 1.3, height: 1.3)).fill()
            NSGraphicsContext.current?.compositingOperation = .sourceOver
        }
    }

    private static func mark() {
        NSBezierPath(roundedRect: NSRect(x: 8.2, y: 6.6, width: 1.6, height: 4.4), xRadius: 0.8, yRadius: 0.8).fill()
        NSBezierPath(ovalIn: NSRect(x: 8.1, y: 11.8, width: 1.8, height: 1.8)).fill()
    }
}
