import SwiftUI

extension PrinterSession {
    /// Where the printer sorts in the fleet: what needs a person first, then what finishes soonest.
    var attentionRank: (Int, Int) {
        guard let s = status, isOnline else {
            return phase == .connecting ? (5, 0) : (6, 0)
        }
        if s.state == .failed && !s.canceled { return (0, 0) }
        if s.state == .paused || (s.mostSevereIssue.map { $0.severity <= .serious } ?? false) { return (1, 0) }
        switch s.state {
        case .printing, .preparing: return (2, s.remainingMinutes)
        case .finished: return (3, 0)
        default: return (4, 0)
        }
    }

    var needsAttention: Bool { attentionRank.0 <= 1 }

    /// The line under the name: what went wrong, else the job, else the model.
    var fleetDetail: String {
        guard let s = status, isOnline else { return "\(config.modelName) · \(linkSummary)" }
        if let issue = s.mostSevereIssue, issue.severity <= .serious { return issue.message }
        if !s.jobName.isEmpty && s.state != .idle { return s.jobName }
        return "\(config.modelName) · \(s.stateTitle)"
    }
}

extension PrinterStore {
    /// Every printer, the ones that need someone first, then by soonest finish, then by name.
    var fleetSessions: [PrinterSession] {
        sessions.sorted { a, b in
            let ra = a.attentionRank, rb = b.attentionRank
            if ra != rb { return ra < rb }
            return a.config.name.localizedStandardCompare(b.config.name) == .orderedAscending
        }
    }

    /// "6 printing · 1 paused · 2 finished · 1 offline".
    var fleetSummary: String {
        var counts: [String] = []
        func add(_ title: (Int) -> String, _ match: (PrinterSession) -> Bool) {
            let n = sessions.filter(match).count
            if n > 0 { counts.append(title(n)) }
        }
        add({ String(localized: "\($0) need attention", comment: "Printers") }) { $0.attentionRank.0 == 0 }
        add({ String(localized: "\($0) printing", comment: "Printers") }) { $0.isOnline && [.printing, .preparing].contains($0.status?.state) }
        add({ String(localized: "\($0) paused", comment: "Printers") }) { $0.isOnline && $0.status?.state == .paused }
        add({ String(localized: "\($0) finished", comment: "Printers") }) { $0.isOnline && $0.status?.state == .finished }
        add({ String(localized: "\($0) ready", comment: "Printers") }) { $0.isOnline && $0.status?.state == .idle }
        add({ String(localized: "\($0) offline", comment: "Printers") }) { !$0.isOnline }
        return counts.joined(separator: " · ")
    }
}

/// Every printer at a glance, for people with more than a couple.
struct FleetPage: View {
    @Environment(PrinterStore.self) private var store
    @Environment(Navigation.self) private var navigation

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            PageHeader(title: "All Printers", subtitle: String(localized: "\(store.sessions.count) printers") + " · " + store.fleetSummary) {
                Button("Add Printer…") { MainWindow.addPrinter() }
            }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 250), spacing: 14)], spacing: 14) {
                ForEach(store.fleetSessions) { session in
                    FleetCard(session: session) {
                        store.select(session.id)
                        navigation.section = .overview
                    }
                }
            }
        }
    }
}

/// One printer in the fleet grid: its render, state and job.
struct FleetCard: View {
    var session: PrinterSession
    var open: () -> Void

    var body: some View {
        Button(action: open) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(alignment: .center, spacing: 12) {
                    PrinterArt(status: session.status, model: session.config.modelName, dimmed: !session.isOnline, animated: false)
                        .frame(width: 64, height: 76)
                    VStack(alignment: .leading, spacing: 5) {
                        HStack(spacing: 6) {
                            Text(session.config.name).font(.system(size: 13.5, weight: .semibold)).lineLimit(1)
                            Spacer(minLength: 4)
                            StateChip(session: session)
                        }
                        Text(session.fleetDetail).font(.system(size: 11.5)).foregroundStyle(.secondary).lineLimit(1)
                        FleetProgress(session: session)
                    }
                }
                Divider()
                // What the printer really is and where it's reached, whatever it's called in Bambu Bar.
                Text("Bambu Lab \(session.config.modelName) · \(session.config.host)")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .panel(radius: 16, padding: 0, tint: session.needsAttention ? tint : nil)
        .overlay {
            if session.needsAttention {
                RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(tint.opacity(0.55), lineWidth: 1)
            }
        }
        .help("\(session.config.name): \(session.fleetDetail)")
    }

    private var tint: Color { session.status.map(Palette.color(for:)) ?? Palette.failed }
}

/// The printer's state as a small coloured label.
struct StateChip: View {
    var session: PrinterSession

    var body: some View {
        Text(title)
            .font(.system(size: 10.5, weight: .semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 7).padding(.vertical, 2)
            .background(Capsule().fill(color.opacity(0.14)))
            .lineLimit(1)
            .fixedSize()
    }

    private var title: String {
        guard let s = session.status, session.isOnline else {
            return session.phase == .connecting ? String(localized: "Connecting") : String(localized: "Offline")
        }
        return s.stateTitle
    }

    private var color: Color {
        guard let s = session.status, session.isOnline else { return session.phase == .connecting ? Palette.paused : .secondary }
        return Palette.color(for: s)
    }
}

/// Progress and time left for a printing job; finish time or nothing otherwise.
struct FleetProgress: View {
    var session: PrinterSession
    var compact = false

    var body: some View {
        if let s = session.status, session.isOnline, s.state.isActive || s.state == .finished {
            VStack(alignment: .leading, spacing: 4) {
                FilamentProgressBar(progress: Double(s.progress) / 100, tint: Palette.color(for: s), height: compact ? 4 : 5)
                HStack {
                    Text("\(s.progress)%").fontWeight(.semibold).monospacedDigit()
                    Spacer()
                    Text(timing(s)).foregroundStyle(.secondary).monospacedDigit()
                }
                .font(.system(size: compact ? 10 : 10.5))
            }
        } else {
            // Same height as a job, so cards line up.
            Color.clear.frame(height: compact ? 18 : 21)
        }
    }

    private func timing(_ s: PrinterStatus) -> String {
        switch s.state {
        case .finished: return String(localized: "Done", comment: "A finished print")
        case .paused: return String(localized: "\(Format.duration(minutes: s.remainingMinutes)) left · paused")
        default:
            guard let end = s.estimatedEnd else { return s.stageText ?? "" }
            let left = Format.duration(minutes: s.remainingMinutes)
            // The finish time only when it's today; "tomorrow 06:01" doesn't fit beside the percentage.
            return Calendar.current.isDateInToday(end) ? "\(left) · \(Format.finish(end))" : String(localized: "\(left) left", comment: "Time left, such as “2h 05m left”")
        }
    }
}

/// The menu panel's list of every printer: one row each, tap to see it.
struct FleetList: View {
    @Environment(PrinterStore.self) private var store
    var open: (PrinterSession) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ScrollView {
                VStack(spacing: 6) {
                    ForEach(store.fleetSessions) { session in
                        Button { open(session) } label: { row(session) }
                            .buttonStyle(.plain)
                    }
                }
            }
            .frame(maxHeight: 440)
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func row(_ session: PrinterSession) -> some View {
        HStack(spacing: 10) {
            PrinterArt(status: session.status, model: session.config.modelName, dimmed: !session.isOnline, animated: false)
                .frame(width: 30, height: 36)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(session.config.name).font(.system(size: 12.5, weight: .semibold)).lineLimit(1)
                    Spacer(minLength: 4)
                    StateChip(session: session)
                }
                if let s = session.status, session.isOnline, s.state.isActive || s.state == .finished {
                    FleetProgress(session: session, compact: true)
                } else {
                    Text(session.fleetDetail).font(.system(size: 10.5)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous)
            .fill(session.needsAttention ? Palette.color(for: session.status ?? PrinterStatus()).opacity(0.12) : Color.primary.opacity(0.05)))
        .contentShape(Rectangle())
        .help("\(session.config.name): \(session.fleetDetail)")
    }
}
