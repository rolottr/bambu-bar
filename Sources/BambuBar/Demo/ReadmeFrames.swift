import AppKit
import SwiftUI

/// `--demo printing --fleet --readme-frames <dir>` renders the README animations frame by frame from scripted demo data
/// (no printer, no network, nothing saved), then quits. `scripts/make-readme-animations.sh` turns the frames into
/// animated WebP.
///
/// Every frame is a fresh hosting view rendered offscreen at a fixed clock, so animations come out the same on
/// every run and nothing on the screen is recorded.
@MainActor
enum ReadmeFrames {
    /// Also on for `--store-media`, which renders from the same scripted demo.
    static var isActive: Bool { CommandLine.arguments.contains("--readme-frames") || StoreMedia.directory != nil }

    static var directory: URL? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: "--readme-frames"), i + 1 < a.count else { return nil }
        return URL(fileURLWithPath: a[i + 1], isDirectory: true)
    }

    static let fps = 15.0
    /// Today at 9:41, the time the menu bar shows.
    static let start: Date = Calendar.current.date(bySettingHour: 9, minute: 41, second: 0, of: Date()) ?? Date()

    /// Seconds into the animation at a frame's clock.
    static func elapsed(_ clock: Date) -> TimeInterval { clock.timeIntervalSince(start) }
    private static var window: NSWindow?

    static func run(into dir: URL) {
        Task { @MainActor in
            // Let the demo printer report in.
            try? await Task.sleep(for: .seconds(1))
            let store = PrinterStore.shared
            guard let session = store.current else { exit(1) }
            renderMenuBar(into: dir.appendingPathComponent("menubar"), store: store, session: session)
            renderWindow(into: dir.appendingPathComponent("showcase"), store: store, session: session)
            renderWidgets(into: dir.appendingPathComponent("widgets"), store: store)
            print("frames written to \(dir.path)")
            exit(0)
        }
    }

    // MARK: - Scripted print

    /// The demo print at `t` seconds: printing with the progress climbing, the chamber light switching on,
    /// then (from `pauseAt`) paused because the filament ran out.
    static func status(at t: Double, pauseAt: Double?) -> PrinterStatus {
        var s = PrinterStatus.sample
        s.jobName = "Articulated Dragon"
        s.totalLayers = 284
        s.wifiSignal = -52
        let printing = min(t, pauseAt ?? t)
        let p = 0.62 + printing * 0.016
        s.progress = Int(p * 100)
        s.layer = Int(Double(s.totalLayers) * p)
        s.remainingMinutes = max(1, Int(162 * (1 - p)))
        s.updatedAt = start.addingTimeInterval(t)
        s.chamberLightOn = t >= 1.8
        s.nozzle = Temperature(current: 220 + (Int(t * 2) % 3 == 0 ? 1 : 0), target: 220)
        if let pauseAt, t >= pauseAt {
            s.state = .paused
            s.stageID = 6
            s.issues = [PrinterIssue(id: "HMS_0700_2300_0002_0001", severity: .serious,
                                     message: "AMS A slot 4 filament has run out. Insert a new spool to continue.",
                                     wikiURL: HMSCatalog.wikiURL(code: "0700230000020001", serial: nil))]
        }
        return s
    }

    // MARK: - Menu bar

    private static func renderMenuBar(into dir: URL, store: PrinterStore, session: PrinterSession) {
        let size = CGSize(width: 600, height: 700)
        let frames = Int(10 * fps)
        for i in 0..<frames {
            let t = Double(i) / fps
            session.showDemoStatus(status(at: t, pauseAt: 5.0))
            let clock = start.addingTimeInterval(t)
            // The last stretch shows every printer, as the panel's grid button does.
            let all = store.hasMultiplePrinters && t >= 7.6
            save(MenuBarScene(store: store, size: size, clock: clock, showsAll: all).environment(\.demoClock, clock), size: size,
                 scale: 1.6, to: dir, index: i)
        }
    }

    // MARK: - Window

    /// A tour of the window: the printer page with its 3D model, the filament page, every printer, then back to
    /// the start so the loop is seamless.
    private static func renderWindow(into dir: URL, store: PrinterStore, session: PrinterSession) {
        let windowSize = FramedWindow.standardSize
        let size = CGSize(width: windowSize.width + 56, height: windowSize.height + 56)
        var stops: [(AppSection, Double)] = [(.overview, 5.2), (.filament, 3.8)]
        if store.hasMultiplePrinters { stops.append((.fleet, 3.8)) }
        let fade = 0.45
        let total = stops.reduce(0) { $0 + $1.1 }
        for i in 0..<Int(total * fps) {
            let t = Double(i) / fps
            session.showDemoStatus(status(at: t, pauseAt: nil))
            let clock = start.addingTimeInterval(t)
            // Which stop, and how far into the fade to the next one (the last fades back to the first).
            var begin = 0.0, index = 0
            while index < stops.count - 1 && t >= begin + stops[index].1 { begin += stops[index].1; index += 1 }
            let local = t - begin
            let next = stops[(index + 1) % stops.count].0
            let blend = max(0, min(1, (local - (stops[index].1 - fade)) / fade))
            let e = blend * blend * (3 - 2 * blend)
            let view = ZStack {
                DesktopBackdrop()
                FramedWindow(store: store, section: stops[index].0, next: next, blend: e, size: windowSize)
            }
            .environment(\.demoClock, clock)
            .environment(\.colorScheme, .dark)
            save(view, size: size, scale: 1.1, to: dir, index: i)
        }
    }

    // MARK: - Widgets

    private enum Segment {
        case beat(WidgetSize, ShowcaseScene, Double)
        case morph(WidgetSize, WidgetSize, Double)

        var length: Double {
            switch self {
            case .beat(_, _, let l), .morph(_, _, let l): return l
            }
        }
    }

    private static func renderWidgets(into dir: URL, store: PrinterStore) {
        let size = CGSize(width: 580, height: 620)
        let printer = ShowcasePrinter(store)
        // Per size: printing, paused, finished; then the widget morphs into the next size (and back to the
        // first at the end, so the loop is seamless).
        let beats: [(ShowcaseScene, Double)] = [(.printing, 2.6), (.paused, 1.5), (.finished, 1.5)]
        let sizes = WidgetSize.allCases
        var segments: [Segment] = []
        for (i, s) in sizes.enumerated() {
            for (scene, length) in beats { segments.append(.beat(s, scene, length)) }
            segments.append(.morph(s, sizes[(i + 1) % sizes.count], 0.5))
        }
        let total = segments.reduce(0) { $0 + $1.length }
        for i in 0..<Int(total * fps) {
            let t = Double(i) / fps
            let now = start.addingTimeInterval(t)
            var begin = 0.0
            var segment = segments[0]
            for candidate in segments {
                if t < begin + candidate.length { segment = candidate; break }
                begin += candidate.length
            }
            let local = t - begin
            let view: WidgetStageFrame
            switch segment {
            case .beat(let s, let scene, _):
                view = WidgetStageFrame(size: s, morph: nil, scene: scene,
                                        snapshot: scene.snapshot(at: local, now: now, printer: printer), date: now)
            case .morph(let from, let to, let length):
                // Out of the last beat (finished), into the first (printing).
                view = WidgetStageFrame(size: to, morph: (from, to, local / length), scene: .printing,
                                        snapshot: ShowcaseScene.printing.snapshot(at: 0, now: now, printer: printer), date: now,
                                        fromSnapshot: ShowcaseScene.finished.snapshot(at: 1.5, now: now, printer: printer))
            }
            save(view.frame(width: size.width, height: size.height), size: size, scale: 1.6, to: dir, index: i)
        }
    }

    // MARK: - Rendering

    private static func save<V: View>(_ view: V, size: CGSize, scale: CGFloat, to dir: URL, index: Int) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent(String(format: "frame-%04d.png", index + 1))
        try? render(view, size: size, scale: scale)?.representation(using: .png, properties: [:])?.write(to: url)
    }

    /// Draws `view` offscreen at `size` points, `scale` pixels per point.
    static func render<V: View>(_ view: V, size: CGSize, scale: CGFloat) -> NSBitmapImageRep? {
        let host = NSHostingView(rootView: view.frame(width: size.width, height: size.height))
        host.frame = NSRect(origin: .zero, size: size)
        let window = self.window ?? {
            let w = NSWindow(contentRect: NSRect(x: -6000, y: -6000, width: size.width, height: size.height),
                             styleMask: [.borderless], backing: .buffered, defer: false)
            w.isReleasedWhenClosed = false
            return w
        }()
        self.window = window
        window.setContentSize(size)
        window.contentView = host
        window.orderFrontRegardless()
        host.layoutSubtreeIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.03))
        host.layoutSubtreeIfNeeded()
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale), pixelsHigh: Int(size.height * scale),
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        rep.size = size
        host.cacheDisplay(in: host.bounds, to: rep)
        return rep
    }
}

// MARK: - Scenes

/// A desktop with a menu bar; Bambu Bar's status item is open and its panel hangs below it on frosted glass.
struct MenuBarScene: View {
    let store: PrinterStore
    let size: CGSize
    let clock: Date
    var showsAll = false

    private let barHeight: CGFloat = 26
    /// The panel's top edge, just under the menu bar.
    static let panelTop: CGFloat = 32
    /// Under the status item: everything right of it in the menu bar is 404 points wide.
    private var panelOrigin: CGPoint { CGPoint(x: size.width - 404, y: Self.panelTop) }

    var body: some View {
        ZStack(alignment: .topLeading) {
            DesktopBackdrop()
            menuBar
            panel
                .offset(x: panelOrigin.x, y: panelOrigin.y)
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
        .clipped()
        .environment(\.colorScheme, .dark)
    }

    private var menuBar: some View {
        HStack(spacing: 18) {
            Image(systemName: "apple.logo").font(.system(size: 14, weight: .semibold))
            Text("Finder").font(.system(size: 13, weight: .bold))
            ForEach(["File", "Edit", "View"], id: \.self) { Text($0).font(.system(size: 13)) }
            Spacer()
            statusItem
            Image(systemName: "wifi").font(.system(size: 13, weight: .semibold))
            Image(systemName: "magnifyingglass").font(.system(size: 13, weight: .semibold))
            Image(systemName: "switch.2").font(.system(size: 13, weight: .semibold))
            Text(clock.formatted(.dateTime.weekday(.abbreviated).hour().minute())).font(.system(size: 13, weight: .medium))
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 16)
        .frame(width: size.width, height: barHeight)
        .background(Color.black.opacity(0.28))
    }

    /// The status item, highlighted because its panel is open.
    private var statusItem: some View {
        let s = store.status
        let text: String = {
            guard let s else { return "" }
            if s.state == .paused { return "\(s.progress)% · Paused" }
            return "\(s.progress)% · \(Format.duration(minutes: s.remainingMinutes))"
        }()
        return HStack(spacing: 4) {
            Image(nsImage: MenuBarIcon.image(state: s?.state, progress: Double(s?.progress ?? 0) / 100,
                                             alert: (s?.mostSevereIssue?.severity ?? .info) <= .serious, offline: false))
                .renderingMode(.template)
            Text(text).font(.system(size: 13, weight: .medium)).monospacedDigit()
        }
        .padding(.horizontal, 7)
        .padding(.vertical, 2)
        .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(.white.opacity(0.22)))
    }

    /// The panel on dark glass over the desktop, like the real menu bar window.
    private var panel: some View {
        MenuPanel(showsAllOverride: showsAll)
            .environment(store)
            .environment(\.plainPanel, true)
            // The desktop behind is all soft gradients, so translucent dark glass over it reads as the real panel.
            .background(Color.black.opacity(0.45))
            .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(.white.opacity(0.16), lineWidth: 0.8))
            .shadow(color: .black.opacity(0.4), radius: 22, y: 12)
    }
}

/// The three window buttons, drawn because offscreen rendering only captures the window's content.
struct TrafficLights: View {
    var body: some View {
        HStack(spacing: 8) {
            ForEach([Color(red: 1, green: 0.37, blue: 0.34), Color(red: 1, green: 0.74, blue: 0.18),
                     Color(red: 0.16, green: 0.79, blue: 0.25)], id: \.self) { color in
                Circle().fill(color).frame(width: 12, height: 12)
                    .overlay(Circle().strokeBorder(.black.opacity(0.15), lineWidth: 0.5))
            }
        }
    }
}

/// The Widgets page stage for the README: the widget (morphing between sizes) on a desktop with the states under it.
private struct WidgetStageFrame: View {
    let size: WidgetSize
    let morph: (from: WidgetSize, to: WidgetSize, progress: Double)?
    let scene: ShowcaseScene
    let snapshot: SharedSnapshot
    let date: Date
    /// What the outgoing size shows during a morph.
    var fromSnapshot: SharedSnapshot?

    private static func scale(_ size: WidgetSize) -> CGFloat {
        switch size {
        case .small: return 1.5
        case .medium: return 1.25
        case .large: return 1.15
        }
    }

    var body: some View {
        ZStack {
            DesktopBackdrop()
            VStack(spacing: 0) {
                Spacer(minLength: 0)
                widget
                Spacer(minLength: 0)
                states.padding(.bottom, 22)
            }
        }
        .environment(\.colorScheme, .dark)
    }

    @ViewBuilder
    private var widget: some View {
        if let morph {
            // Ease the frame from one size to the next while the content crossfades.
            let e = morph.progress < 0.5 ? 2 * morph.progress * morph.progress : 1 - pow(-2 * morph.progress + 2, 2) / 2
            let a = dimensions(morph.from), b = dimensions(morph.to)
            let w = a.width + (b.width - a.width) * e, h = a.height + (b.height - a.height) * e
            ZStack(alignment: .topLeading) {
                WidgetBackdrop()
                content(morph.from, snapshot: fromSnapshot ?? snapshot).opacity(1 - e)
                content(morph.to, snapshot: snapshot).opacity(e)
            }
            .frame(width: w, height: h, alignment: .topLeading)
            .clipShape(RoundedRectangle(cornerRadius: 22 * (Self.scale(morph.from) * (1 - e) + Self.scale(morph.to) * e), style: .continuous))
            .shadow(color: .black.opacity(0.3), radius: 14, y: 6)
        } else {
            WidgetFrame(width: size.frame.width, height: size.frame.height) { view(for: size, snapshot: snapshot) }
                .scaleEffect(Self.scale(size))
                .frame(width: dimensions(size).width, height: dimensions(size).height)
        }
    }

    private func dimensions(_ size: WidgetSize) -> CGSize {
        CGSize(width: size.frame.width * Self.scale(size), height: size.frame.height * Self.scale(size))
    }

    private func content(_ size: WidgetSize, snapshot: SharedSnapshot) -> some View {
        view(for: size, snapshot: snapshot)
            .padding(16)
            .frame(width: size.frame.width, height: size.frame.height)
            .scaleEffect(Self.scale(size), anchor: .topLeading)
    }

    @ViewBuilder
    private func view(for size: WidgetSize, snapshot: SharedSnapshot) -> some View {
        switch size {
        case .small: SmallWidgetView(snapshot: snapshot, date: date)
        case .medium: MediumWidgetView(snapshot: snapshot, date: date)
        case .large: LargeWidgetView(snapshot: snapshot, date: date)
        }
    }

    private var states: some View {
        HStack(spacing: 2) {
            ForEach(ShowcaseScene.allCases) { s in
                HStack(spacing: 5) {
                    Image(systemName: s.symbol).font(.system(size: 10.5, weight: .semibold))
                        .foregroundStyle(s == scene ? s.color : .white.opacity(0.75))
                    Text(s.title).font(.system(size: 11.5, weight: s == scene ? .semibold : .medium))
                        .foregroundStyle(.white.opacity(s == scene ? 1 : 0.8))
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background {
                    if s == scene {
                        Capsule().fill(.white.opacity(0.18))
                            .overlay(Capsule().strokeBorder(s.color.opacity(0.55), lineWidth: 0.75))
                    }
                }
            }
        }
        .padding(3)
        .background(Capsule().fill(.black.opacity(0.35)))
        .overlay(Capsule().strokeBorder(.white.opacity(0.12), lineWidth: 0.75))
    }
}

/// The main window for the README: a page (crossfading into the next) beside a drawn copy of the sidebar (the
/// real sidebar's material doesn't render offscreen).
/// The README window as it sits on the desktop: rounded, outlined, with its window buttons and shadow.
struct FramedWindow: View {
    let store: PrinterStore
    let section: AppSection
    var next: AppSection
    var blend: Double = 0
    static let standardSize = CGSize(width: 1000, height: 660)
    var size = standardSize

    var body: some View {
        ReadmeWindow(store: store, section: section, next: next, blend: blend)
            .frame(width: size.width, height: size.height)
            .overlay(alignment: .topLeading) { TrafficLights().padding(.leading, 20).padding(.top, 19) }
            .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.white.opacity(0.14), lineWidth: 1))
            .shadow(color: .black.opacity(0.45), radius: 18, y: 10)
    }
}

private struct ReadmeWindow: View {
    let store: PrinterStore
    let section: AppSection
    var next: AppSection
    /// 0 shows `section`, 1 shows `next`.
    var blend: Double

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            ZStack {
                page(section).opacity(1 - blend)
                if blend > 0 { page(next).opacity(blend) }
            }
            .background(PageBackground(status: store.status))
        }
        .environment(store)
        .environment(Navigation.shared)
        .environment(LoginItem.shared)
    }

    private func page(_ section: AppSection) -> some View {
        ScrollView {
            Group {
                switch section {
                case .fleet: FleetPage()
                case .filament: FilamentPage()
                default: OverviewPage()
                }
            }
            .frame(maxWidth: 860)
            .padding(.horizontal, 28)
            .padding(.vertical, 24)
            .frame(maxWidth: .infinity)
        }
        .scrollDisabled(true)
    }

    private var selected: AppSection { blend < 0.5 ? section : next }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 2) {
            Color.clear.frame(height: 44)
            if store.hasMultiplePrinters {
                picker.padding(.horizontal, 12).padding(.bottom, 8)
            }
            ForEach(AppSection.allCases.filter { $0 != .fleet || store.hasMultiplePrinters }) { row($0) }
            Spacer()
            if !store.hasMultiplePrinters {
                HStack(spacing: 10) {
                    PrinterArt(status: store.status, model: store.config?.modelName, animated: false)
                        .frame(width: 26, height: 31)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(store.printerName).font(.system(size: 12, weight: .semibold))
                        Text(store.status.map { "\($0.state.title) · \($0.progress)%" } ?? "")
                            .font(.system(size: 10.5)).foregroundStyle(.secondary)
                    }
                }
                .padding(.horizontal, 14)
                .padding(.bottom, 14)
            }
        }
        .frame(width: 210)
        .frame(maxHeight: .infinity)
        .background(Color(red: 0.13, green: 0.13, blue: 0.14))
        .overlay(alignment: .trailing) { Rectangle().fill(.white.opacity(0.06)).frame(width: 1) }
    }

    /// The printer switcher at the top of the sidebar, as `SidebarPrinterPicker` draws it.
    private var picker: some View {
        HStack(spacing: 10) {
            PrinterArt(status: store.status, model: store.config?.modelName, animated: false)
                .frame(width: 26, height: 31)
            VStack(alignment: .leading, spacing: 1) {
                Text(store.printerName).font(.system(size: 12.5, weight: .semibold)).lineLimit(1)
                HStack(spacing: 4) {
                    Circle().fill(store.status.map(Palette.color(for:)) ?? .secondary).frame(width: 7, height: 7)
                    Text(store.status.map { "\($0.state.title) · \($0.progress)%" } ?? "")
                        .font(.system(size: 10.5)).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 4)
            Image(systemName: "chevron.up.chevron.down").font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.06)))
    }

    private func row(_ section: AppSection) -> some View {
        let isSelected = section == selected
        return HStack(spacing: 8) {
            Image(systemName: section.icon)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(isSelected ? .white : Color.accentColor)
                .frame(width: 20)
            Text(section.title).font(.system(size: 13, weight: .medium))
            Spacer()
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(isSelected ? Color.accentColor : .clear))
        .padding(.horizontal, 10)
    }
}
