import AppKit
import SwiftUI

extension View {
    /// Hover tracking backed by an AppKit `NSTrackingArea` instead of SwiftUI's `.onHover`,
    /// which hit-tests the entire window hierarchy on every pointer move.
    func onHoverChange(_ action: @escaping (Bool) -> Void) -> some View {
        background(HoverTracker(onChange: action))
    }
}

private struct HoverTracker: NSViewRepresentable {
    let onChange: (Bool) -> Void

    func makeNSView(context: Context) -> TrackingView { TrackingView() }

    func updateNSView(_ view: TrackingView, context: Context) { view.onChange = onChange }

    final class TrackingView: NSView {
        var onChange: ((Bool) -> Void)?

        override init(frame: NSRect) {
            super.init(frame: frame)
            addTrackingArea(NSTrackingArea(
                rect: .zero,
                options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                owner: self
            ))
        }

        @available(*, unavailable)
        required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

        override func viewWillMove(toWindow newWindow: NSWindow?) {
            super.viewWillMove(toWindow: newWindow)
            if newWindow == nil {
                onChange?(false)
            }
        }

        override func mouseEntered(with event: NSEvent) { onChange?(true) }
        override func mouseExited(with event: NSEvent) { onChange?(false) }

        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}
