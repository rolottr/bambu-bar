import SwiftUI

/// Every loaded spool, large, with material, colour, what's left and the AMS climate.
struct FilamentPage: View {
    @Environment(PrinterStore.self) private var store

    // Four slots fill the available AMS width instead of leaving an unused fifth column.
    private let columns = Array(repeating: GridItem(.flexible(minimum: 150), spacing: 12), count: 4)

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            PageHeader(title: "Filament", subtitle: subtitle)
            if let status = store.status, store.isOnline {
                if status.ams.isEmpty && status.externalSpool == nil {
                    empty("No AMS connected", detail: "Spools loaded in an AMS or on the external holder appear here.")
                }
                ForEach(status.ams) { unit in
                    VStack(alignment: .leading, spacing: 10) {
                        HStack(alignment: .firstTextBaseline) {
                            Text(unit.title).font(.system(size: 16, weight: .semibold))
                            Spacer()
                            climate(unit)
                        }
                        LazyVGrid(columns: columns, spacing: 12) {
                            ForEach(unit.spools) { SpoolCard(spool: $0, active: $0.id == status.activeSpoolID && status.state.isActive) }
                        }
                    }
                }
                if let ext = status.externalSpool {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("External spool").font(.system(size: 16, weight: .semibold))
                        LazyVGrid(columns: columns, spacing: 12) {
                            SpoolCard(spool: ext, active: ext.id == status.activeSpoolID && status.state.isActive)
                        }
                    }
                }
            } else {
                empty(store.isConfigured ? "Printer offline" : "No printer set up",
                      detail: store.isConfigured ? "Filament appears once Bambu Bar is connected." : "Set up your printer on the Printer page.")
            }
        }
    }

    private var subtitle: String {
        guard let s = store.status else { return String(localized: "Spools in your AMS and on the external holder") }
        let loaded = s.allSpools.filter(\.isLoaded).count
        let grams = s.allSpools.compactMap(\.gramsLeft)
        let count = String(localized: "\(loaded) spool(s) loaded")
        if !grams.isEmpty, !(s.state.isActive && s.activeSpool != nil) {
            return count + " · " + String(localized: "\(Format.grams(grams.reduce(0, +))) of filament on hand")
        }
        if let active = s.activeSpool, s.state.isActive {
            return count + " · " + String(localized: "printing with \(active.slotName) \(active.displayName)", comment: "The slot (A1), then the filament")
        }
        return count
    }

    @ViewBuilder
    private func climate(_ unit: AMSUnit) -> some View {
        HStack(spacing: 14) {
            if unit.dryingMinutesLeft > 0 {
                Label("Drying · \(Format.duration(minutes: unit.dryingMinutesLeft)) left", systemImage: "sun.max.fill")
                    .foregroundStyle(Palette.bed)
            }
            if let h = unit.humidity {
                Label("\(h)% humidity", systemImage: "humidity.fill").foregroundStyle(h > 40 ? Palette.paused : .secondary)
            } else if let level = unit.humidityLevel {
                Label("Humidity \(level) of 5", systemImage: "humidity.fill").foregroundStyle(level >= 4 ? Palette.paused : .secondary)
            }
            if let t = unit.temperature {
                Label(Format.temperature(t), systemImage: "thermometer.medium").foregroundStyle(.secondary)
            }
        }
        .font(.system(size: 12, weight: .medium))
        .labelStyle(TightLabelStyle())
    }

    private func empty(_ title: LocalizedStringKey, detail: LocalizedStringKey) -> some View {
        VStack(spacing: 8) {
            Image(systemName: "circle.circle").font(.system(size: 34)).foregroundStyle(.tertiary)
            Text(title).font(.system(size: 15, weight: .semibold))
            Text(detail).font(.system(size: 12)).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 50)
        .panel()
    }
}

struct SpoolCard: View {
    var spool: Spool
    var active: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top) {
                SpoolSwatch(spool: spool, active: active, size: 58,
                            hubText: spool.isLoaded ? spool.weight.map { Format.grams($0, approximate: false) } : nil)
                Spacer()
                VStack(alignment: .trailing, spacing: 4) {
                    Text(spool.label)
                        .font(.system(size: 11, weight: .bold, design: .rounded))
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Capsule().fill(Color.primary.opacity(0.08)))
                    if active {
                        Text("In use").font(.system(size: 10.5, weight: .semibold)).foregroundStyle(Palette.accent)
                    }
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(spool.isLoaded ? spool.displayName : String(localized: "Empty slot"))
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(spool.isLoaded ? .primary : .secondary)
                    .lineLimit(1)
                Text(spool.isLoaded ? detail : String(localized: "Load a spool to see it here"))
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            // Kept in place even when the printer can't measure the spool, so every card has the same proportions.
            remaining
                .opacity(spool.isLoaded && spool.remain != nil ? 1 : 0)
                .accessibilityHidden(spool.remain == nil)
        }
        .panel(radius: 18, padding: 14, tint: active ? Palette.accent : nil)
        .overlay {
            if active {
                RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(Palette.accent.opacity(0.6), lineWidth: 1.2)
            }
        }
        .help(help)
    }

    private var remaining: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text("Remaining").foregroundStyle(.secondary)
                Spacer()
                if let grams = spool.gramsLeft {
                    Text(Format.grams(grams)).fontWeight(.semibold).monospacedDigit()
                }
            }
            .font(.system(size: 10.5))
            HStack(spacing: 8) {
                FilamentProgressBar(progress: Double(spool.remain ?? 0) / 100,
                                    tint: Color(bambuHex: spool.color).map { bambuLuminance(spool.color) > 0.88 ? Color.secondary : $0 } ?? .secondary,
                                    height: 5)
                // Wide enough for "100%", so the bars line up across cards.
                Text("\(spool.remain ?? 0)%")
                    .font(.system(size: 10.5, weight: .semibold)).monospacedDigit()
                    .frame(width: 32, alignment: .trailing)
            }
        }
    }

    /// "Bambu Lab · Grass Green", with the material when the name doesn't already say it and the hex code when
    /// the colour has no Bambu name.
    private var detail: String {
        var parts: [String] = []
        if let brand = spool.brand { parts.append(brand) }
        if !spool.displayName.localizedCaseInsensitiveContains(spool.material) { parts.append(spool.material) }
        parts.append(spool.colorName ?? "#" + String(spool.color.prefix(6)))
        return parts.joined(separator: " · ")
    }

    private var help: String {
        guard spool.isLoaded else { return String(localized: "\(spool.slotName): empty", comment: "AMS slot such as A1") }
        var lines = [[spool.brand, spool.displayName].compactMap { $0 }.joined(separator: " "),
                     [spool.colorName, "#" + String(spool.color.prefix(6))].compactMap { $0 }.joined(separator: " · ")]
        if let range = spool.nozzleRange { lines.append(String(localized: "Prints at \(range.lowerBound)–\(range.upperBound) °C")) }
        if spool.remain == nil {
            lines.append(spool.isExternal ? String(localized: "The external holder can’t weigh the spool.")
                         : String(localized: "Only Bambu Lab spools with an RFID tag report what’s left."))
        }
        return lines.joined(separator: "\n")
    }
}
