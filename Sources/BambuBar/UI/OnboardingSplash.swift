import AppKit
import SwiftUI

/// Re-openable UDM-style Setup Guide sheet: one feature per card in fixed order, looping visual
/// mini-demo per card (respecting Reduce Motion and stopping cleanly on disappear), draft toggles
/// applied only on Finish, bottom selector chips, and full keyboard navigation (←/→, Space, ↩, Esc).
struct OnboardingSplash: View {
    @Environment(PrinterStore.self) private var store
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    let firstRun: Bool
    let close: () -> Void

    @State private var cards: [OnboardingFeature]
    @State private var index = 0
    @State private var draft: OnboardingDraft
    @State private var phase = 0
    @State private var demoTask: Task<Void, Never>?
    @State private var keyMonitor: Any?

    init(settings: OnboardingSettings, capabilities: OnboardingCapabilities?, firstRun: Bool, close: @escaping () -> Void) {
        self.firstRun = firstRun
        self.close = close
        let visibleCards = OnboardingFeature.cards(for: capabilities)
        _cards = State(initialValue: visibleCards)
        _draft = State(initialValue: OnboardingDraft(from: settings))
    }

    private var current: OnboardingFeature {
        cards[min(max(index, 0), max(cards.count - 1, 0))]
    }

    private var isLast: Bool { index >= cards.count - 1 }

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 18) {
                demoStage
                    .frame(height: 240)
                    .frame(maxWidth: .infinity)
                    .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.75)
                    )

                cardControls
            }
            .padding(22)

            Divider()

            VStack(spacing: 14) {
                bottomSelector
                actionRow
            }
            .padding(.horizontal, 22)
            .padding(.vertical, 14)
        }
        .frame(width: 640, height: 470)
        .onAppear {
            startDemoLoop()
            installKeyMonitor()
        }
        .onDisappear {
            stopDemoLoop()
            removeKeyMonitor()
        }
        .onChange(of: index) { _, _ in
            phase = 0
            startDemoLoop()
        }
        .onChange(of: reduceMotion) { _, reduced in
            if reduced { stopDemoLoop() } else { startDemoLoop() }
        }
    }

    // MARK: - Controls

    private var cardControls: some View {
        HStack(alignment: .center, spacing: 14) {
            Image(systemName: current.symbol)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(Palette.tint)
                .frame(width: 32, height: 32)
                .background(Circle().fill(Palette.tint.opacity(0.14)))
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(current.title)
                    .font(.system(size: 16, weight: .semibold))
                if let label = current.toggleLabel {
                    Text(label)
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }

            Spacer(minLength: 12)

            if let value = draft.value(for: current) {
                Toggle(current.title, isOn: Binding(
                    get: { value },
                    set: { draft.set($0, for: current) }
                ))
                .toggleStyle(.switch)
                .labelsHidden()
                .tint(Palette.tintDeep)
            }
        }
        .frame(minHeight: 40)
    }

    private var bottomSelector: some View {
        HStack(spacing: 6) {
            ForEach(Array(cards.enumerated()), id: \.element.id) { idx, feature in
                let selected = idx == index
                let toggleValue = draft.value(for: feature)
                Button {
                    jump(to: idx)
                } label: {
                    VStack(spacing: 4) {
                        HStack(spacing: 4) {
                            Image(systemName: feature.symbol)
                                .font(.system(size: 12, weight: .semibold))
                            if let on = toggleValue {
                                Circle()
                                    .fill(on ? Palette.accent : Color.secondary.opacity(0.35))
                                    .frame(width: 6, height: 6)
                            }
                        }
                        Text(feature.shortTitle)
                            .font(.system(size: 10.5, weight: selected ? .semibold : .medium))
                            .lineLimit(1)
                    }
                    .foregroundStyle(selected ? Palette.tint : .secondary)
                    .frame(maxWidth: .infinity, minHeight: 40)
                    .padding(.horizontal, 6)
                    .background(
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .fill(selected ? Palette.tint.opacity(0.15) : Color.primary.opacity(0.04))
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .strokeBorder(selected ? Palette.tint.opacity(0.55) : .clear, lineWidth: 1)
                    )
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(selected ? .isSelected : [])
            }
        }
    }

    private var actionRow: some View {
        HStack(spacing: 10) {
            Button(firstRun ? "Skip" : "Cancel") { skip() }
                .fixedSize()
            Spacer()
            Button("Back") { move(-1) }
                .disabled(index == 0)
                .fixedSize()
            if isLast {
                Button("Finish") { finish() }
                    .buttonStyle(.borderedProminent)
                    .tint(Palette.tintDeep)
                    .fixedSize()
            } else {
                Button("Continue") { move(1) }
                    .buttonStyle(.borderedProminent)
                    .tint(Palette.tintDeep)
                    .fixedSize()
            }
        }
    }

    // MARK: - Looping Mini-Demos

    @ViewBuilder
    private var demoStage: some View {
        ZStack {
            DesktopBackdrop()
            switch current {
            case .menuBar: menuBarDemo
            case .widgets: widgetsDemo
            case .notifications: notificationsDemo
            case .camera: cameraDemo
            case .controls: controlsDemo
            case .fleet: fleetDemo
            case .done: doneDemo
            }
        }
        .environment(\.colorScheme, .dark)
        .allowsHitTesting(false)
    }

    private var menuBarDemo: some View {
        let pcts = [24, 48, 72, 91]
        let mins = ["1h 12m", "46m", "24m", "8m"]
        let step = phase % pcts.count
        let pct = pcts[step]
        return VStack(spacing: 24) {
            HStack(spacing: 12) {
                Spacer()
                HStack(spacing: 5) {
                    Image(nsImage: MenuBarIcon.image(state: .printing, progress: Double(pct) / 100, alert: false, offline: false))
                        .renderingMode(.template)
                    if draft.showProgressInMenuBar {
                        Text("\(pct)% · \(mins[step])")
                            .font(.system(size: 12.5, weight: .semibold))
                            .monospacedDigit()
                            .contentTransition(.numericText())
                    }
                }
                .padding(.horizontal, 9)
                .padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(.white.opacity(0.22)))
                .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(Palette.tint, lineWidth: 1.5))
                Image(systemName: "wifi").font(.system(size: 12, weight: .semibold))
            }
            .padding(.horizontal, 14)
            .frame(height: 30)
            .background(Color.black.opacity(0.35))

            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text(store.printerName).font(.system(size: 13, weight: .semibold))
                    Spacer()
                    Text("\(pct)%").font(.system(size: 18, weight: .bold, design: .rounded)).monospacedDigit()
                }
                FilamentProgressBar(progress: Double(pct) / 100, tint: Palette.accent, height: 7)
                HStack {
                    Label("\(mins[step]) left", systemImage: "clock")
                    Spacer()
                    Text("220° · 60°")
                }
                .font(.system(size: 11)).foregroundStyle(.secondary).monospacedDigit()
            }
            .padding(14)
            .frame(width: 280)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.black.opacity(0.45)))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.white.opacity(0.14), lineWidth: 0.75))
            Spacer(minLength: 0)
        }
        .animation(reduceMotion ? nil : .smooth(duration: 0.4), value: step)
        .animation(reduceMotion ? nil : .smooth(duration: 0.25), value: draft.showProgressInMenuBar)
    }

    private var widgetsDemo: some View {
        let sizes = WidgetSize.allCases
        let active = sizes[phase % sizes.count]
        return HStack(spacing: 16) {
            ForEach(sizes) { size in
                let highlighted = size == active
                VStack(spacing: 8) {
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(.black.opacity(highlighted ? 0.55 : 0.30))
                        .overlay(
                            VStack(spacing: 4) {
                                Image(systemName: size == .small ? "circle.dashed" : size == .medium ? "rectangle.split.2x1" : "rectangle.grid.2x2")
                                    .font(.system(size: 20, weight: .semibold))
                                    .foregroundStyle(highlighted ? Palette.accent : .white.opacity(0.6))
                                Text("64%").font(.system(size: 13, weight: .bold, design: .rounded)).monospacedDigit()
                            }
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 12, style: .continuous)
                                .strokeBorder(highlighted ? Palette.tint : .white.opacity(0.12), lineWidth: highlighted ? 1.5 : 0.75)
                        )
                        .frame(width: size == .small ? 90 : 140, height: size == .large ? 130 : 90)
                    Text(size.title)
                        .font(.system(size: 11, weight: highlighted ? .semibold : .regular))
                        .foregroundStyle(highlighted ? .white : .white.opacity(0.65))
                }
                .scaleEffect(highlighted ? 1.04 : 0.96)
            }
        }
        .animation(reduceMotion ? nil : .spring(response: 0.45, dampingFraction: 0.8), value: active)
    }

    private var notificationsDemo: some View {
        let items: [(String, String, Color, String)] = [
            ("checkmark.circle.fill", String(localized: "Print finished"), Palette.finished, "Articulated Dragon · 1h 42m"),
            ("pause.circle.fill", String(localized: "Print paused"), Palette.paused, String(localized: "AMS slot 2 filament runout")),
            ("exclamationmark.triangle.fill", String(localized: "Print failed"), Palette.failed, String(localized: "Nozzle temperature anomaly")),
        ]
        let active = phase % items.count
        return VStack(spacing: 10) {
            ForEach(Array(items.enumerated()), id: \.offset) { idx, item in
                let lit = idx == active
                HStack(spacing: 10) {
                    Image(systemName: item.0)
                        .font(.system(size: 18, weight: .semibold))
                        .foregroundStyle(item.2)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.1).font(.system(size: 12.5, weight: .semibold))
                        Text(item.3).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .frame(width: 340)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.black.opacity(lit ? 0.62 : 0.34)))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(lit ? item.2.opacity(0.6) : .white.opacity(0.10), lineWidth: 1))
                .opacity(draft.notificationsEnabled ? (lit ? 1 : 0.65) : 0.35)
                .scaleEffect(lit ? 1.02 : 0.98)
            }
        }
        .animation(reduceMotion ? nil : .smooth(duration: 0.35), value: active)
    }

    private var cameraDemo: some View {
        let live = (phase % 2) == 0 && draft.cameraEnabled
        return ZStack(alignment: .topLeading) {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(LinearGradient(colors: [Color.black.opacity(0.75), Color(white: 0.14)], startPoint: .top, endPoint: .bottom))
                .overlay {
                    PrinterArt(status: .sample, model: store.config?.modelName, dimmed: !draft.cameraEnabled, animated: false)
                        .frame(width: 110, height: 132)
                }
                .overlay(alignment: .topLeading) {
                    HStack(spacing: 5) {
                        Circle().fill(live ? Palette.failed : Color.secondary).frame(width: 7, height: 7)
                        Text(draft.cameraEnabled ? "1080p · H.264" : String(localized: "Off"))
                            .font(.system(size: 10.5, weight: .semibold))
                    }
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(Capsule().fill(.black.opacity(0.55)))
                    .padding(10)
                }
                .overlay(
                    RoundedRectangle(cornerRadius: 14, style: .continuous)
                        .strokeBorder(.white.opacity(0.14), lineWidth: 0.75)
                )
                .frame(width: 320, height: 180)
        }
        .animation(reduceMotion ? nil : .smooth(duration: 0.3), value: live)
    }

    private var controlsDemo: some View {
        let speeds = SpeedLevel.allCases
        let currentSpeed = speeds[phase % speeds.count]
        return VStack(spacing: 14) {
            HStack(spacing: 10) {
                demoPill(symbol: "pause.fill", title: String(localized: "Pause"), tint: .white, active: phase % 3 == 0)
                demoPill(symbol: "stop.fill", title: String(localized: "Stop"), tint: Palette.failed, active: false)
                demoPill(symbol: currentSpeed.symbol, title: currentSpeed.title, tint: Palette.tint, active: phase % 3 != 0)
            }
            .opacity(draft.showPrinterActions ? 1 : 0.35)
            Text(verbatim: "\(currentSpeed.percent)%")
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .opacity(draft.showPrinterActions ? 1 : 0.35)
        }
        .animation(reduceMotion ? nil : .smooth(duration: 0.3), value: phase)
    }

    private func demoPill(symbol: String, title: String, tint: Color, active: Bool) -> some View {
        Label(title, systemImage: symbol)
            .font(.system(size: 12.5, weight: .semibold))
            .foregroundStyle(tint)
            .padding(.horizontal, 14)
            .padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.black.opacity(active ? 0.60 : 0.38)))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(active ? tint.opacity(0.65) : .white.opacity(0.12), lineWidth: 1))
    }

    private var fleetDemo: some View {
        let samplePrinters: [(String, Int, Color)] = [
            ("P2S Workshop", 72, Palette.printing),
            ("X1C Garage", 100, Palette.finished),
            ("A1 Desk", 41, Palette.paused),
        ]
        let focused = phase % samplePrinters.count
        return VStack(spacing: 8) {
            ForEach(Array(samplePrinters.enumerated()), id: \.offset) { idx, item in
                HStack(spacing: 10) {
                    Circle().fill(item.2).frame(width: 8, height: 8)
                    Text(verbatim: item.0).font(.system(size: 12.5, weight: .semibold))
                    Spacer()
                    Text("\(item.1)%").font(.system(size: 12, weight: .semibold, design: .rounded)).monospacedDigit()
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 9)
                .frame(width: 300)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.black.opacity(idx == focused ? 0.6 : 0.35)))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(idx == focused ? Palette.tint.opacity(0.6) : .white.opacity(0.1), lineWidth: 1))
                .opacity(draft.panelShowsAllPrinters ? 1 : (idx == 0 ? 1 : 0.35))
            }
        }
        .animation(reduceMotion ? nil : .smooth(duration: 0.35), value: focused)
    }

    private var doneDemo: some View {
        VStack(spacing: 16) {
            HStack(spacing: 10) {
                Spacer()
                HStack(spacing: 5) {
                    Image(nsImage: MenuBarIcon.image(state: .printing, progress: 0.64, alert: false, offline: false))
                        .renderingMode(.template)
                    if draft.showProgressInMenuBar {
                        Text("64% · 32m").font(.system(size: 12, weight: .semibold)).monospacedDigit()
                    }
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(.white.opacity(0.24)))
                .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(Palette.accent, lineWidth: 1.5))
            }
            .padding(.horizontal, 16)
            .frame(height: 30)
            .background(Color.black.opacity(0.35))

            Spacer()

            VStack(spacing: 10) {
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 38))
                    .foregroundStyle(Palette.accent)
                HStack(spacing: 10) {
                    ForEach(cards.filter { $0 != .done }) { feature in
                        if let on = draft.value(for: feature) {
                            Label(feature.shortTitle, systemImage: feature.symbol)
                                .font(.system(size: 11, weight: .medium))
                                .padding(.horizontal, 9)
                                .padding(.vertical, 4)
                                .foregroundStyle(on ? .white : .white.opacity(0.5))
                                .background(Capsule().fill(on ? Palette.accent.opacity(0.28) : .white.opacity(0.08)))
                        }
                    }
                }
            }
            Spacer()
        }
    }

    // MARK: - Actions & Keyboard

    private func startDemoLoop() {
        stopDemoLoop()
        guard !reduceMotion else { return }
        demoTask = Task { @MainActor in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(1.5))
                guard !Task.isCancelled else { return }
                phase &+= 1
            }
        }
    }

    private func stopDemoLoop() {
        demoTask?.cancel()
        demoTask = nil
    }

    private func jump(to target: Int) {
        let clamped = min(max(target, 0), cards.count - 1)
        if reduceMotion { index = clamped } else { withAnimation(.easeOut(duration: 0.15)) { index = clamped } }
    }

    private func move(_ delta: Int) {
        jump(to: index + delta)
    }

    private func skip() {
        stopDemoLoop()
        removeKeyMonitor()
        close()
    }

    private func finish() {
        stopDemoLoop()
        removeKeyMonitor()
        store.applyOnboarding(draft)
        close()
    }

    private func installKeyMonitor() {
        removeKeyMonitor()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            // Ignore events with command/control/option modifiers so standard shortcuts pass through.
            guard event.modifierFlags.intersection([.command, .control, .option]).isEmpty else { return event }
            switch event.keyCode {
            case 123: // ←
                move(-1)
                return nil
            case 124: // →
                move(1)
                return nil
            case 49: // Space
                draft.toggle(current)
                return nil
            case 36, 76: // Return / Enter
                if isLast { finish() } else { move(1) }
                return nil
            case 53: // Esc
                skip()
                return nil
            default:
                return event
            }
        }
    }

    private func removeKeyMonitor() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
    }
}
