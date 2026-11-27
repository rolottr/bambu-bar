import Foundation

extension WidgetReloadPolicy.Connection {
    /// Where a printer's connection stands for its widget. The widget's link and whether a phase forces a reload
    /// both come from here, so a phase can't show on the widget without reaching it (WidgetReloadPolicyTests).
    init(_ phase: PrinterConnection.Phase) {
        switch phase {
        case .online: self = .online
        case .connecting: self = .connecting
        case .unauthorized: self = .unauthorized
        // A certificate that doesn't check out isn't retried, so no later phase would correct a recent "online".
        case .idle, .certificate: self = .idle
        // Retried: access may be turned on.
        case .offline, .localNetworkDenied: self = .offline
        }
    }
}
