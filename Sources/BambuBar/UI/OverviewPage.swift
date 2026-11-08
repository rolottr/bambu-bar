import SwiftUI

/// The window's main page: the job up front, then temperatures, filament, machine details and issues.
struct OverviewPage: View {
    @Environment(PrinterStore.self) private var store

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .bottom) {
                VStack(alignment: .leading, spacing: 3) {
                    PrinterNameField()
                    Text(subtitle).font(.system(size: 13)).foregroundStyle(.secondary)
                }
                Spacer()
                RefreshControl()
            }
            .padding(.bottom, 4)
            if let status = store.status, store.isOnline {
                JobHero(status: status, compact: false)
                if store.showPrinterActions && status.state.isActive {
                    PrintControls(status: status)
                        .frame(maxWidth: 520)
                    if status.controlsLocked {
                        lockedNote
                    }
                }
                ForEach(status.issues) { issue in IssueBanner(issue: issue) { store.current?.dismiss(issue) } }
                // Only with the camera on, in Settings and for this printer (never for the demo printers).
                if let session = store.current, session.showsCamera {
                    CameraCard(session: session)
                }
                metrics(status)
                if !status.ams.isEmpty || status.externalSpool?.isLoaded == true {
                    VStack(alignment: .leading, spacing: 8) {
                        SectionLabel("Filament")
                        FilamentStrip(status: status)
                    }
                }
                machine(status)
            } else {
                ConnectionCard()
            }
        }
    }

    private var subtitle: String {
        guard let config = store.config else { return "" }
        var parts = ["Bambu Lab \(config.modelName)", config.host]
        if let fw = store.status?.firmware { parts.append(String(localized: "Firmware \(fw)")) }
        return parts.joined(separator: " · ")
    }

    private var lockedNote: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "lock.fill").foregroundStyle(.secondary)
            Text("The printer only accepts pause, stop and speed changes from apps in **LAN Only** mode with **Developer Mode** on (printer screen → Settings → Network). Status and the chamber light work either way.")
                .font(.system(size: 11.5))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: 620, alignment: .leading)
    }

    private func metrics(_ s: PrinterStatus) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionLabel(s.chamber == nil && store.config?.model?.isEnclosed == false ? "Temperatures & cooling" : "Temperatures & airflow")
            Grid(horizontalSpacing: 10, verticalSpacing: 10) {
                GridRow {
                    TemperatureTiles(status: s, model: store.config?.model, fillsHeight: true)
                    FanTile(status: s)
                }
            }
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func machine(_ s: PrinterStatus) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionLabel("Printer")
            HStack(alignment: .top, spacing: 10) {
                VStack(spacing: 9) {
                    DetailRow(title: "Speed", value: String(localized: "\(store.speed.title) (\(store.speed.percent)%)", comment: "Speed profile and its percentage"), symbol: "speedometer")
                    DetailRow(title: "Nozzle", value: nozzleText(s), symbol: "pencil.tip")
                    // Only printers with a door sensor report the door.
                    if let open = s.doorOpen {
                        DetailRow(title: "Door", value: open ? String(localized: "Open", comment: "Door") : String(localized: "Closed", comment: "Door"), symbol: "door.left.hand.closed")
                    }
                    if s.chamberLightOn != nil {
                        DetailRow(title: "Light", value: store.chamberLightOn ? String(localized: "On") : String(localized: "Off"), symbol: "lightbulb")
                    }
                }
                .panel(radius: 16, padding: 13)
                VStack(spacing: 9) {
                    DetailRow(title: "Network", value: s.wifiSignal.map { "\(WiFi.describe($0)) (\($0) dBm)" } ?? "—", symbol: "wifi")
                    DetailRow(title: "Storage", value: s.hasSDCard == true ? String(localized: "SD card inserted") : s.hasSDCard == false ? String(localized: "No SD card") : "—", symbol: "sdcard")
                    DetailRow(title: "Live View", value: s.liveViewEnabled == true ? String(localized: "On") : s.liveViewEnabled == false ? String(localized: "Off") : "—", symbol: "video")
                    DetailRow(title: "Firmware", value: (s.firmware ?? "—") + (s.updateAvailable ? " · " + String(localized: "Update available") : ""), symbol: "cpu")
                }
                .panel(radius: 16, padding: 13)
            }
            if let model = store.config?.model {
                ModelCard(model: model)
            }
        }
    }

    private func nozzleText(_ s: PrinterStatus) -> String {
        var parts: [String] = []
        if let d = s.nozzleDiameter { parts.append(String(format: "%.1f mm", d)) }
        if let t = s.nozzleType { parts.append(NozzleType.describe(t)) }
        return parts.isEmpty ? "—" : parts.joined(separator: " · ")
    }
}

/// What this model is built for, from Bambu Lab's published profiles.
struct ModelCard: View {
    var model: PrinterModel

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(spacing: 9) {
                DetailRow(title: "Build volume", value: model.buildVolumeText, symbol: "cube")
                DetailRow(title: "Chamber", value: model.chamberText, symbol: model.isEnclosed ? "square.stack.3d.up.fill" : "square.stack.3d.up")
                DetailRow(title: "Nozzles", value: model.nozzleCount == 2 ? String(localized: "Two, up to \(model.nozzleMax) °C") : String(localized: "One, up to \(model.nozzleMax) °C"), symbol: "flame")
            }
            .panel(radius: 16, padding: 13)
            VStack(spacing: 9) {
                DetailRow(title: "Bed", value: String(localized: "Up to \(model.bedMax) °C"), symbol: "heat.waves")
                DetailRow(title: "Fans", value: fans, symbol: "fan")
                DetailRow(title: "Filament", value: model.usesAMSLite ? "AMS Lite" : "AMS, AMS 2 Pro, AMS HT", symbol: "circle.circle")
            }
            .panel(radius: 16, padding: 13)
        }
    }

    private var fans: String {
        var parts = [String(localized: "Part cooling")]
        if model.hasAuxFan { parts.append(String(localized: "aux", comment: "Auxiliary fan, in a list after “Part cooling”")) }
        if model.hasChamberFan { parts.append(String(localized: "chamber", comment: "Chamber fan, in a list after “Part cooling”")) }
        return parts.joined(separator: ", ")
    }
}

struct FanTile: View {
    var status: PrinterStatus

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 4) {
                Image(systemName: "fan.fill").font(.system(size: 10, weight: .semibold))
                    .foregroundStyle((status.partFan ?? 0) > 0 ? Palette.chamber : .secondary)
                Text("Fans").font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
            }
            ForEach(status.fanReadings) { fan in
                HStack(spacing: 6) {
                    Text(fan.title).font(.system(size: 10.5)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
                    FanBar(value: fan.value)
                    FanPercent(value: fan.value).frame(width: 32, alignment: .trailing)
                }
            }
        }
        .panel(radius: 14, padding: 11)
    }
}

/// The fans in one row, for the menu bar panel (Settings → Menu bar panel): narrower than the Printer page's
/// tile, so it fits beside nothing and works for every model, two nozzles or not.
struct FanStrip: View {
    var status: PrinterStatus

    var body: some View {
        let fans = status.fanReadings
        // Five fans (the H3C) leave too little room for a name and its speed side by side: the speed goes below.
        let stacked = fans.count > 4
        HStack(alignment: .center, spacing: stacked ? 10 : 12) {
            Image(systemName: "fan.fill").font(.system(size: 11, weight: .semibold))
                .foregroundStyle((status.partFan ?? 0) > 0 ? Palette.chamber : .secondary)
                .accessibilityHidden(true)
            ForEach(fans) { fan in
                VStack(alignment: .leading, spacing: 4) {
                    if stacked {
                        Text(fan.title).font(.system(size: 10.5)).foregroundStyle(.secondary).lineLimit(1)
                        FanPercent(value: fan.value)
                    } else {
                        HStack(spacing: 4) {
                            Text(fan.title).font(.system(size: 10.5)).foregroundStyle(.secondary).lineLimit(1)
                            Spacer(minLength: 0)
                            FanPercent(value: fan.value)
                        }
                    }
                    FanBar(value: fan.value)
                }
                .accessibilityElement(children: .combine)
            }
        }
        .panel(radius: 14, padding: 11)
    }
}

/// One fan's speed as a hairline bar.
private struct FanBar: View {
    var value: Int?

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.08))
                Capsule().fill(Palette.chamber.opacity(0.85)).frame(width: geo.size.width * CGFloat(value ?? 0) / 100)
            }
        }
        .frame(height: 4)
    }
}

private struct FanPercent: View {
    var value: Int?

    var body: some View {
        Text(value.map { "\($0)%" } ?? "–").font(.system(size: 10.5, weight: .medium)).monospacedDigit()
            .lineLimit(1).fixedSize()
    }
}

struct FanReading: Identifiable {
    var id: String
    var title: LocalizedStringKey
    var value: Int?
}

extension PrinterStatus {
    /// Part cooling, then the aux and chamber fans where the model has them, and the hotend fan when one of
    /// those is missing (the A series reports only part cooling and hotend). The H3C has two aux fans and, besides
    /// the exhaust, a chamber fan, named as Bambu Studio does.
    var fanReadings: [FanReading] {
        var fans = [FanReading(id: "part", title: "Part", value: partFan)]
        if let auxFan { fans.append(FanReading(id: "aux", title: leftAuxFan == nil ? "Aux" : "Aux R", value: auxFan)) }
        if let leftAuxFan { fans.append(FanReading(id: "aux-left", title: "Aux L", value: leftAuxFan)) }
        if let exhaustFan { fans.append(FanReading(id: "chamber", title: circulationFan == nil ? "Chamber" : "Exhaust", value: exhaustFan)) }
        if let circulationFan { fans.append(FanReading(id: "circulation", title: "Chamber", value: circulationFan)) }
        if let heatbreakFan, auxFan == nil || exhaustFan == nil { fans.append(FanReading(id: "hotend", title: "Hotend", value: heatbreakFan)) }
        return fans
    }

    /// The printer has reported any fan speed yet.
    var hasFans: Bool {
        partFan != nil || auxFan != nil || leftAuxFan != nil || exhaustFan != nil || circulationFan != nil || heatbreakFan != nil
    }
}

enum NozzleType {
    /// "HS01" → "Hardened steel", "HH01" → "Hardened steel, high flow".
    static func describe(_ code: String) -> String {
        let upper = code.uppercased()
        guard upper.count >= 4 else {
            return upper.contains("HARDENED") ? String(localized: "Hardened steel") : upper.contains("STAINLESS") ? String(localized: "Stainless steel") : code
        }
        let chars = Array(upper)
        let material: String
        switch String(chars[2...3]) {
        case "00": material = String(localized: "Stainless steel")
        case "01": material = String(localized: "Hardened steel")
        case "05": material = String(localized: "Tungsten carbide")
        default: material = code
        }
        switch chars[1] {
        case "U": return String(localized: "\(material), TPU high flow", comment: "Nozzle material, then its type")
        case "H", "E": return String(localized: "\(material), high flow", comment: "Nozzle material, then its type")
        default: return material
        }
    }
}

/// The printer's name as the page title, with a pencil to rename it. The name is Bambu Bar's own: it starts as
/// the one the printer announces (set in Bambu Handy) and changing it here doesn't rename the printer.
struct PrinterNameField: View {
    @Environment(PrinterStore.self) private var store
    @State private var editing = false
    @State private var draft = ""
    @FocusState private var focused: Bool

    var body: some View {
        if editing, let config = store.config {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    TextField("Printer name", text: $draft)
                        .textFieldStyle(.plain)
                        .font(.system(size: 26, weight: .bold))
                        .padding(.horizontal, 8).padding(.vertical, 2)
                        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.07)))
                        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Palette.accent.opacity(0.6), lineWidth: 1))
                        .frame(maxWidth: 420)
                        .focused($focused)
                        .onSubmit(save)
                        .onExitCommand { editing = false }
                    Button("Cancel") { editing = false }
                    Button("Save", action: save)
                        .buttonStyle(.borderedProminent)
                        .tint(Palette.accentDeep)
                        .disabled(draft.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                Text(hint(config))
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
            }
            .onAppear { focused = true }
        } else {
            HStack(alignment: .center, spacing: 8) {
                Text(store.printerName).font(.system(size: 26, weight: .bold)).lineLimit(1)
                if store.config != nil {
                    Button {
                        draft = store.printerName
                        editing = true
                    } label: {
                        Image(systemName: "pencil")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(.secondary)
                            .frame(width: 24, height: 24)
                            .background(Circle().fill(Color.primary.opacity(0.07)))
                    }
                    .buttonStyle(.plain)
                    .help("Rename this printer in Bambu Bar")
                }
            }
        }
    }

    private func hint(_ config: PrinterConfig) -> String {
        if let announced = store.announcedName(for: config.serial), announced != draft {
            return String(localized: "Only in Bambu Bar. The printer still calls itself \u{201C}\(announced)\u{201D}; rename it there in Bambu Handy.")
        }
        return String(localized: "Only in Bambu Bar, for the menu bar, window and widgets. The printer keeps its own name.")
    }

    private func save() {
        guard let serial = store.config?.serial, !draft.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        store.rename(serial, to: draft)
        editing = false
    }
}
