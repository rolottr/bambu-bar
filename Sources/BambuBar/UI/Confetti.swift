import AppKit
import SwiftUI

/// A short burst of confetti over a window, for a moment worth celebrating (a supporter license activated).
/// It draws in a clear, click-through panel above the window, so it never covers or blocks the controls, and
/// stays off when the person has asked macOS to reduce motion.
@MainActor
enum Confetti {
    private static var panel: NSPanel?

    static func celebrate(over window: NSWindow? = nil) {
        let host = window ?? NSApp.keyWindow
        guard !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
              let frame = host?.frame ?? NSScreen.main?.visibleFrame, frame.width > 0, frame.height > 0 else { return }
        panel?.close()
        let burst = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        burst.isOpaque = false
        burst.backgroundColor = .clear
        burst.hasShadow = false
        burst.ignoresMouseEvents = true
        burst.isReleasedWhenClosed = false
        burst.level = max(.floating, (host?.level ?? .normal) + 1)
        burst.collectionBehavior = [.transient, .ignoresCycle, .fullScreenAuxiliary, .canJoinAllSpaces]
        burst.contentView = NSHostingView(rootView: ConfettiView())
        burst.orderFrontRegardless()
        panel = burst
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(ConfettiView.duration))
            guard panel === burst else { return }
            burst.close()
            panel = nil
        }
    }
}

/// Paper pieces shot from the window's two bottom corners, falling back under gravity while they flutter.
struct ConfettiView: View {
    nonisolated static let duration: Double = 3.4
    @State private var start = Date.now
    @State private var pieces = (0..<180).map { Piece(fromLeft: $0.isMultiple(of: 2)) }

    var body: some View {
        TimelineView(.animation) { timeline in
            Canvas { context, size in
                let t = timeline.date.timeIntervalSince(start)
                for piece in pieces { piece.draw(in: &context, size: size, time: t) }
            }
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
    }

    struct Piece {
        private static let colors: [Color] = [
            Color(red: 0.0, green: 0.68, blue: 0.26), Color(red: 1.0, green: 0.78, blue: 0.0),
            Color(red: 1.0, green: 0.36, blue: 0.29), Color(red: 0.2, green: 0.55, blue: 1.0),
            Color(red: 0.75, green: 0.35, blue: 0.95), Color(red: 0.2, green: 0.85, blue: 0.85),
        ]
        /// Gravity and air drag in fractions of the window height (and width) per second.
        private static let gravity = 1.5, drag = 1.1

        let fromLeft: Bool
        let delay = Double.random(in: 0...0.25)
        let speedX = Double.random(in: 0.25...0.95)
        let speedY = Double.random(in: 1.4...2.5)
        let spin = Double.random(in: -9...9)
        let flutter = Double.random(in: 6...14)
        let phase = Double.random(in: 0...(2 * .pi))
        let width = Double.random(in: 6...10)
        let length = Double.random(in: 9...15)
        let color = colors.randomElement()!

        func draw(in context: inout GraphicsContext, size: CGSize, time: Double) {
            let t = time - delay
            guard t > 0 else { return }
            // Drag slows the sideways throw; the fall is gravity's, with drag capping its speed.
            let slow = (1 - exp(-Self.drag * t)) / Self.drag
            let dx = speedX * slow * (fromLeft ? 1 : -1)
            let dy = -speedY * slow + Self.gravity * (t - slow) / Self.drag
            let x = (fromLeft ? 0 : 1) + dx + 0.015 * sin(flutter * 0.5 * t + phase)
            let y = 1.02 + dy
            let fade = min(1, max(0, (ConfettiView.duration - time) / 0.6))
            guard fade > 0, y < 1.1 else { return }

            var piece = context
            piece.translateBy(x: x * size.width, y: y * size.height)
            piece.rotate(by: .radians(spin * t + phase))
            piece.scaleBy(x: max(0.08, abs(cos(flutter * t + phase))), y: 1)
            piece.opacity = fade
            piece.fill(Path(roundedRect: CGRect(x: -width / 2, y: -length / 2, width: width, height: length), cornerRadius: 1.5),
                       with: .color(color))
        }
    }
}
