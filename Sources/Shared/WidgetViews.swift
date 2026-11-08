import SwiftUI
#if WIDGET_EXTENSION
    import WidgetKit
#endif

/// Widget layouts. They live in the shared sources so the app can show the very same views in Settings.
/// `date` is the timeline entry's date: progress and time left are projected to it, so the widget keeps
/// moving between the app's updates.
struct WidgetState {
    var snapshot: SharedSnapshot
    var date: Date

    var status: PrinterStatus? { snapshot.link == .online || snapshot.link == .connecting ? snapshot.status : nil }
    var isLive: Bool { status != nil }
    var state: JobState { status?.state ?? .idle }
    var stateTitle: String { status?.stateTitle ?? state.title }
    var stateColor: Color { status.map { Palette.color(for: $0) } ?? Palette.color(for: state) }

    var progress: Double {
        guard let s = status else { return 0 }
        return s.projectedProgress(at: date)
    }

    var percentText: String { "\(Int((progress * 100).rounded(.down)))" }

    var minutesLeft: Int? {
        guard let s = status, let end = s.estimatedEnd, s.state.isActive else { return nil }
        if s.state == .paused { return s.remainingMinutes }
        return max(0, Int((end.timeIntervalSince(date) / 60).rounded(.up)))
    }

    var tint: Color {
        if state == .paused { return Palette.paused }
        if state == .failed { return status?.canceled == true ? .secondary : Palette.failed }
        if let spool = status?.activeSpool {
            let l = bambuLuminance(spool.color)
            if l < 0.88 && l > 0.12, let c = Color(bambuHex: spool.color) { return c }
        }
        return Palette.accent
    }

    var headline: String {
        switch snapshot.link {
        case .unconfigured: return String(localized: "Set up Bambu Bar")
        case .offline: return String(localized: "Offline")
        case .unauthorized: return String(localized: "Access code rejected")
        case .connecting where status == nil: return String(localized: "Connecting…")
        default: break
        }
        guard let s = status else { return String(localized: "Offline") }
        if s.state == .idle { return s.state.title }
        return s.jobName.isEmpty ? s.state.title : s.jobName
    }

    var stateLine: String {
        guard let s = status else {
            switch snapshot.link {
            case .unconfigured: return String(localized: "Open the app to connect your printer")
            case .unauthorized: return String(localized: "Enter the access code again in the app")
            default: return lastJobLine ?? String(localized: "Waiting for the printer")
            }
        }
        if s.state == .printing, let left = minutesLeft {
            return String(localized: "\(Format.duration(minutes: left)) left", comment: "Time left, such as “2h 05m left”")
        }
        if s.state == .idle { return lastJobLine ?? String(localized: "Ready to print") }
        return s.detailText
    }

    var lastJobLine: String? {
        guard let last = snapshot.lastJob else { return nil }
        return String(localized: "Last: \(last.name)", comment: "The last print's name")
    }

    var finishText: String? {
        guard let left = minutesLeft, state == .printing else { return nil }
        return Format.finish(date.addingTimeInterval(TimeInterval(left * 60)), now: date)
    }
}

struct WidgetBackdrop: View {
    var body: some View {
        LinearGradient(colors: [Color(red: 0.16, green: 0.17, blue: 0.18), Color(red: 0.08, green: 0.09, blue: 0.10)],
                       startPoint: .top, endPoint: .bottom)
    }
}

// MARK: - Small

/// Numbers first: a progress ring with the percentage, time left and finish time, then the job.
/// Every state leads with what's useful in it (the reason for a pause, how long ago a print finished,
/// temperatures and spools when idle).
struct SmallWidgetView: View {
    var snapshot: SharedSnapshot
    var date: Date

    var body: some View {
        let w = WidgetState(snapshot: snapshot, date: date)
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 10) {
                SmallWidgetDial(w: w)
                    .frame(width: 58, height: 58)
                VStack(alignment: .leading, spacing: 1) {
                    Text(w.isLive ? w.stateTitle.uppercased() : w.headline.uppercased())
                        .font(.system(size: 9.5, weight: .bold))
                        .tracking(0.4)
                        .foregroundStyle(w.isLive ? w.stateColor : .secondary)
                        .lineLimit(1)
                        .widgetAccentableCompat()
                    primary(w)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: 4)
            if let s = w.status, s.state.isActive, let spool = s.activeSpool, spool.isLoaded {
                // The filament going into the print.
                HStack(spacing: 5) {
                    SpoolSwatch(spool: spool, active: true, size: 15)
                    Text("\(spool.displayName) · \(spool.slotName)")
                        .font(.system(size: 10.5, weight: .medium))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer(minLength: 4)
            }
            footer(w)
        }
        .foregroundStyle(.primary)
    }

    /// The figure beside the dial: time left while printing, the reason while paused, temperatures when idle.
    @ViewBuilder
    private func primary(_ w: WidgetState) -> some View {
        if let s = w.status {
            switch s.state {
            case .printing:
                Text(w.minutesLeft.map { Format.duration(minutes: $0) } ?? "–")
                    .font(.system(size: 19, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
                Text("left").font(.system(size: 10.5)).foregroundStyle(.secondary)
            case .preparing:
                Text(s.stageText ?? String(localized: "Getting ready"))
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
            case .paused:
                Text(s.pauseReason ?? String(localized: "Waiting", comment: "A paused print, reason unknown"))
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
            case .finished, .failed:
                if let end = snapshot.lastJob?.endedAt {
                    Text(Self.ago(end, w.date))
                        .font(.system(size: 17, weight: .semibold, design: .rounded))
                        .lineLimit(1)
                        .minimumScaleFactor(0.7)
                    if let d = snapshot.lastJob?.duration {
                        Text("took \(Format.duration(seconds: d))").font(.system(size: 10.5)).foregroundStyle(.secondary).lineLimit(1)
                    }
                } else {
                    Text(s.state == .finished ? "Done" : "Stopped")
                        .font(.system(size: 17, weight: .semibold, design: .rounded))
                }
            case .idle:
                temps(s)
            }
        } else {
            Text(snapshot.link == .unconfigured ? "Open the app" : "No connection")
                .font(.system(size: 12, weight: .semibold))
                .lineLimit(2)
        }
    }

    private func temps(_ s: PrinterStatus) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            tempLine("flame.fill", s.activeNozzle.current, Palette.nozzle, s.activeNozzle.isHeating)
            tempLine("heat.waves", s.bed.current, Palette.bed, s.bed.isHeating)
            if let c = s.chamber { tempLine("thermometer.medium", c, Palette.chamber, false) }
        }
        .padding(.top, 2)
    }

    private func tempLine(_ symbol: String, _ value: Double, _ tint: Color, _ hot: Bool) -> some View {
        HStack(spacing: 4) {
            Image(systemName: symbol).font(.system(size: 9, weight: .semibold)).foregroundStyle(hot ? tint : .secondary).frame(width: 12)
            Text(Format.temperature(value)).font(.system(size: 13, weight: .semibold, design: .rounded)).monospacedDigit()
        }
    }

    @ViewBuilder
    private func footer(_ w: WidgetState) -> some View {
        if let s = w.status {
            if s.state == .idle {
                // Idle: what's loaded, ready for the next job.
                if !s.allSpools.filter(\.isLoaded).isEmpty {
                    HStack(spacing: 5) {
                        ForEach(s.allSpools.filter(\.isLoaded).prefix(5)) { spool in
                            SpoolSwatch(spool: spool, size: 20)
                        }
                    }
                    .padding(.bottom, 4)
                }
                Text(snapshot.lastJob.map { "Last: \($0.name)" } ?? snapshot.printerName)
                    .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
            } else {
                Text(s.jobName.isEmpty ? snapshot.printerName : s.jobName)
                    .font(.system(size: 12.5, weight: .semibold))
                    .lineLimit(1)
                Text(detail(w, s))
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .monospacedDigit()
            }
        } else {
            Text(snapshot.printerName).font(.system(size: 12.5, weight: .semibold)).lineLimit(1)
            Text(w.stateLine).font(.system(size: 10.5)).foregroundStyle(.secondary).lineLimit(2)
        }
    }

    private func detail(_ w: WidgetState, _ s: PrinterStatus) -> String {
        let layers = s.totalLayers > 0 ? "\(s.layer)/\(s.totalLayers)" : nil
        switch s.state {
        case .printing:
            return [w.finishText.map { String(localized: "Done \($0)", comment: "Finish time, such as “Done 14:32”") }, layers].compactMap { $0 }.joined(separator: " · ")
        case .preparing:
            let n = s.activeNozzle
            return n.isHeating ? "\(s.activeNozzleTitle) \(Int(n.current))° → \(Int(n.target))°" : String(localized: "Starting soon")
        case .paused:
            return [layers.map { String(localized: "Layer \($0)", comment: "Such as “Layer 182/284”") }, "\(s.progress)%"].compactMap { $0 }.joined(separator: " · ")
        case .finished:
            return String(localized: "Ready to remove") + (layers.map { " · \($0)" } ?? "")
        case .failed:
            return s.canceled ? s.stateTitle : (s.issues.first?.message ?? String(localized: "Print failed"))
        case .idle:
            return ""
        }
    }

    static func ago(_ date: Date, _ now: Date) -> String {
        let minutes = Int(now.timeIntervalSince(date) / 60)
        if minutes < 1 { return String(localized: "Just now") }
        return String(localized: "\(Format.duration(minutes: minutes)) ago", comment: "Such as “2h 05m ago”")
    }
}

/// The small widget's dial: progress ring with the percentage, a check when done, the printer when idle.
struct SmallWidgetDial: View {
    var w: WidgetState

    var body: some View {
        let s = w.status
        ZStack {
            switch s?.state {
            case .printing?, .paused?:
                ProgressRing(progress: w.progress, tint: w.tint, lineWidth: 6.5)
                    .widgetAccentableCompat()
                VStack(spacing: -2) {
                    HStack(alignment: .firstTextBaseline, spacing: 0) {
                        Text(w.percentText).font(.system(size: 17, weight: .bold, design: .rounded)).monospacedDigit()
                        Text("%").font(.system(size: 9, weight: .bold, design: .rounded)).foregroundStyle(.secondary)
                    }
                    if s?.state == .paused {
                        Image(systemName: "pause.fill").font(.system(size: 8, weight: .bold)).foregroundStyle(Palette.paused)
                    }
                }
            case .preparing?:
                ProgressRing(progress: s.map { $0.activeNozzle.target > 0 ? min(1, $0.activeNozzle.current / $0.activeNozzle.target) : 0.1 } ?? 0.1,
                             tint: Palette.preparing, lineWidth: 6.5)
                    .widgetAccentableCompat()
                Image(systemName: "flame.fill").font(.system(size: 18, weight: .semibold)).foregroundStyle(Palette.nozzle)
            case .finished?:
                ProgressRing(progress: 1, tint: Palette.finished, lineWidth: 6.5)
                    .widgetAccentableCompat()
                Image(systemName: "checkmark").font(.system(size: 20, weight: .bold)).foregroundStyle(Palette.finished)
            case .failed?:
                ProgressRing(progress: w.progress, tint: s?.canceled == true ? .secondary : Palette.failed, lineWidth: 6.5)
                Image(systemName: s?.canceled == true ? "xmark" : "exclamationmark").font(.system(size: 19, weight: .bold))
                    .foregroundStyle(s?.canceled == true ? Color.secondary : Palette.failed)
            default:
                WidgetPrinterArt(status: s, dimmed: !w.isLive, model: w.snapshot.model)
                    .padding(2)
            }
        }
    }
}

// MARK: - Medium

struct MediumWidgetView: View {
    var snapshot: SharedSnapshot
    var date: Date
    var showsTemps = true

    var body: some View {
        let w = WidgetState(snapshot: snapshot, date: date)
        HStack(spacing: 14) {
            WidgetPrinterArt(status: w.status, dimmed: !w.isLive, model: snapshot.model)
                .frame(width: 92, height: 110)
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Text(snapshot.printerName)
                        .font(.system(size: 11.5, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                    Spacer()
                    if w.isLive {
                        Circle().fill(w.stateColor).frame(width: 6, height: 6).widgetAccentableCompat()
                        Text(w.stateTitle)
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(w.stateColor)
                            .widgetAccentableCompat()
                    }
                }
                Text(w.headline)
                    .font(.system(size: 15, weight: .semibold))
                    .lineLimit(1)
                    .padding(.top, 3)
                if w.isLive && w.state.isActive {
                    HStack(alignment: .firstTextBaseline, spacing: 1) {
                        Text(w.percentText)
                            .font(.system(size: 30, weight: .semibold, design: .rounded))
                            .monospacedDigit()
                        Text("%").font(.system(size: 15, weight: .semibold, design: .rounded)).foregroundStyle(.secondary)
                        Spacer()
                        if let s = w.status, s.totalLayers > 0 {
                            Text("\(s.layer)/\(s.totalLayers)")
                                .font(.system(size: 11, weight: .medium, design: .rounded))
                                .foregroundStyle(.secondary)
                                .monospacedDigit()
                        }
                    }
                    FilamentProgressBar(progress: w.progress, tint: w.tint, height: 6)
                        .widgetAccentableCompat()
                        .padding(.vertical, 5)
                    HStack {
                        Text(w.stateLine).lineLimit(1)
                        Spacer(minLength: 4)
                        if let finish = w.finishText { Text("Done \(finish)").foregroundStyle(.secondary) }
                    }
                    .font(.system(size: 11, weight: .medium))
                } else {
                    Text(w.stateLine)
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                        .padding(.top, 2)
                    Spacer(minLength: 6)
                    if let s = w.status { WidgetSpoolRow(status: s, size: 22) }
                }
                Spacer(minLength: 0)
                if showsTemps, let s = w.status, s.state.isActive {
                    WidgetTemps(status: s)
                }
            }
        }
    }
}

// MARK: - Large

struct LargeWidgetView: View {
    var snapshot: SharedSnapshot
    var date: Date

    var body: some View {
        let w = WidgetState(snapshot: snapshot, date: date)
        VStack(alignment: .leading, spacing: 0) {
            MediumWidgetView(snapshot: snapshot, date: date, showsTemps: false)
                .frame(height: 118)
            if let s = w.status {
                HStack(spacing: 8) {
                    WidgetTempTile(title: s.activeNozzleTitle, symbol: "flame.fill", value: s.activeNozzle.current, target: s.activeNozzle.target,
                                   tint: Palette.nozzle)
                    WidgetTempTile(title: String(localized: "Bed"), symbol: "heat.waves", value: s.bed.current, target: s.bed.target, tint: Palette.bed)
                    if let chamber = s.chamber {
                        WidgetTempTile(title: String(localized: "Chamber"), symbol: "thermometer.medium", value: chamber,
                                       target: s.chamberTarget, tint: Palette.chamber)
                    } else if let other = s.idleNozzle {
                        WidgetTempTile(title: s.idleNozzleTitle, symbol: "flame.fill", value: other.current, target: other.target,
                                       tint: Palette.nozzle)
                    } else if let fan = s.partFan {
                        WidgetStatTile(title: String(localized: "Part fan"), symbol: "fan.fill", value: "\(fan)%",
                                       tint: Palette.chamber, active: fan > 0)
                    }
                }
                .padding(.top, 14)
                if let unit = s.ams.first {
                    VStack(alignment: .leading, spacing: 9) {
                        HStack {
                            Text(unit.title.uppercased())
                                .font(.system(size: 10, weight: .bold)).tracking(0.4).foregroundStyle(.secondary)
                            Spacer()
                            if let h = unit.humidity {
                                Label("\(h)%", systemImage: "humidity.fill")
                                    .font(.system(size: 10.5, weight: .medium)).foregroundStyle(.secondary)
                            }
                        }
                        HStack(spacing: 0) {
                            ForEach(unit.spools) { spool in
                                VStack(spacing: 3) {
                                    SpoolSwatch(spool: spool, active: spool.id == s.activeSpoolID && s.state.isActive, size: 34)
                                    Text(spool.isLoaded ? spool.material : String(localized: "Empty", comment: "Filament slot"))
                                        .font(.system(size: 10.5, weight: .semibold)).lineLimit(1)
                                    Text(spool.isLoaded ? spool.amountText : spool.label)
                                        .font(.system(size: 10)).foregroundStyle(.secondary)
                                }
                                .frame(maxWidth: .infinity)
                            }
                        }
                    }
                    .padding(.top, 14)
                }
                Spacer(minLength: 8)
                footer(w, status: s)
            } else {
                Spacer()
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: snapshot.link == .unconfigured ? "printer.fill" : "wifi.exclamationmark")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.secondary)
                    Text(snapshot.link == .unconfigured
                         ? "Open Bambu Bar, pick your printer and enter its access code. Progress, temperatures and filament show up here."
                         : "Bambu Bar shows the printer here again as soon as it's back on the network.")
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.07)))
            }
        }
    }

    @ViewBuilder
    private func footer(_ w: WidgetState, status s: PrinterStatus) -> some View {
        if let issue = s.mostSevereIssue {
            HStack(alignment: .top, spacing: 6) {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Palette.paused)
                Text(issue.message).lineLimit(2)
            }
            .font(.system(size: 11, weight: .medium))
        } else if let last = snapshot.lastJob {
            HStack(spacing: 6) {
                Image(systemName: last.outcome == .finished ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .foregroundStyle(last.outcome == .finished ? Palette.finished : Palette.failed)
                Text("Last: \(last.name)").lineLimit(1)
                Spacer(minLength: 4)
                if let end = last.endedAt {
                    Text(Self.relative.localizedString(for: end, relativeTo: w.date)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            .font(.system(size: 11, weight: .medium))
        }
    }

    private static let relative: RelativeDateTimeFormatter = {
        let f = RelativeDateTimeFormatter()
        f.unitsStyle = .short
        return f
    }()
}

struct WidgetTempTile: View {
    var title: String
    var symbol: String
    var value: Double?
    /// nil for sensors without a heater.
    var target: Double?
    var tint: Color

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 3) {
                Image(systemName: symbol).font(.system(size: 9, weight: .semibold)).foregroundStyle((target ?? 0) > 0 ? tint : .secondary)
                Text(title).font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
            }
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(value.map { Format.temperature($0) } ?? "–")
                    .font(.system(size: 18, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                // The target only while the heater is still getting there.
                if let target, target > 0, let value, abs(value - target) > 2 {
                    Text("→ \(Int(target))°")
                        .font(.system(size: 10, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .foregroundStyle(tint)
                }
            }
            .lineLimit(1)
            .minimumScaleFactor(0.7)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 9)
        .padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 11, style: .continuous).fill(Color.primary.opacity(0.07)))
    }
}

/// A tile like the temperature ones for a plain reading (fan speed on models without a chamber sensor).
struct WidgetStatTile: View {
    var title: String
    var symbol: String
    var value: String
    var tint: Color
    var active: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 3) {
                Image(systemName: symbol).font(.system(size: 9, weight: .semibold)).foregroundStyle(active ? tint : .secondary)
                Text(title).font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
            }
            Text(value)
                .font(.system(size: 18, weight: .semibold, design: .rounded))
                .monospacedDigit()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 9)
        .padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 11, style: .continuous).fill(Color.primary.opacity(0.07)))
    }
}

struct WidgetTemps: View {
    var status: PrinterStatus

    var body: some View {
        HStack(spacing: 12) {
            item("flame.fill", status.activeNozzle.current, status.activeNozzle.target, Palette.nozzle)
            item("heat.waves", status.bed.current, status.bed.target, Palette.bed)
            if let c = status.chamber { item("thermometer.medium", c, 0, Palette.chamber) }
            Spacer(minLength: 0)
        }
        .font(.system(size: 11, weight: .medium, design: .rounded))
        .monospacedDigit()
    }

    private func item(_ symbol: String, _ value: Double, _ target: Double, _ tint: Color) -> some View {
        HStack(spacing: 3) {
            Image(systemName: symbol).font(.system(size: 9, weight: .semibold)).foregroundStyle(target > 0 ? tint : .secondary)
            Text(Format.temperature(value))
        }
    }
}

struct WidgetSpoolRow: View {
    var status: PrinterStatus
    var size: CGFloat

    var body: some View {
        HStack(spacing: 6) {
            ForEach(status.allSpools.filter { $0.isLoaded || !$0.isExternal }.prefix(8)) { spool in
                SpoolSwatch(spool: spool, active: spool.id == status.activeSpoolID && status.state.isActive, size: size)
            }
        }
    }
}

/// The printer drawing, switching to line art when the desktop renders the widget in one colour.
struct WidgetPrinterArt: View {
    var status: PrinterStatus?
    var dimmed: Bool
    var model: String?
    #if WIDGET_EXTENSION
        @Environment(\.widgetRenderingMode) private var renderingMode
        private var monochrome: Bool { renderingMode != .fullColor }
    #else
        private let monochrome = false
    #endif

    var body: some View {
        PrinterArt(status: status, model: model, dimmed: dimmed, animated: false, monochrome: monochrome)
    }
}

extension View {
    /// Marks views that should take the accent tint when the desktop renders widgets monochrome.
    @ViewBuilder
    func widgetAccentableCompat() -> some View {
        #if WIDGET_EXTENSION
            self.widgetAccentable()
        #else
            self
        #endif
    }
}
