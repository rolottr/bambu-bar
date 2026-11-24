import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Live-only-when-visible diagnostics page: connected printer capabilities, filterable event stream,
/// and sanitized JSON export (no access codes, full serials, or IP addresses).
struct ActivityPage: View {
    @Environment(PrinterStore.self) private var store
    @State private var filter: ActivityLog.Category?

    private var filteredEntries: [ActivityLog.Entry] {
        let entries = store.activity.visibleEntries.reversed()
        guard let filter else { return Array(entries) }
        return entries.filter { $0.category == filter }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            PageHeader(title: "Activity") {
                HStack(spacing: 8) {
                    Button { store.activity.clear() } label: {
                        Label("Clear", systemImage: "trash")
                    }
                    .controlSize(.small)
                    .disabled(store.activity.visibleEntries.isEmpty)
                    .fixedSize()

                    Button { exportJSON() } label: {
                        Label("Export…", systemImage: "square.and.arrow.up")
                    }
                    .controlSize(.small)
                    .fixedSize()
                }
            }

            capabilitiesCard
            eventsCard
        }
        .onAppear { store.activity.startMonitoring() }
        .onDisappear { store.activity.stopMonitoring() }
    }

    // MARK: - Capabilities

    private var capabilitiesCard: some View {
        let summaries = store.capabilitySummaries()
        return SettingsGroup("Reported Capabilities", symbol: "list.bullet.rectangle") {
            if summaries.isEmpty {
                Label("No printer configured", systemImage: "printer")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
            } else {
                VStack(alignment: .leading, spacing: 14) {
                    ForEach(Array(summaries.enumerated()), id: \.element.id) { idx, cap in
                        if idx > 0 { Divider() }
                        capabilitySection(cap)
                    }
                }
            }
        }
    }

    private func capabilitySection(_ cap: ActivityLog.CapabilitySummary) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: cap.isOnline ? "checkmark.circle.fill" : "circle.dashed")
                    .foregroundStyle(cap.isOnline ? Palette.accent : .secondary)
                    .font(.system(size: 12, weight: .semibold))
                Text(verbatim: cap.printerName)
                    .font(.system(size: 13, weight: .semibold))
                Text(verbatim: cap.modelName)
                    .font(.system(size: 11.5, weight: .medium))
                    .foregroundStyle(.secondary)
                if !cap.serialPrefix.isEmpty {
                    Text(verbatim: "\(cap.serialPrefix)…")
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(.tertiary)
                }
                Spacer()
                Text(cap.isOnline ? String(localized: "Online") : String(localized: "Offline"))
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(cap.isOnline ? Palette.accent : .secondary)
            }

            let items: [(String, Bool, String)] = [
                (String(localized: "AMS"), cap.amsCount > 0, cap.amsCount > 0 ? "\(cap.amsCount)" : ""),
                (String(localized: "Chamber sensor"), cap.hasChamberSensor, cap.hasChamberHeater ? String(localized: "Heated") : ""),
                (String(localized: "Chamber light"), cap.hasChamberLight, cap.hasDualLight ? String(localized: "Dual") : ""),
                (String(localized: "Camera"), cap.cameraEnabled, cap.cameraEnabled ? String(localized: "Enabled") : String(localized: "Off")),
                (String(localized: "Nozzles"), true, "\(cap.nozzleCount)"),
                (String(localized: "Aux & chamber fans"), cap.hasAuxFan || cap.hasChamberFan, fanDetail(cap)),
            ]

            Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 8) {
                ForEach(0..<((items.count + 1) / 2), id: \.self) { row in
                    GridRow {
                        capabilityRow(items[row * 2])
                        if row * 2 + 1 < items.count {
                            capabilityRow(items[row * 2 + 1])
                        }
                    }
                }
            }
        }
    }

    private func capabilityRow(_ item: (String, Bool, String)) -> some View {
        HStack(spacing: 8) {
            Image(systemName: item.1 ? "checkmark" : "minus")
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(item.1 ? Palette.accent : .secondary)
                .frame(width: 12)
            Text(item.0)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(item.1 ? .primary : .secondary)
            Spacer(minLength: 8)
            if !item.2.isEmpty {
                Text(verbatim: item.2)
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    private func fanDetail(_ cap: ActivityLog.CapabilitySummary) -> String {
        switch (cap.hasAuxFan, cap.hasChamberFan) {
        case (true, true): return String(localized: "Aux · Chamber")
        case (true, false): return String(localized: "Aux")
        case (false, true): return String(localized: "Chamber")
        case (false, false): return ""
        }
    }

    // MARK: - Events

    private var eventsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Label("Events", systemImage: "waveform.path.ecg")
                    .font(.system(size: 13, weight: .semibold))
                Spacer()
                filterBar
            }

            VStack(alignment: .leading, spacing: 0) {
                if filteredEntries.isEmpty {
                    Text("No activity recorded yet.")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(16)
                } else {
                    ScrollView {
                        // Eager VStack keeps bounded log rows stable for VoiceOver / accessibility scrolling.
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(filteredEntries.enumerated()), id: \.element.id) { idx, entry in
                                if idx > 0 { Divider().padding(.leading, 34) }
                                eventRow(entry)
                            }
                        }
                        .padding(.vertical, 6)
                    }
                    .frame(minHeight: 220, maxHeight: 360)
                }
            }
            .panel(radius: 16, padding: 0)
        }
    }

    private var filterBar: some View {
        HStack(spacing: 4) {
            filterChip(title: String(localized: "All"), active: filter == nil) { filter = nil }
            ForEach(ActivityLog.Category.allCases) { category in
                filterChip(title: category.title, active: filter == category) { filter = category }
            }
        }
    }

    private func filterChip(title: String, active: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 11, weight: active ? .semibold : .medium))
                .foregroundStyle(active ? Palette.tint : .secondary)
                .padding(.horizontal, 9)
                .padding(.vertical, 4)
                .background(Capsule().fill(active ? Palette.tint.opacity(0.15) : Color.primary.opacity(0.05)))
        }
        .buttonStyle(.plain)
    }

    private func eventRow(_ entry: ActivityLog.Entry) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Image(systemName: entry.category.symbol)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(categoryTint(entry.category))
                .frame(width: 16)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    if store.hasMultiplePrinters {
                        Text(verbatim: entry.printerName)
                            .font(.system(size: 11.5, weight: .semibold))
                            .foregroundStyle(.secondary)
                    }
                    Text(verbatim: entry.summary)
                        .font(.system(size: 12))
                        .lineLimit(2)
                }
            }
            Spacer(minLength: 8)
            if let outcome = entry.outcome {
                outcomeBadge(outcome)
            }
            Text(verbatim: entry.timestamp.formatted(date: .omitted, time: .standard))
                .font(.system(size: 11, design: .monospaced))
                .foregroundStyle(.tertiary)
                .fixedSize()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 7)
        .accessibilityElement(children: .combine)
    }

    private func categoryTint(_ category: ActivityLog.Category) -> Color {
        switch category {
        case .connection: return Palette.tint
        case .job: return Palette.printing
        case .issue: return Palette.paused
        case .camera: return .secondary
        case .action: return Palette.tint
        }
    }

    private func outcomeBadge(_ outcome: ActivityLog.Outcome) -> some View {
        let color: Color
        switch outcome {
        case .applied: color = Palette.accent
        case .sent: color = .secondary
        case .failed: color = Palette.failed
        }
        return Text(outcome.title)
            .font(.system(size: 10.5, weight: .semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(Capsule().fill(color.opacity(0.14)))
            .fixedSize()
    }

    // MARK: - Export

    private func exportJSON() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        let date = Date().formatted(.iso8601.year().month().day())
        panel.nameFieldStringValue = "Bambu Bar Activity \(date).json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let data = try store.exportActivityJSON()
            try data.write(to: url, options: .atomic)
            store.showToast(String(localized: "Activity exported"))
        } catch {
            store.showToast(error.localizedDescription, error: true)
        }
    }
}
