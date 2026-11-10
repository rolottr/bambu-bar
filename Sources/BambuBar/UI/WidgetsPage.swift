import SwiftUI

/// The desktop widgets, shown off: one widget on a desktop backdrop that morphs between its three sizes while a
/// simulated print plays through every state, the features each size carries, and how to add one.
struct WidgetsPage: View {
    @Environment(PrinterStore.self) private var store

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            PageHeader(title: "Widgets", subtitle: String(localized: "Your printers on the desktop and in Notification Center, in three sizes."))
            WidgetShowcase()
            howTo
        }
    }

    private var howTo: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionLabel("Add a widget")
            VStack(alignment: .leading, spacing: 12) {
                step(1, "cursorarrow.click.2", "Right-click the desktop and choose **Edit Widgets…** (or click **Edit Widgets** at the bottom of Notification Center).")
                step(2, "magnifyingglass", "Search for **Bambu Bar**.")
                step(3, "hand.tap.fill", "Drag the size you want onto the desktop or into Notification Center, then click **Done**.")
                step(4, "printer.fill", store.hasMultiplePrinters
                     ? "To show a particular printer, right-click the widget and choose **Edit \"Printer\"**. Add one widget per printer to watch them side by side; a widget left empty follows the menu bar."
                     : "Add more printers later and each widget can show a different one: right-click it and choose **Edit \"Printer\"**.")
            }
            .panel(radius: 16, padding: 16)
        }
    }

    private func step(_ n: Int, _ symbol: String, _ text: LocalizedStringKey) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Text("\(n)")
                .font(.system(size: 12, weight: .bold, design: .rounded))
                .foregroundStyle(Palette.accent)
                .frame(width: 22, height: 22)
                .background(Circle().fill(Palette.accent.opacity(0.15)))
            Image(systemName: symbol).font(.system(size: 13)).foregroundStyle(.secondary).frame(width: 18).padding(.top, 3)
            Text(text).font(.system(size: 12.5)).fixedSize(horizontal: false, vertical: true)
                .padding(.top, 2)
        }
    }
}

// MARK: - Showcase

enum WidgetSize: String, CaseIterable, Identifiable {
    case small, medium, large
    var id: String { rawValue }
    var title: String {
        switch self {
        case .small: return String(localized: "Small", comment: "Widget size")
        case .medium: return String(localized: "Medium", comment: "Widget size")
        case .large: return String(localized: "Large", comment: "Widget size")
        }
    }
    /// How much the showcase enlarges it, so every size fills the stage about equally.
    var stageScale: CGFloat {
        switch self {
        case .small: return 1.3
        case .medium: return 1.1
        case .large: return 1.0
        }
    }

    var frame: CGSize {
        switch self {
        case .small: return CGSize(width: 170, height: 170)
        case .medium: return CGSize(width: 364, height: 170)
        case .large: return CGSize(width: 364, height: 382)
        }
    }

    /// What each size carries, for the feature chips.
    var features: Set<WidgetFeature> {
        switch self {
        case .small: return [.progress, .timeLeft, .finishTime, .layers, .stage, .lastPrint, .temperatures, .spools]
        case .medium: return [.progress, .timeLeft, .finishTime, .layers, .stage, .temperatures, .spools, .printer]
        case .large: return Set(WidgetFeature.allCases)
        }
    }

    var summary: String {
        switch self {
        case .small: return String(localized: "The essentials at a glance: a progress ring with the percentage, time left and when it'll be done. Paused, it says why; finished, how long ago; idle, the temperatures and loaded spools.")
        case .medium: return String(localized: "The printer as it is: the part growing in the filament's colour, the job and its progress, finish time and layers, and nozzle, bed and chamber temperatures.")
        case .large: return String(localized: "Everything: progress and timing, temperature tiles, every AMS spool with what's left, the newest error in plain words, and how the last print went.")
        }
    }
}

enum WidgetFeature: String, CaseIterable, Identifiable {
    case progress, timeLeft, finishTime, layers, stage, temperatures, spools, printer, errors, lastPrint
    var id: String { rawValue }

    var title: String {
        switch self {
        case .progress: return String(localized: "Progress", comment: "Widget feature")
        case .timeLeft: return String(localized: "Time left", comment: "Widget feature")
        case .finishTime: return String(localized: "Finish time", comment: "Widget feature")
        case .layers: return String(localized: "Layers", comment: "Widget feature")
        case .stage: return String(localized: "Stage & pause reason", comment: "Widget feature")
        case .temperatures: return String(localized: "Temperatures", comment: "Widget feature")
        case .spools: return String(localized: "AMS spools", comment: "Widget feature")
        case .printer: return String(localized: "Live drawing", comment: "Widget feature: the printer drawing that shows the part growing")
        case .errors: return String(localized: "Errors", comment: "Widget feature")
        case .lastPrint: return String(localized: "Last print", comment: "Widget feature")
        }
    }

    var symbol: String {
        switch self {
        case .progress: return "percent"
        case .timeLeft: return "clock.fill"
        case .finishTime: return "flag.checkered"
        case .layers: return "square.stack.3d.up.fill"
        case .stage: return "pause.circle.fill"
        case .temperatures: return "thermometer.medium"
        case .spools: return "circle.circle.fill"
        case .printer: return "printer.fill"
        case .errors: return "exclamationmark.triangle.fill"
        case .lastPrint: return "checkmark.circle.fill"
        }
    }
}

/// The simulated print the showcase plays.
enum ShowcaseScene: String, CaseIterable, Identifiable {
    case printing, preparing, paused, finished, idle
    var id: String { rawValue }
    var title: String {
        switch self {
        case .printing: return String(localized: "Printing", comment: "Printer state")
        case .preparing: return String(localized: "Heating", comment: "Printer state")
        case .paused: return String(localized: "Paused", comment: "Printer state")
        case .finished: return String(localized: "Finished", comment: "Printer state")
        case .idle: return String(localized: "Ready", comment: "Printer state")
        }
    }
    var help: String {
        switch self {
        case .printing: return String(localized: "Show the widget while printing")
        case .preparing: return String(localized: "Show the widget while heating")
        case .paused: return String(localized: "Show the widget while paused")
        case .finished: return String(localized: "Show the widget once finished")
        case .idle: return String(localized: "Show the widget while ready")
        }
    }
    var symbol: String {
        switch self {
        case .printing: return "printer.fill"
        case .preparing: return "flame.fill"
        case .paused: return "pause.fill"
        case .finished: return "checkmark.circle.fill"
        case .idle: return "moon.zzz.fill"
        }
    }

    var color: Color {
        switch self {
        case .printing: return Palette.printing
        case .preparing: return Palette.preparing
        case .paused: return Palette.paused
        case .finished: return Palette.finished
        case .idle: return Color(red: 0.62, green: 0.66, blue: 0.70)
        }
    }

    /// The snapshot `t` seconds into the scene, so numbers move while it plays. The printer's own name, model
    /// and spools are used when there is one, so the tour previews your setup rather than made-up filament.
    func snapshot(at t: Double, now: Date, printer: ShowcasePrinter) -> SharedSnapshot {
        var s = PrinterStatus.sample
        s.jobName = "Articulated Dragon"
        s.updatedAt = now
        if !printer.ams.isEmpty || printer.externalSpool != nil {
            s.ams = printer.ams
            s.externalSpool = printer.externalSpool
            s.activeSpoolID = printer.activeSpoolID
        }
        s.fit(to: PrinterModel.named(printer.model))
        let filament = s.activeSpool
        var lastJob: JobRecord?
        switch self {
        case .printing:
            // Races from 18 % to 96 % so the ring, bar, layers and time left all move.
            let p = min(0.96, 0.18 + t / ShowcaseScene.duration * 0.78)
            s.progress = Int(p * 100)
            s.totalLayers = 284
            s.layer = Int(Double(s.totalLayers) * p)
            s.remainingMinutes = max(1, Int(148 * (1 - p)))
            s.nozzle = Temperature(current: 220 + (t.truncatingRemainder(dividingBy: 2) < 1 ? 1 : 0), target: 220)
        case .preparing:
            s.state = .preparing
            s.stageID = t < ShowcaseScene.duration * 0.55 ? 7 : 1
            s.progress = 0
            s.layer = 0
            s.remainingMinutes = 152
            s.nozzle = Temperature(current: min(220, 95 + t * 40), target: 220)
            s.bed = Temperature(current: min(55, 30 + t * 9), target: 55)
        case .paused:
            s.state = .paused
            s.stageID = 6
            s.progress = 64
            s.layer = 182
            s.totalLayers = 284
            s.remainingMinutes = 54
            s.issues = [PrinterIssue(id: "HMS_0700_2300_0002_0001", severity: .serious,
                                     message: HMSCatalog.shared.hmsText("0700230000020001")
                                         ?? "AMS A slot 4 filament has run out. Insert a new spool to continue.", wikiURL: nil)]
        case .finished:
            s.state = .finished
            s.stageID = 255
            s.progress = 100
            s.layer = 284
            s.totalLayers = 284
            s.remainingMinutes = 0
            s.activeSpoolID = nil
            s.nozzle = Temperature(current: max(38, 120 - t * 14), target: 0)
            s.bed = Temperature(current: max(30, 52 - t * 3), target: 0)
            lastJob = JobRecord(name: s.jobName, startedAt: now.addingTimeInterval(-3 * 3600), endedAt: now.addingTimeInterval(-12 * 60),
                                outcome: .finished, totalLayers: 284, filamentColor: filament?.color ?? "E2763AFF", filamentMaterial: filament?.material ?? "PLA")
        case .idle:
            s.state = .idle
            s.stageID = 255
            s.jobName = ""
            s.progress = 0
            s.layer = 0
            s.totalLayers = 0
            s.remainingMinutes = 0
            s.activeSpoolID = nil
            s.chamberLightOn = false
            s.nozzle = Temperature(current: 26, target: 0)
            s.bed = Temperature(current: 24, target: 0)
            s.chamber = 23
            lastJob = JobRecord(name: "Articulated Dragon", startedAt: now.addingTimeInterval(-5 * 3600), endedAt: now.addingTimeInterval(-2 * 3600),
                                outcome: .finished, totalLayers: 284, filamentColor: filament?.color ?? "E2763AFF", filamentMaterial: filament?.material ?? "PLA")
        }
        return SharedSnapshot(serial: "showcase", printerName: printer.name, model: printer.model, link: .online, status: s,
                              lastJob: lastJob, savedAt: now)
    }

    static let duration: Double = 3.6
}

/// Who the showcase dresses up as: the current printer, or a sample P2S before one is set up.
struct ShowcasePrinter {
    var name: String
    var model: String
    var ams: [AMSUnit]
    var externalSpool: Spool?
    var activeSpoolID: Int?

    @MainActor
    init(_ store: PrinterStore) {
        self.init(session: store.current)
    }

    @MainActor
    init(session: PrinterSession?) {
        name = session?.config.name ?? "P2S Workshop"
        model = session?.config.modelName ?? "P2S"
        let status = session?.status
        ams = status?.ams ?? []
        externalSpool = status?.externalSpool?.isLoaded == true ? status?.externalSpool : nil
        // The spool in use if it's printing, else the first loaded one, so the part has a real colour.
        let loaded = (status?.allSpools ?? []).filter(\.isLoaded)
        activeSpoolID = status?.activeSpool.map(\.id) ?? loaded.first(where: { bambuLuminance($0.color) < 0.85 })?.id ?? loaded.first?.id
    }
}

struct WidgetShowcase: View {
    @Environment(PrinterStore.self) private var store
    @Namespace private var sceneHighlight
    @State private var size: WidgetSize = .small
    @State private var scene: ShowcaseScene = .printing
    @State private var sceneStart = Date()
    @State private var ticker: Task<Void, Never>?

    /// The stage hugs the widget: its size plus this margin all round.
    private let margin: CGFloat = 26

    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(alignment: .top, spacing: 20) {
                stage
                details.frame(minWidth: 240)
            }
            .frame(minWidth: 730)
            VStack(alignment: .leading, spacing: 20) {
                stage
                details
            }
        }
        .onAppear(perform: startTicker)
        .onDisappear { ticker?.cancel() }
    }

    private var details: some View {
        VStack(alignment: .leading, spacing: 14) {
            controls
            features
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var stage: some View {
        let scaled = CGSize(width: size.frame.width * size.stageScale, height: size.frame.height * size.stageScale)
        return ZStack(alignment: .bottom) {
            DesktopBackdrop()
            VStack(spacing: 0) {
                Spacer(minLength: 0)
            TimelineView(.animation(minimumInterval: 1 / 20)) { context in
                let t = min(ShowcaseScene.duration, context.date.timeIntervalSince(sceneStart))
                WidgetFrame(width: size.frame.width, height: size.frame.height) {
                    widget(scene.snapshot(at: t, now: context.date, printer: ShowcasePrinter(store)), date: context.date)
                        .id(size)
                        .transition(.opacity.combined(with: .scale(scale: 0.96)))
                }
                .scaleEffect(size.stageScale)
                .frame(width: scaled.width, height: scaled.height)
            }
                Spacer(minLength: 0)
                scenes
                    .padding(.bottom, 14)
            }
        }
        .frame(width: max(scaled.width + margin * 2, Self.scenesWidth), height: scaled.height + margin * 2 + Self.scenesHeight)
        .clipShape(RoundedRectangle(cornerRadius: 30, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 30, style: .continuous).strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.75))
        .shadow(color: .black.opacity(0.2), radius: 16, y: 8)
        .animation(.spring(response: 0.6, dampingFraction: 0.82), value: size)
    }

    @ViewBuilder
    private func widget(_ snapshot: SharedSnapshot, date: Date) -> some View {
        switch size {
        case .small: SmallWidgetView(snapshot: snapshot, date: date)
        case .medium: MediumWidgetView(snapshot: snapshot, date: date)
        case .large: LargeWidgetView(snapshot: snapshot, date: date)
        }
    }

    private var controls: some View {
        Picker("Size", selection: Binding(get: { size }, set: { pick(size: $0) })) {
            ForEach(WidgetSize.allCases) { Text($0.title).tag($0) }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
    }

    private static let scenesHeight: CGFloat = 44
    private static let scenesWidth: CGFloat = 470

    /// The states the tour walks through, on a frosted bar under the widget. The highlight slides to the
    /// current one; clicking a state jumps there.
    private var scenes: some View {
        HStack(spacing: 2) {
            ForEach(ShowcaseScene.allCases) { s in
                Button { pick(scene: s) } label: {
                    HStack(spacing: 5) {
                        Image(systemName: s.symbol)
                            .font(.system(size: 10.5, weight: .semibold))
                            .foregroundStyle(s == scene ? s.color : .white.opacity(0.75))
                        Text(s.title)
                            .font(.system(size: 11.5, weight: s == scene ? .semibold : .medium))
                            .foregroundStyle(.white.opacity(s == scene ? 1 : 0.8))
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .background {
                        if s == scene {
                            Capsule()
                                .fill(.white.opacity(0.18))
                                .overlay(Capsule().strokeBorder(s.color.opacity(0.55), lineWidth: 0.75))
                                .matchedGeometryEffect(id: "highlight", in: sceneHighlight)
                        }
                    }
                    .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .help(s.help)
            }
        }
        .padding(3)
        .background(Capsule().fill(.black.opacity(0.28)))
        .background(Capsule().fill(.ultraThinMaterial))
        .overlay(Capsule().strokeBorder(.white.opacity(0.12), lineWidth: 0.75))
        .environment(\.colorScheme, .dark)
    }

    private var features: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(size.title).font(.system(size: 17, weight: .semibold))
                .id("title-\(size.rawValue)")
                .transition(.opacity)
            Text(size.summary)
                .font(.system(size: 12.5))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .id(size)
                .transition(.opacity)
            FlowLayout(spacing: 6) {
                ForEach(WidgetFeature.allCases) { feature in
                    let on = size.features.contains(feature)
                    Label(feature.title, systemImage: feature.symbol)
                        .font(.system(size: 11, weight: .medium))
                        .padding(.horizontal, 9).padding(.vertical, 4)
                        .foregroundStyle(on ? Palette.accent : Color.secondary.opacity(0.6))
                        .background(Capsule().fill(on ? Palette.accent.opacity(0.13) : Color.primary.opacity(0.04)))
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .animation(.smooth(duration: 0.35), value: size)
        .panel(radius: 16, padding: 14)
    }

    // MARK: Tour

    /// Every scene in one size, then the next size, on a loop: the widget morphs while its content keeps moving.
    private func startTicker() {
        ticker?.cancel()
        sceneStart = Date()
        ticker = Task { @MainActor in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(ShowcaseScene.duration))
                guard !Task.isCancelled else { return }
                advance()
            }
        }
    }

    private func advance() {
        let scenes = ShowcaseScene.allCases
        let next = (scenes.firstIndex(of: scene)! + 1) % scenes.count
        withAnimation(.smooth(duration: 0.4)) {
            scene = scenes[next]
            if next == 0 {
                let sizes = WidgetSize.allCases
                size = sizes[(sizes.firstIndex(of: size)! + 1) % sizes.count]
            }
        }
        sceneStart = Date()
    }

    /// Jumps to a size; the loop carries on from there.
    private func pick(size new: WidgetSize) {
        withAnimation(.spring(response: 0.6, dampingFraction: 0.82)) { size = new }
        startTicker()
    }

    /// Jumps to a state, giving it a full turn before the loop moves on.
    private func pick(scene new: ShowcaseScene) {
        withAnimation(.smooth(duration: 0.4)) { scene = new }
        startTicker()
    }
}

/// Approximates the desktop widget chrome around a widget view.
struct WidgetFrame<Content: View>: View {
    var width: CGFloat
    var height: CGFloat
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(16)
            .frame(width: width, height: height)
            .background(WidgetBackdrop())
            .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).strokeBorder(.white.opacity(0.12), lineWidth: 0.75))
            .shadow(color: .black.opacity(0.25), radius: 10, y: 4)
            .environment(\.colorScheme, .dark)
    }
}

/// A soft macOS-style wallpaper behind the showcase widget.
struct DesktopBackdrop: View {
    var body: some View {
        ZStack {
            LinearGradient(colors: [Color(red: 0.55, green: 0.40, blue: 0.32), Color(red: 0.20, green: 0.15, blue: 0.18)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
            // Soft light from radial gradients (not blurred shapes, which offscreen rendering skips).
            GeometryReader { geo in
                ZStack {
                    RadialGradient(colors: [Color(red: 0.95, green: 0.62, blue: 0.38).opacity(0.55), .clear],
                                   center: UnitPoint(x: 0.15, y: 0.15), startRadius: 0, endRadius: max(geo.size.width, geo.size.height) * 0.6)
                    RadialGradient(colors: [Color(red: 0.30, green: 0.45, blue: 0.80).opacity(0.45), .clear],
                                   center: UnitPoint(x: 0.9, y: 0.9), startRadius: 0, endRadius: max(geo.size.width, geo.size.height) * 0.65)
                }
            }
        }
    }
}

/// Lays chips out in rows, wrapping as needed.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, row: CGFloat = 0, widest: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > 0 && x + size.width > width {
                y += row + spacing
                x = 0
                row = 0
            }
            x += size.width + spacing
            row = max(row, size.height)
            widest = max(widest, x - spacing)
        }
        return CGSize(width: min(width, widest), height: y + row)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, row: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > bounds.minX && x + size.width > bounds.maxX {
                y += row + spacing
                x = bounds.minX
                row = 0
            }
            view.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            row = max(row, size.height)
        }
    }
}
