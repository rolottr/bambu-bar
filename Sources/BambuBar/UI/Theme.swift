import SwiftUI

/// A rounded module like Control Center's: a faint fill and hairline that sit well on glass in light and dark.
struct PanelBackground: ViewModifier {
    var radius: CGFloat = 18
    var padding: CGFloat = 14
    var tint: Color?

    func body(content: Content) -> some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background {
                RoundedRectangle(cornerRadius: radius, style: .continuous)
                    .fill(tint.map { $0.opacity(0.12) } ?? Color.primary.opacity(0.05))
                    .overlay(
                        RoundedRectangle(cornerRadius: radius, style: .continuous)
                            .strokeBorder(LinearGradient(colors: [Color.primary.opacity(0.10), Color.primary.opacity(0.03)],
                                                         startPoint: .top, endPoint: .bottom), lineWidth: 0.75)
                    )
            }
    }
}

extension View {
    func panel(radius: CGFloat = 18, padding: CGFloat = 14, tint: Color? = nil) -> some View {
        modifier(PanelBackground(radius: radius, padding: padding, tint: tint))
    }
}

/// Small uppercase heading above a group.
struct SectionLabel: View {
    var title: Text
    var trailing: String?

    init(_ title: LocalizedStringKey, trailing: String? = nil) {
        self.title = Text(title)
        self.trailing = trailing
    }

    init(verbatim title: String, trailing: String? = nil) {
        self.title = Text(verbatim: title)
        self.trailing = trailing
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            title
                .textCase(.uppercase)
                .font(.system(size: 10.5, weight: .semibold))
                .tracking(0.6)
                .foregroundStyle(.secondary)
            Spacer(minLength: 6)
            if let trailing {
                Text(trailing).font(.system(size: 11)).foregroundStyle(.tertiary).lineLimit(1)
            }
        }
    }
}

/// A temperature readout: value large, target small, a hairline gauge underneath.
/// Nozzle (two on H2D, H2C and X2D), bed, and the chamber where the model has a sensor; scaled to the model's
/// maximum temperatures.
struct TemperatureTiles: View {
    var status: PrinterStatus
    var model: PrinterModel?
    /// Stretch to the row's height (beside the taller fan tile on the Printer page).
    var fillsHeight = false
    /// The chamber tile, on models with a sensor; the menu bar panel can leave it out (Settings).
    var showsChamber = true

    var body: some View {
        if let left = status.secondNozzle {
            // Both nozzles: the idle one can be still warm, so the one printing gets a dot.
            let printing = status.state.isActive
            TemperatureTile(fillsHeight: fillsHeight, title: "Left", symbol: "flame.fill", temperature: left, tint: Palette.nozzle,
                            inUse: printing && status.leftNozzleInUse == true)
            TemperatureTile(fillsHeight: fillsHeight, title: "Right", symbol: "flame.fill", temperature: status.nozzle, tint: Palette.nozzle,
                            inUse: printing && status.leftNozzleInUse == false)
        } else {
            TemperatureTile(fillsHeight: fillsHeight, title: "Nozzle", symbol: "flame.fill", temperature: status.nozzle, tint: Palette.nozzle)
        }
        TemperatureTile(fillsHeight: fillsHeight, title: "Bed", symbol: "heat.waves", temperature: status.bed, tint: Palette.bed)
        if showsChamber {
            if let chamber = status.chamber {
                if model?.chamberHeaterMax != nil {
                    TemperatureTile(fillsHeight: fillsHeight, title: "Chamber", symbol: "thermometer.medium",
                                    temperature: Temperature(current: chamber, target: status.chamberTarget ?? 0),
                                    tint: Palette.chamber)
                } else {
                    TemperatureTile(fillsHeight: fillsHeight, title: "Chamber", symbol: "thermometer.medium", temperature: nil, plain: chamber,
                                    tint: Palette.chamber)
                }
            } else if model?.hasChamberSensor == true {
                // A model with a sensor that hasn't reported yet. An unknown model shows a chamber only once it
                // reports one.
                TemperatureTile(fillsHeight: fillsHeight, title: "Chamber", symbol: "thermometer.medium", temperature: nil, plain: nil, tint: Palette.chamber)
            }
        }
    }
}

/// One temperature, big. While a heater is still heating or cooling toward its target, the target follows it
/// in small type ("164° → 220°"); once there, or with the heater off, only the reading shows.
struct TemperatureTile: View {
    var fillsHeight = false
    var title: LocalizedStringKey
    var symbol: String
    var temperature: Temperature?
    var plain: Double? = nil
    var tint: Color
    /// The nozzle printing on a two-nozzle model.
    var inUse = false

    var body: some View {
        let current = temperature?.current ?? plain
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 4) {
                Image(systemName: symbol).font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(heating ? tint : .secondary)
                    .symbolEffect(.pulse, options: .repeating, isActive: changing)
                Text(title).font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
                    .lineLimit(1).minimumScaleFactor(0.75)
                if inUse {
                    Circle().fill(tint).frame(width: 5, height: 5).help("Printing with this nozzle")
                }
            }
            HStack(alignment: .firstTextBaseline, spacing: 5) {
                Text(current.map { "\(Int($0.rounded()))°" } ?? "–")
                    .font(.system(size: 22, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .contentTransition(.numericText())
                if changing, let target = temperature?.target {
                    Text("→ \(Int(target))°")
                        .font(.system(size: 12, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .foregroundStyle(tint)
                }
                Spacer(minLength: 0)
            }
            .lineLimit(1)
            .minimumScaleFactor(0.7)
        }
        .frame(maxHeight: fillsHeight ? .infinity : nil, alignment: .topLeading)
        .panel(radius: 14, padding: 11)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text("\(Text(title)), \(accessibilityValue)", comment: "VoiceOver: tile name, then its reading"))
    }

    private var accessibilityValue: Text {
        let current = temperature?.current ?? plain
        var parts: [String] = []
        if inUse { parts.append(String(localized: "in use", comment: "A nozzle that is printing, read by VoiceOver")) }
        parts.append(current.map { String(localized: "\(Int($0)) degrees") } ?? String(localized: "unknown", comment: "Temperature not reported yet"))
        if changing, let t = temperature {
            parts.append(t.current < t.target ? String(localized: "heating to \(Int(t.target))") : String(localized: "cooling to \(Int(t.target))"))
        }
        return Text(verbatim: parts.joined(separator: ", "))
    }

    private var heating: Bool { (temperature?.target ?? 0) > 0 }
    /// Still on its way to the target.
    private var changing: Bool {
        guard let t = temperature, t.target > 0 else { return false }
        return !t.isAtTarget
    }
}

/// Round glass-like control used for the print controls.
struct ControlButtonStyle: ButtonStyle {
    var tint: Color = .primary
    var prominent = false

    func makeBody(configuration: Configuration) -> some View {
        ControlButtonBody(configuration: configuration, tint: tint, prominent: prominent)
    }
}

private struct ControlButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let tint: Color
    let prominent: Bool
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        configuration.label
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(prominent ? Color.white : tint)
            .frame(maxWidth: .infinity, minHeight: 34)
            .background {
                RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .fill(prominent ? AnyShapeStyle(tint.gradient)
                          : tint == .primary ? AnyShapeStyle(Color.primary.opacity(configuration.isPressed ? 0.12 : 0.06))
                          : AnyShapeStyle(tint.opacity(configuration.isPressed ? 0.22 : 0.12)))
                    .overlay(RoundedRectangle(cornerRadius: 11, style: .continuous)
                        .strokeBorder(Color.primary.opacity(prominent ? 0 : 0.07), lineWidth: 0.75))
            }
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .opacity(isEnabled ? 1 : 0.45)
            .animation(.spring(duration: 0.2), value: configuration.isPressed)
            .contentShape(Rectangle())
    }
}

/// A row of label/value pairs for detail sections.
struct DetailRow: View {
    var title: LocalizedStringKey
    var value: String
    var symbol: String?

    var body: some View {
        HStack(spacing: 8) {
            if let symbol {
                Image(systemName: symbol).font(.system(size: 11)).foregroundStyle(.secondary).frame(width: 16)
            }
            Text(title).foregroundStyle(.secondary)
            Spacer(minLength: 8)
            Text(value).fontWeight(.medium).multilineTextAlignment(.trailing)
        }
        .font(.system(size: 12))
    }
}

/// Relative time like "3h ago", "yesterday".
func relativeTime(_ date: Date) -> String {
    let formatter = RelativeDateTimeFormatter()
    formatter.unitsStyle = .short
    return formatter.localizedString(for: date, relativeTo: Date())
}

/// Asks the printer for a full report. The arrow spins until fresh data arrives, and the label says how
/// recent the data is, so it's clear what the button does and that it worked.
struct RefreshControl: View {
    @Environment(PrinterStore.self) private var store
    /// Icon only, for the menu bar panel's header.
    var compact = false

    @State private var spinning = false
    @State private var requestedAt: Date?

    var body: some View {
        HStack(spacing: 8) {
            if !compact {
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    Text(label(now: context.date))
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                        .contentTransition(.numericText())
                }
            }
            Button(action: refresh) {
                // The symbol effect stops with `spinning` (finishing its turn); a repeat-forever
                // rotation animation can't be cancelled and kept spinning after the refresh.
                Image(systemName: "arrow.clockwise")
                    .symbolEffect(.rotate.clockwise.wholeSymbol, options: .repeat(.continuous), isActive: spinning)
            }
            .buttonStyle(HeaderIconStyle())
            .help("Ask the printer for a full status report")
            .disabled(!store.isConfigured)
        }
        .onChange(of: store.current?.lastHeard) { _, heard in
            // A reply after the request ends the spin.
            if spinning, let heard, let requestedAt, heard >= requestedAt { finish() }
        }
    }

    private func label(now: Date) -> String {
        if spinning { return String(localized: "Refreshing…") }
        guard let heard = store.current?.lastHeard, store.isOnline else { return store.linkSummary }
        let seconds = Int(now.timeIntervalSince(heard))
        if seconds < 3 { return String(localized: "Updated just now") }
        if seconds < 60 { return String(localized: "Updated \(seconds)s ago", comment: "Seconds since the last report, short") }
        return String(localized: "Updated \(relativeTime(heard))", comment: "Relative time such as “3 min. ago”")
    }

    private func refresh() {
        guard !spinning else { return }
        requestedAt = Date()
        spinning = true
        store.refresh()
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(4))
            if spinning { finish() }
        }
    }

    private func finish() {
        spinning = false
    }
}
