import SwiftUI

/// The menu bar in one place: which printer the icon follows, what it says while printing and what the panel
/// under it shows, with a preview beside the settings that changes as they do.
struct MenuBarPage: View {
    @Environment(PrinterStore.self) private var store
    @AppStorage(PanelContent.temperatures) private var temperatures = true
    @AppStorage(PanelContent.chamber) private var chamber = true
    @AppStorage(PanelContent.fans) private var fans = false
    @AppStorage(PanelContent.filament) private var filament = true

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            PageHeader(title: "Menu Bar", subtitle: String(localized: "What the menu bar shows while you print, and what you see when you click it."))
            // Side by side when the window is wide enough, so each change shows next to the switch that made it.
            // The settings' ideal width is pinned, or their long notes would ask for a single line and never fit.
            ViewThatFits(in: .horizontal) {
                HStack(alignment: .top, spacing: 16) {
                    settings.frame(minWidth: 250, idealWidth: 250, maxWidth: .infinity, alignment: .topLeading)
                    preview
                }
                VStack(alignment: .leading, spacing: 18) {
                    preview.frame(maxWidth: .infinity)
                    settings
                }
            }
        }
    }

    private var preview: some View {
        MenuBarPreview(style: store.menuBarStyle, temperatures: temperatures, chamber: chamber, fans: fans,
                       filament: filament, actions: store.showPrinterActions)
    }

    private var settings: some View {
        @Bindable var store = store
        return VStack(alignment: .leading, spacing: 22) {
            SettingsGroup("Menu bar", symbol: "menubar.rectangle") {
                if store.hasMultiplePrinters {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Printer in the menu bar").font(.system(size: 12.5))
                        Picker("Printer in the menu bar", selection: $store.menuBarSerial) {
                            Text("Automatic").tag(String?.none)
                            Divider()
                            ForEach(store.sessions) { Text($0.config.name).tag(Optional($0.id)) }
                        }
                        .labelsHidden()
                    }
                    if store.menuBarSerial == nil {
                        Text("Automatic shows whichever printer is busy (paused first, then printing), otherwise the one you last looked at.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Divider()
                }
                VStack(alignment: .leading, spacing: 8) {
                    Text("While printing, show").font(.system(size: 12.5))
                    Picker("While printing, show", selection: $store.menuBarStyle) {
                        ForEach(MenuBarStyle.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.radioGroup)
                    .labelsHidden()
                    .font(.system(size: 12.5))
                }
            }

            SettingsGroup("What to show", symbol: "list.bullet.rectangle") {
                SettingsToggle("Temperatures", isOn: $temperatures)
                SettingsToggle("Chamber temperature", isOn: $chamber)
                    .disabled(!temperatures)
                SettingsToggle("Fans", isOn: $fans)
                SettingsToggle("Filament", isOn: $filament)
                Text("What the panel under the menu bar icon shows below the print. The chamber temperature appears only for printers with a chamber sensor (not the P1 and A series).")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Divider()
                // One switch for every print control, so it's here rather than in Settings: the panel is where they're used most.
                SettingsToggle("Pause, stop and speed", isOn: $store.showPrinterActions)
                Text("Show pause, resume, stop and print speed controls in the menu bar panel and main window, and print actions in the Printer menu.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

// MARK: - Preview

/// The menu bar and its open panel over a desktop, playing a sample print on your printer. It's built from the
/// panel's own pieces and only looks: nothing in it can be clicked (the light switch would reach the printer).
/// Whatever was just turned on lights up for a moment, so it's clear what the switch added.
struct MenuBarPreview: View {
    var style: MenuBarStyle
    var temperatures: Bool
    var chamber: Bool
    var fans: Bool
    var filament: Bool
    var actions: Bool

    @Environment(PrinterStore.self) private var store
    @State private var start = Date()
    @State private var lit: Part?
    @State private var dimming: Task<Void, Never>?

    private enum Part { case statusItem, header, temperatures, fans, filament, actions }

    /// One trip from 18 % to 96 %, so the numbers in the menu bar and the panel keep moving.
    private static let loop: Double = 90
    private static let panelWidth: CGFloat = 372
    private static let margin: CGFloat = 14

    var body: some View {
        VStack(spacing: 10) {
            TimelineView(.periodic(from: start, by: 1)) { context in
                let printer = ShowcasePrinter(session: store.menuBarSession ?? store.current)
                let elapsed = context.date.timeIntervalSince(start).truncatingRemainder(dividingBy: Self.loop)
                let status = ShowcaseScene.printing.snapshot(at: elapsed / Self.loop * ShowcaseScene.duration,
                                                             now: context.date, printer: printer).status ?? .sample
                stage(status, printer: printer, date: context.date)
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(Text("Preview of the menu bar and its panel"))
            Text("Sample print. Changes show here as you make them.")
                .font(.system(size: 11)).foregroundStyle(.secondary)
        }
        .fixedSize(horizontal: true, vertical: false)
        .onChange(of: style) { light(.statusItem) }
        .onChange(of: store.menuBarSerial) { light(.header) }
        .onChange(of: temperatures) { if temperatures { light(.temperatures) } }
        .onChange(of: chamber) { if chamber && temperatures { light(.temperatures) } }
        .onChange(of: fans) { if fans { light(.fans) } }
        .onChange(of: filament) { if filament { light(.filament) } }
        .onChange(of: actions) { if actions { light(.actions) } }
        .onDisappear { dimming?.cancel() }
    }

    private func stage(_ status: PrinterStatus, printer: ShowcasePrinter, date: Date) -> some View {
        VStack(spacing: 8) {
            menuBar(status, date: date)
            panel(status, printer: printer)
                .padding(.horizontal, Self.margin)
                .padding(.bottom, Self.margin + 4)
        }
        .frame(width: Self.panelWidth + Self.margin * 2)
        .background(DesktopBackdrop())
        .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.75))
        .shadow(color: .black.opacity(0.2), radius: 16, y: 8)
        .allowsHitTesting(false)
    }

    // MARK: Menu bar

    private func menuBar(_ status: PrinterStatus, date: Date) -> some View {
        HStack(spacing: 12) {
            Spacer(minLength: 0)
            statusItem(status)
            Image(systemName: "wifi").font(.system(size: 12, weight: .semibold))
            Text(date, format: .dateTime.weekday(.abbreviated).hour().minute())
                .font(.system(size: 12.5, weight: .medium))
                .monospacedDigit()
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 12)
        .frame(height: 26)
        .background(Color.black.opacity(0.3))
        .environment(\.colorScheme, .dark)
    }

    /// Highlighted, as the real one is while its panel is open.
    private func statusItem(_ status: PrinterStatus) -> some View {
        let text = style.text(for: status)
        return HStack(spacing: 4) {
            Image(nsImage: MenuBarIcon.image(state: status.state, progress: Double(status.progress) / 100, alert: false, offline: false))
                .renderingMode(.template)
            if let text {
                Text(text)
                    .font(.system(size: 12.5, weight: .medium))
                    .monospacedDigit()
                    .contentTransition(.numericText())
                    .transition(.opacity.combined(with: .move(edge: .leading)))
            }
        }
        .padding(.horizontal, 7)
        .padding(.vertical, 2)
        .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(.white.opacity(0.22)))
        .overlay {
            RoundedRectangle(cornerRadius: 7, style: .continuous)
                .strokeBorder(Palette.accent, lineWidth: 1.5)
                .padding(-3)
                .shadow(color: Palette.accent.opacity(0.8), radius: 5)
                .opacity(lit == .statusItem ? 1 : 0)
        }
        .animation(.smooth(duration: 0.35), value: text)
        .animation(.smooth(duration: 0.35), value: style)
    }

    // MARK: Panel

    /// The panel's parts in the panel's order, each shown as its switch says.
    private func panel(_ status: PrinterStatus, printer: ShowcasePrinter) -> some View {
        VStack(spacing: 10) {
            header(printer, status: status)
                .lit(lit == .header)
            JobHero(status: status, compact: true, model: printer.model)
            if temperatures {
                HStack(spacing: 8) {
                    TemperatureTiles(status: status, model: PrinterModel.named(printer.model), showsChamber: chamber)
                }
                .lit(lit == .temperatures)
                .transition(Self.appear)
            }
            if fans && status.hasFans {
                FanStrip(status: status)
                    .lit(lit == .fans)
                    .transition(Self.appear)
            }
            if filament && (!status.ams.isEmpty || status.externalSpool?.isLoaded == true) {
                FilamentStrip(status: status, maxHeight: 210)
                    .lit(lit == .filament)
                    .transition(Self.appear)
            }
            if actions {
                PrintControls(status: status)
                    .lit(lit == .actions)
                    .transition(Self.appear)
            }
            footer
        }
        .padding(12)
        .frame(width: Self.panelWidth)
        .background(.regularMaterial)
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(.white.opacity(0.14), lineWidth: 0.75))
        .shadow(color: .black.opacity(0.3), radius: 18, y: 10)
        .animation(.smooth(duration: 0.4), value: temperatures)
        .animation(.smooth(duration: 0.4), value: chamber)
        .animation(.smooth(duration: 0.4), value: fans)
        .animation(.smooth(duration: 0.4), value: filament)
        .animation(.smooth(duration: 0.4), value: actions)
    }

    private static let appear = AnyTransition.asymmetric(
        insertion: .opacity.combined(with: .scale(scale: 0.94, anchor: .top)),
        removal: .opacity.combined(with: .scale(scale: 0.97, anchor: .top)))

    private func header(_ printer: ShowcasePrinter, status: PrinterStatus) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                Text(verbatim: printer.name)
                    .font(.system(size: 14, weight: .semibold))
                    .lineLimit(1)
                HStack(spacing: 5) {
                    Circle().fill(Palette.accent).frame(width: 6, height: 6)
                    Text(verbatim: [printer.model, status.wifiSignal.map(WiFi.describe) ?? String(localized: "Connected")].joined(separator: " · "))
                        .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            ForEach(["arrow.clockwise", "gearshape"], id: \.self) { symbol in
                Image(systemName: symbol)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .frame(width: 28, height: 28)
                    .background(Circle().fill(Color.primary.opacity(0.06)))
            }
        }
        .padding(.horizontal, 4)
        .padding(.top, 2)
    }

    private var footer: some View {
        HStack(spacing: 6) {
            Label("Open Bambu Bar", systemImage: "macwindow")
            Spacer()
            Text("Quit")
        }
        .font(.system(size: 12, weight: .medium))
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }

    /// Lights a part up, then lets it fade; a newer change takes over.
    private func light(_ part: Part) {
        dimming?.cancel()
        withAnimation(.smooth(duration: 0.25)) { lit = part }
        dimming = Task { @MainActor in
            try? await Task.sleep(for: .seconds(1.6))
            guard !Task.isCancelled else { return }
            withAnimation(.smooth(duration: 0.8)) { lit = nil }
        }
    }
}

private extension View {
    /// An accent outline with a soft glow around a part of the preview.
    func lit(_ on: Bool) -> some View {
        overlay {
            RoundedRectangle(cornerRadius: 20, style: .continuous)
                .strokeBorder(Palette.accent, lineWidth: 1.5)
                .padding(-3)
                .shadow(color: Palette.accent.opacity(0.7), radius: 6)
                .opacity(on ? 1 : 0)
        }
    }
}
