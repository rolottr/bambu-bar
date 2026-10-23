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
                if status.state.isActive {
                    PrintControls(status: status)
                        .frame(maxWidth: 520)
                    if status.controlsLocked {
                        lockedNote
                    }
                }
                ForEach(status.issues) { IssueBanner(issue: $0) }
                // The demo printers have no camera; keep it out of their screenshots too.
                if let session = store.current, !session.isDemo {
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
        if let fw = store.status?.firmware { parts.append("Firmware \(fw)") }
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
                    DetailRow(title: "Speed", value: "\(store.speed.title) (\(store.speed.percent)%)", symbol: "speedometer")
                    DetailRow(title: "Nozzle", value: nozzleText(s), symbol: "pencil.tip")
                    if store.config?.model?.isEnclosed ?? true {
                        DetailRow(title: "Door", value: s.doorOpen.map { $0 ? "Open" : "Closed" } ?? "—", symbol: "door.left.hand.closed")
                    }
                    if s.chamberLightOn != nil {
                        DetailRow(title: "Light", value: store.chamberLightOn ? "On" : "Off", symbol: "lightbulb")
                    }
                }
                .panel(radius: 16, padding: 13)
                VStack(spacing: 9) {
                    DetailRow(title: "Network", value: s.wifiSignal.map { "\(WiFi.describe($0)) (\($0) dBm)" } ?? "—", symbol: "wifi")
                    DetailRow(title: "Storage", value: s.hasSDCard == true ? "SD card inserted" : s.hasSDCard == false ? "No SD card" : "—", symbol: "sdcard")
                    DetailRow(title: "Live View", value: s.liveViewEnabled == true ? "On" : s.liveViewEnabled == false ? "Off" : "—", symbol: "video")
                    DetailRow(title: "Firmware", value: (s.firmware ?? "—") + (s.updateAvailable ? " · Update available" : ""), symbol: "cpu")
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
                DetailRow(title: "Nozzles", value: model.nozzleCount == 2 ? "Two, up to \(model.nozzleMax) °C" : "One, up to \(model.nozzleMax) °C", symbol: "flame")
            }
            .panel(radius: 16, padding: 13)
            VStack(spacing: 9) {
                DetailRow(title: "Bed", value: "Up to \(model.bedMax) °C", symbol: "heat.waves")
                DetailRow(title: "Fans", value: fans, symbol: "fan")
                DetailRow(title: "Filament", value: model.usesAMSLite ? "AMS Lite" : "AMS, AMS 2 Pro, AMS HT", symbol: "circle.circle")
            }
            .panel(radius: 16, padding: 13)
        }
    }

    private var fans: String {
        var parts = ["Part cooling"]
        if model.hasAuxFan { parts.append("aux") }
        if model.hasChamberFan { parts.append("chamber") }
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
                    .rotationEffect(.degrees(0))
                Text("Fans").font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
            }
            fan("Part", status.partFan)
            if let aux = status.auxFan { fan("Aux", aux) }
            if let exhaust = status.exhaustFan { fan("Chamber", exhaust) }
            if let hotend = status.heatbreakFan, status.auxFan == nil || status.exhaustFan == nil { fan("Hotend", hotend) }
        }
        .panel(radius: 14, padding: 11)
    }

    private func fan(_ title: String, _ value: Int?) -> some View {
        HStack(spacing: 6) {
            Text(title).font(.system(size: 10.5)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.primary.opacity(0.08))
                    Capsule().fill(Palette.chamber.opacity(0.85)).frame(width: geo.size.width * CGFloat(value ?? 0) / 100)
                }
            }
            .frame(height: 4)
            Text(value.map { "\($0)%" } ?? "–").font(.system(size: 10.5, weight: .medium)).monospacedDigit()
                .frame(width: 32, alignment: .trailing)
        }
    }
}

enum NozzleType {
    /// "HS01" → "Hardened steel", "HH01" → "Hardened steel, high flow".
    static func describe(_ code: String) -> String {
        let upper = code.uppercased()
        guard upper.count >= 4 else {
            return upper.contains("HARDENED") ? "Hardened steel" : upper.contains("STAINLESS") ? "Stainless steel" : code
        }
        let chars = Array(upper)
        let material: String
        switch String(chars[2...3]) {
        case "00": material = "Stainless steel"
        case "01": material = "Hardened steel"
        case "05": material = "Tungsten carbide"
        default: material = code
        }
        switch chars[1] {
        case "U": return material + ", TPU high flow"
        case "H", "E": return material + ", high flow"
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
            return "Only in Bambu Bar. The printer still calls itself \u{201C}\(announced)\u{201D}; rename it there in Bambu Handy."
        }
        return "Only in Bambu Bar, for the menu bar, window and widgets. The printer keeps its own name."
    }

    private func save() {
        guard let serial = store.config?.serial, !draft.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        store.rename(serial, to: draft)
        editing = false
    }
}
