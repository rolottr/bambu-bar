import SwiftUI

/// The menu bar panel's window lives on after it closes, and SwiftUI would go on laying it out and drawing it
/// (animations and all) with every report. Closed, it holds an empty frame of the same size instead.
struct MenuPanelHost: View {
    @State private var isOpen = true
    @State private var size: CGSize?

    var body: some View {
        ZStack {
            if isOpen {
                MenuPanel()
                    .onGeometryChange(for: CGSize.self, of: \.size) { size = $0 }
            } else {
                Color.clear.frame(width: size?.width, height: size?.height)
            }
        }
        .onAppear { isOpen = true }
        .onDisappear { isOpen = false }
    }
}

/// The panel under the menu bar icon: everything about the print at a glance, plus the common controls.
struct MenuPanel: View {
    @Environment(PrinterStore.self) private var store
    @Environment(\.plainPanel) private var plainPanel
    /// The panel lists every printer instead of showing one.
    @AppStorage("panelShowsAllPrinters") private var showsAllSetting = false
    /// Set by README frames, which mustn't touch the setting.
    var showsAllOverride: Bool?

    private var showsAll: Bool {
        get { showsAllOverride ?? showsAllSetting }
        nonmutating set { showsAllSetting = newValue }
    }

    var body: some View {
        VStack(spacing: 10) {
            header
            if !store.isConfigured {
                SetupPrompt()
            } else if showsAll && store.hasMultiplePrinters {
                FleetList { session in
                    store.select(session.id)
                    showsAll = false
                }
            } else if let status = store.status, store.isOnline {
                JobHero(status: status, compact: true)
                if let issue = status.mostSevereIssue, issue.severity <= .common {
                    IssueBanner(issue: issue)
                }
                // The demo printers have no camera; keep it out of their screenshots too.
                if let session = store.current, !session.isDemo {
                    CameraPreview(session: session)
                }
                temperatures(status)
                if !status.ams.isEmpty || status.externalSpool?.isLoaded == true {
                    FilamentStrip(status: status, maxHeight: 360)
                }
                if status.state.isActive {
                    PrintControls(status: status)
                }
            } else {
                ConnectionCard()
            }
            if let release = UpdateChecker.shared.offered {
                UpdateRow(release: release)
            }
            if let toast = store.toast {
                ToastView(toast: toast)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
            footer
        }
        .padding(12)
        .frame(width: 372)
        // The menu bar window is clear glass; frost it so text stays legible over busy windows.
        .background(plainPanel ? AnyShapeStyle(Color.clear) : AnyShapeStyle(.regularMaterial))
        .animation(.smooth(duration: 0.3), value: store.toast)
        .animation(.smooth(duration: 0.3), value: store.status?.state)
        .animation(.smooth(duration: 0.25), value: store.selectedSerial)
        .animation(.smooth(duration: 0.25), value: showsAll)
        // Opening the panel shows the printer the menu bar is showing.
        .onAppear { if showsAllOverride == nil, let serial = store.menuBarSession?.id { store.select(serial) } }
    }

    private var header: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                if showsAll && store.hasMultiplePrinters {
                    Text("All Printers").font(.system(size: 14, weight: .semibold))
                    Text(store.fleetSummary)
                        .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                } else if store.hasMultiplePrinters {
                    PrinterPicker {
                        HStack(spacing: 5) {
                            Text(store.printerName)
                                .font(.system(size: 14, weight: .semibold))
                                .lineLimit(1)
                            Image(systemName: "chevron.down")
                                .font(.system(size: 9.5, weight: .bold))
                                .foregroundStyle(.secondary)
                        }
                        .contentShape(Rectangle())
                    }
                    .help("Show another printer")
                } else {
                    Text(store.isConfigured ? store.printerName : "Bambu Bar")
                        .font(.system(size: 14, weight: .semibold))
                        .lineLimit(1)
                }
                if !(showsAll && store.hasMultiplePrinters) {
                    HStack(spacing: 5) {
                        Circle().fill(linkColor).frame(width: 6, height: 6)
                        Text(linkLine).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
            }
            Spacer(minLength: 8)
            if store.hasMultiplePrinters {
                Button { showsAll.toggle() } label: { Image(systemName: showsAll ? "square.grid.2x2.fill" : "square.grid.2x2") }
                    .buttonStyle(HeaderIconStyle())
                    .help(showsAll ? "Show one printer" : "Show all printers")
            }
            if store.isConfigured {
                RefreshControl(compact: true)
            }
            Button { MainWindow.show(.settings) } label: { Image(systemName: "gearshape") }
                .buttonStyle(HeaderIconStyle())
                .help("Settings")
        }
        .padding(.horizontal, 4)
        .padding(.top, 2)
    }

    private var linkColor: Color {
        switch store.phase {
        case .online: return Palette.accent
        case .connecting: return Palette.paused
        case .idle: return .secondary
        default: return Palette.failed
        }
    }

    private var linkLine: String {
        guard let config = store.config else { return String(localized: "No printer set up") }
        var parts = [config.modelName]
        if store.phase == .online {
            if let wifi = store.status?.wifiSignal { parts.append(WiFi.describe(wifi)) } else { parts.append(String(localized: "Connected")) }
        } else {
            parts.append(store.linkSummary)
        }
        return parts.joined(separator: " · ")
    }

    private func temperatures(_ s: PrinterStatus) -> some View {
        HStack(spacing: 8) {
            TemperatureTiles(status: s, model: store.config?.model)
        }
    }

    private var footer: some View {
        HStack(spacing: 6) {
            Button { MainWindow.show(.overview) } label: {
                Label("Open Bambu Bar", systemImage: "macwindow")
            }
            .buttonStyle(FooterStyle())
            .keyboardShortcut("o")
            Spacer()
            Button { NSApp.terminate(nil) } label: { Text("Quit") }
                .buttonStyle(FooterStyle())
                .keyboardShortcut("q")
        }
        .padding(.horizontal, 2)
    }
}

// MARK: - Job hero

/// The printer drawing beside the job: name, stage, big percentage, filament-coloured progress and timing.
struct JobHero: View {
    var status: PrinterStatus
    var compact: Bool
    @Environment(PrinterStore.self) private var store

    var body: some View {
        HStack(alignment: .center, spacing: compact ? 12 : 22) {
            PrinterModelView(art: art)
                .frame(width: compact ? 92 : 190, height: compact ? 110 : 228)
            VStack(alignment: .leading, spacing: compact ? 6 : 10) {
                StatePill(state: status.state, text: pillText ?? status.stateTitle, compact: compact, color: Palette.color(for: status))
                if status.state == .idle {
                    idleBody
                } else {
                    jobBody
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .panel(radius: compact ? 20 : 26, padding: compact ? 12 : 22)
        .overlay(alignment: .topTrailing) {
            if status.chamberLightOn != nil {
                LightToggle(compact: compact)
                    .padding(compact ? 10 : 16)
            }
        }
    }

    /// After a print the part stays on the plate in the colour it was printed in.
    private var art: PrinterArt {
        var art = PrinterArt(status: status, model: store.config?.modelName, lightOverride: store.chamberLightOn)
        if art.filament == nil, !status.state.isActive, let color = store.lastJob?.filamentColor {
            art.filament = Color(bambuHex: color)
        }
        return art
    }

    private var pillText: String? {
        if status.state == .printing, status.speed != .standard { return "\(status.state.title) · \(status.speed.title)" }
        if status.state == .idle, status.doorOpen == true { return String(localized: "\(status.state.title) · Door open", comment: "Printer state, then the door") }
        return nil
    }

    /// The stage while it's doing something special, otherwise the filament it's printing with.
    private var subtitle: String {
        if status.state == .printing, status.stageID == nil || status.stageID == 0 {
            if let spool = status.activeSpool, spool.isLoaded { return "\(spool.displayName) · \(spool.label)" }
            return status.speed == .standard ? status.state.title : String(localized: "\(status.speed.title) speed", comment: "Speed profile, such as “Sport speed”")
        }
        if status.state == .finished, let job = store.lastJob, job.outcome == .finished, let end = job.endedAt {
            let finished = String(localized: "Finished \(relativeTime(end))", comment: "Relative time such as “5 min. ago”")
            return finished + (job.duration.map { " · " + String(localized: "took \(Format.duration(seconds: $0))", comment: "How long the print took") } ?? "")
        }
        if status.state == .failed {
            return status.totalLayers > 0 ? String(localized: "Stopped at layer \(status.layer) of \(status.totalLayers)") : String(localized: "Stopped")
        }
        return status.detailText
    }

    private var tint: Color {
        if status.state == .paused { return Palette.paused }
        if status.state == .failed { return status.canceled ? Color.secondary : Palette.failed }
        if status.state == .finished { return Palette.finished }
        return status.activeSpool.flatMap { spool -> Color? in
            // Very light or very dark filament would vanish against the track; use the accent instead.
            let l = bambuLuminance(spool.color)
            return l > 0.88 || l < 0.12 ? nil : Color(bambuHex: spool.color)
        } ?? Palette.accent
    }

    @ViewBuilder
    private var jobBody: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(status.jobName.isEmpty ? String(localized: "Untitled print") : status.jobName)
                .font(.system(size: compact ? 14 : 20, weight: .semibold))
                .lineLimit(compact ? 1 : 2)
                .truncationMode(.middle)
            Text(subtitle)
                .font(.system(size: compact ? 11.5 : 13))
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
        if status.state == .preparing && status.progress == 0 {
            preparing
        } else {
            progressBody
        }
    }

    /// Before the first layer there's no percentage worth showing: the stage is the news.
    @ViewBuilder
    private var preparing: some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(compact ? .small : .regular)
            Text("Getting ready")
                .font(.system(size: compact ? 18 : 28, weight: .semibold, design: .rounded))
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .padding(.vertical, compact ? 4 : 8)
        timing
    }

    @ViewBuilder
    private var progressBody: some View {
        HStack(alignment: .firstTextBaseline, spacing: 2) {
            Text("\(status.progress)")
                .font(.system(size: compact ? 34 : 56, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .contentTransition(.numericText(value: Double(status.progress)))
            Text("%")
                .font(.system(size: compact ? 17 : 26, weight: .semibold, design: .rounded))
                .foregroundStyle(.secondary)
            Spacer(minLength: 4)
            if status.totalLayers > 0 {
                VStack(alignment: .trailing, spacing: 0) {
                    Text("\(status.layer)/\(status.totalLayers)")
                        .font(.system(size: compact ? 12 : 14, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                    Text("layers").font(.system(size: 10)).foregroundStyle(.tertiary)
                }
            }
        }
        .padding(.top, compact ? -2 : 0)
        FilamentProgressBar(progress: Double(status.progress) / 100, tint: tint, height: compact ? 7 : 10)
            .animation(.smooth, value: status.progress)
        timing
    }

    @ViewBuilder
    private var timing: some View {
        HStack(spacing: 6) {
            if status.state == .finished {
                Label("Ready to remove", systemImage: "hand.raised.fill")
            } else if status.state == .failed && status.canceled {
                Label("Clear the plate to print again", systemImage: "xmark.circle")
            } else if status.state == .failed {
                Label(status.issues.isEmpty ? "Clear the plate to print again" : "Needs attention", systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(Palette.failed)
            } else if let end = status.estimatedEnd {
                Label("\(Format.duration(minutes: status.remainingMinutes)) left", systemImage: "clock")
                Spacer(minLength: 4)
                if status.state != .paused {
                    Text("Done \(Format.finish(end))").foregroundStyle(.secondary)
                }
            } else {
                Label("Estimating…", systemImage: "clock")
            }
        }
        .font(.system(size: compact ? 11.5 : 13, weight: .medium))
        .labelStyle(TightLabelStyle())
        .lineLimit(1)
    }

    @ViewBuilder
    private var idleBody: some View {
        if let last = store.lastJob, last.outcome != .running {
            VStack(alignment: .leading, spacing: 2) {
                Text("Ready to print")
                    .font(.system(size: compact ? 15 : 22, weight: .semibold))
                Text("Last: \(last.name)")
                    .font(.system(size: compact ? 11.5 : 13))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            HStack(spacing: 6) {
                Image(systemName: last.outcome == .finished ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .foregroundStyle(last.outcome == .finished ? Palette.finished : Palette.failed)
                Text(last.outcome.title)
                if let end = last.endedAt { Text("· \(relativeTime(end))").foregroundStyle(.secondary) }
                if let d = last.duration { Text("· \(Format.duration(seconds: d))").foregroundStyle(.secondary) }
            }
            .font(.system(size: compact ? 11.5 : 13, weight: .medium))
            .lineLimit(1)
        } else {
            VStack(alignment: .leading, spacing: 3) {
                Text("Ready to print").font(.system(size: compact ? 15 : 22, weight: .semibold))
                Text("Send a job from Bambu Studio or the printer and it shows up here.")
                    .font(.system(size: compact ? 11.5 : 13))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

/// The chamber light, as a small switch in the corner of the printer card.
struct LightToggle: View {
    var compact = false
    @Environment(PrinterStore.self) private var store
    @State private var hovering = false

    var body: some View {
        let on = store.chamberLightOn
        Button { store.toggleLight() } label: {
            HStack(spacing: 5) {
                Image(systemName: on ? "lightbulb.max.fill" : "lightbulb")
                    .font(.system(size: compact ? 11 : 12.5, weight: .semibold))
                    .foregroundStyle(on ? Color.yellow : Color.secondary)
                    .shadow(color: on ? Color.yellow.opacity(0.8) : .clear, radius: 5)
                    .contentTransition(.symbolEffect(.replace.downUp))
                    .frame(width: compact ? 14 : 16)
                Text("Light")
                    .font(.system(size: compact ? 11 : 12, weight: .semibold))
                    .foregroundStyle(on ? Color.primary : Color.secondary)
            }
            .padding(.horizontal, compact ? 8 : 10)
            .padding(.vertical, compact ? 4 : 6)
            .background {
                Capsule().fill(on ? Color.yellow.opacity(0.16) : Color.primary.opacity(hovering ? 0.10 : 0.06))
                Capsule().strokeBorder(on ? Color.yellow.opacity(0.4) : Color.primary.opacity(0.08), lineWidth: 0.75)
            }
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.smooth(duration: 0.25), value: on)
        .help(on ? "Turn the chamber light off" : "Turn the chamber light on")
        .accessibilityLabel("Chamber light")
        .accessibilityValue(on ? "On" : "Off")
    }
}

struct TightLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 4) {
            configuration.icon.font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
            configuration.title
        }
    }
}

// MARK: - Filament

/// One row per AMS unit (and the external spool), with spools, material and how much is left.
struct FilamentStrip: View {
    var status: PrinterStatus
    /// Past this height the units scroll inside the card. An H2 takes four AMS and eight AMS HTs, and the menu
    /// bar panel can't scroll or grow past the screen.
    var maxHeight: CGFloat?

    var body: some View {
        if let maxHeight {
            ScrollViewReader { proxy in
                ScrollView(.vertical) {
                    units.padding(12)
                }
                .scrollBounceBehavior(.basedOnSize)
                // Opens on the unit feeding the print, which may be one of the lower ones.
                .onAppear { if let unit = activeUnit { proxy.scrollTo(unit, anchor: .center) } }
            }
            .frame(maxHeight: maxHeight)
            .fixedSize(horizontal: false, vertical: true)
            .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
            .panel(radius: 18, padding: 0)
        } else {
            units.panel(radius: 18, padding: 12)
        }
    }

    private var activeUnit: Int? {
        guard status.state.isActive, let id = status.activeSpoolID else { return nil }
        return status.ams.first { unit in unit.spools.contains { $0.id == id } }?.id
    }

    private var units: some View {
        VStack(alignment: .leading, spacing: 9) {
            ForEach(status.ams) { unit in
                VStack(alignment: .leading, spacing: 8) {
                    SectionLabel(verbatim: unit.title, trailing: climate(unit))
                    HStack(spacing: 0) {
                        ForEach(unit.spools) { spool in
                            spoolColumn(spool)
                        }
                        if unit.id == status.ams.last?.id, let ext = status.externalSpool, ext.isLoaded {
                            Divider().frame(height: 40).padding(.horizontal, 4)
                            spoolColumn(ext)
                        }
                    }
                }
            }
            if status.ams.isEmpty, let ext = status.externalSpool, ext.isLoaded {
                SectionLabel("External spool")
                HStack { spoolColumn(ext); Spacer() }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func climate(_ unit: AMSUnit) -> String? {
        var parts: [String] = []
        if unit.dryingMinutesLeft > 0 { parts.append(String(localized: "Drying · \(Format.duration(minutes: unit.dryingMinutesLeft))", comment: "AMS drying, then the time left")) }
        if let h = unit.humidity { parts.append(String(localized: "\(h)% RH", comment: "Relative humidity")) } else if let level = unit.humidityLevel { parts.append(String(localized: "Humidity \(level)/5")) }
        if let t = unit.temperature { parts.append(Format.temperature(t)) }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    private func spoolColumn(_ spool: Spool) -> some View {
        let active = spool.id == status.activeSpoolID && status.state.isActive
        return VStack(spacing: 4) {
            SpoolSwatch(spool: spool, active: active, size: 34)
                .overlay(alignment: .topTrailing) {
                    if active {
                        Circle().fill(Palette.accent).frame(width: 8, height: 8)
                            .overlay(Circle().strokeBorder(.background, lineWidth: 1.5))
                            .offset(x: 2, y: -2)
                    }
                }
            Text(spool.isLoaded ? spool.material : spool.label)
                .font(.system(size: 10.5, weight: .semibold))
                .foregroundStyle(spool.isLoaded ? .primary : .tertiary)
                .lineLimit(1)
            Text(spool.amountText)
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
                .monospacedDigit()
        }
        .frame(maxWidth: .infinity)
        .help(!spool.isLoaded ? String(localized: "\(spool.label): empty", comment: "AMS slot such as A1")
              : "\(spool.label): " + [[spool.brand, spool.displayName].compactMap { $0 }.joined(separator: " "),
                                     spool.colorName, spool.remain == nil ? nil : String(localized: "\(spool.remainDetail) left", comment: "Filament left, such as “81% · ≈ 810 g left”")]
                .compactMap { $0 }.joined(separator: ", "))
    }
}

// MARK: - Controls

struct PrintControls: View {
    var status: PrinterStatus
    @Environment(PrinterStore.self) private var store

    /// The printer only takes print commands in LAN Only mode with Developer Mode on.
    private var locked: Bool { status.controlsLocked }
    private var lockedHelp: String? {
        locked ? String(localized: "The printer accepts this only in LAN Only mode with Developer Mode on (Settings → Network on its screen).") : nil
    }

    var body: some View {
        HStack(spacing: 8) {
            if status.state == .paused {
                Button { store.resume() } label: { Label("Resume", systemImage: locked ? "lock.fill" : "play.fill") }
                    .buttonStyle(ControlButtonStyle(tint: Palette.accentDeep, prominent: true))
                    .help(lockedHelp ?? String(localized: "Resume the print"))
            } else if status.state.isActive {
                Button { store.pause() } label: { Label("Pause", systemImage: locked ? "lock.fill" : "pause.fill") }
                    .buttonStyle(ControlButtonStyle())
                    .help(lockedHelp ?? String(localized: "Pause the print"))
            }
            if status.state.isActive {
                Button { store.stopPrint() } label: { Label("Stop", systemImage: locked ? "lock.fill" : "stop.fill") }
                    .buttonStyle(ControlButtonStyle(tint: Palette.failed))
                    .help(lockedHelp ?? String(localized: "Stop the print"))
            }
            if status.state.isActive {
                Menu {
                    Picker("Speed", selection: Binding(get: { store.speed }, set: { store.setSpeed($0) })) {
                        ForEach(SpeedLevel.allCases) { level in
                            Label("\(level.title) (\(level.percent)%)", systemImage: level.symbol).tag(level)
                        }
                    }
                    .pickerStyle(.inline)
                } label: {
                    Image(systemName: store.speed.symbol)
                }
                .menuStyle(.button)
                .menuIndicator(.hidden)
                .buttonStyle(ControlButtonStyle())
                .frame(width: 44)
                .help("Print speed: \(store.speed.title)")
            }
        }
        .labelStyle(.titleAndIcon)
    }
}

// MARK: - States without a live printer

struct ConnectionCard: View {
    @Environment(PrinterStore.self) private var store

    var body: some View {
        HStack(spacing: 14) {
            PrinterArt(status: nil, model: store.config?.modelName, dimmed: true, animated: false)
                .frame(width: 70, height: 84)
            VStack(alignment: .leading, spacing: 5) {
                Text(title).font(.system(size: 15, weight: .semibold))
                Text(store.linkDetail ?? String(localized: "Bambu Bar keeps trying in the background."))
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 8) {
                    if store.phase == .connecting {
                        ProgressView().controlSize(.small)
                    } else if store.phase == .localNetworkDenied {
                        Button("Open Local Network Settings") { LocalNetwork.openSettings() }
                            .controlSize(.small)
                    } else if store.phase == .unauthorized, let known = store.replacementCode {
                        Button("Use Code from \(known.source)") { store.useReplacementCode() }
                            .controlSize(.small)
                        Button("Settings…") { MainWindow.show(.settings) }
                            .controlSize(.small)
                    } else {
                        Button("Try Again") { store.refresh() }
                            .controlSize(.small)
                        Button("Settings…") { MainWindow.show(.settings) }
                            .controlSize(.small)
                    }
                }
                .padding(.top, 2)
            }
            Spacer(minLength: 0)
        }
        .panel(radius: 20, padding: 14)
    }

    private var title: String {
        switch store.phase {
        case .connecting: return String(localized: "Connecting to \(store.printerName)…")
        case .unauthorized: return String(localized: "Access code rejected")
        case .certificate: return String(localized: "Can't verify the printer")
        case .localNetworkDenied: return String(localized: "Bambu Bar can't reach your network")
        default: return String(localized: "\(store.printerName) is offline")
        }
    }
}

struct SetupPrompt: View {
    @Environment(PrinterStore.self) private var store

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 14) {
                PrinterArt(status: nil, animated: false)
                    .frame(width: 70, height: 84)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Connect your printer").font(.system(size: 15, weight: .semibold))
                    Text(store.discovered.isEmpty
                         ? "Looking for Bambu Lab printers on your network…"
                         : "Found \(store.discovered.count) printer(s) on your network.")
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            ForEach(store.discovered.prefix(3)) { printer in
                Button { MainWindow.show(.overview, setup: printer) } label: {
                    HStack {
                        Image(systemName: "printer.fill").foregroundStyle(Palette.accent)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(printer.name).font(.system(size: 12, weight: .semibold))
                            Text("\(printer.modelName) · \(printer.host)").font(.system(size: 10.5)).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Image(systemName: "chevron.right").font(.system(size: 10, weight: .semibold)).foregroundStyle(.tertiary)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .panel(radius: 12, padding: 9)
            }
            Button { MainWindow.show(.overview) } label: {
                Text("Set Up…").frame(maxWidth: .infinity)
            }
            .buttonStyle(ControlButtonStyle(tint: Palette.accentDeep, prominent: true))
        }
        .panel(radius: 20, padding: 14)
    }
}

struct IssueBanner: View {
    var issue: PrinterIssue

    var body: some View {
        let color = issue.severity <= .fatal ? Palette.failed : issue.severity == .serious ? Palette.paused : Palette.preparing
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: issue.severity <= .serious ? "exclamationmark.triangle.fill" : "info.circle.fill")
                .font(.system(size: 14, weight: .semibold))
                .foregroundStyle(color)
            VStack(alignment: .leading, spacing: 3) {
                Text(issue.message)
                    .font(.system(size: 11.5, weight: .medium))
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 8) {
                    Text(issue.id).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                    if let url = issue.wikiURL {
                        Link("Wiki", destination: url).font(.system(size: 10.5, weight: .semibold))
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .panel(radius: 14, padding: 11, tint: color)
    }
}

/// A newer release, one line above the footer.
struct UpdateRow: View {
    var release: UpdateChecker.Release

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "arrow.down.circle.fill").foregroundStyle(Palette.accent)
            Text("Bambu Bar \(release.version) is available").font(.system(size: 11.5, weight: .medium))
            Spacer(minLength: 0)
            Button("Later") { UpdateChecker.shared.dismissOffer() }
                .buttonStyle(.borderless).controlSize(.small).foregroundStyle(.secondary)
            Button("Download") {
                UpdateChecker.shared.download(release)
                UpdateChecker.shared.dismissOffer()
            }
            .controlSize(.small)
        }
        .panel(radius: 12, padding: 10)
    }
}

struct ToastView: View {
    var toast: PrinterStore.Toast

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: toast.isError ? "exclamationmark.circle.fill" : "checkmark.circle.fill")
                .foregroundStyle(toast.isError ? Palette.paused : Palette.accent)
            Text(toast.message).font(.system(size: 11.5, weight: .medium)).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .panel(radius: 12, padding: 10)
    }
}

struct HeaderIconStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(.secondary)
            .frame(width: 28, height: 28)
            .background(Circle().fill(Color.primary.opacity(configuration.isPressed ? 0.14 : 0.06)))
            .contentShape(Circle())
    }
}

struct FooterStyle: ButtonStyle {
    @State private var hovering = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12, weight: .medium))
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(Color.primary.opacity(configuration.isPressed ? 0.12 : hovering ? 0.07 : 0)))
            .onHover { hovering = $0 }
            .contentShape(Rectangle())
    }
}

enum WiFi {
    static func describe(_ dbm: Int) -> String {
        switch dbm {
        case -55...0: return String(localized: "Wi-Fi excellent")
        case -67 ..< -55: return String(localized: "Wi-Fi good")
        case -75 ..< -67: return String(localized: "Wi-Fi fair")
        case -89 ..< -75: return String(localized: "Wi-Fi weak")
        case -90: return String(localized: "Ethernet")
        default: return String(localized: "Wi-Fi poor")
        }
    }
}
