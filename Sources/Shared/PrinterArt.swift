import SwiftUI

/// Which drawing fits a printer model.
enum PrinterShape: Equatable, Sendable {
    /// P2S, P1S, X1 family, X2D: a closed box with a glass door.
    case enclosed
    /// H2 family and H3C: a wider closed box.
    case wide
    /// P1P: the CoreXY frame without panels.
    case openFrame
    /// A1 and A2L: an open bed-slinger under an arch-shaped gantry.
    case bedSlinger
    /// A1 mini: a bed-slinger with a single Z column.
    case mini

    /// From a model name ("P2S", "A1 mini") or SSDP code ("N7", "N1").
    init(model: String?) {
        if let known = PrinterModel.named(model) {
            self = known.shape
            return
        }
        let m = (model ?? "").lowercased()
        if m.contains("mini") || m == "n1" || m == "a12" || m == "a04" { self = .mini }
        else if m.hasPrefix("a1") || m.hasPrefix("a2") || m == "n2s" || m == "a11" || m == "n9" { self = .bedSlinger }
        else if m.hasPrefix("h2") || m.hasPrefix("h3") || m.hasPrefix("o1") || m.hasPrefix("o2") || m.hasPrefix("n10") { self = .wide }
        else if m == "p1p" || m == "c11" { self = .openFrame }
        else { self = .enclosed }
    }

    var isBedSlinger: Bool { self == .bedSlinger || self == .mini }
}

/// A drawn Bambu Lab printer: enclosure (or open frame), chamber light, build plate, and the part being printed,
/// which grows with progress in the active filament's colour. The AMS sits on top showing the loaded spools.
///
/// Everything is laid out on a 100 × 120 grid and scaled to the view, so the same drawing serves the
/// menu bar panel, the main window and the widgets.
struct PrinterArt: View {
    var state: JobState
    var progress: Double
    var filament: Color?
    var spools: [Color?] = []
    var activeSpool: Int?
    var lightOn = true
    var doorOpen = false
    var dimmed = false
    /// Toolhead motion; off in widgets and when the panel is static.
    var animated = true
    /// Outline drawing in white at varying opacity, for widgets the desktop renders monochrome (vibrant/accented).
    var monochrome = false
    var shape: PrinterShape = .enclosed

    /// Set when rendering README frames, so motion follows the frame clock instead of real time.
    @Environment(\.demoClock) private var demoClock

    var body: some View {
        if !monochrome, let renders = PrinterRenderSet.named(shape.assetName) {
            PrinterRender(renders: renders, state: state, progress: progress, filament: filament, spools: spools,
                          lightOn: lightOn, dimmed: dimmed)
                .saturation(dimmed ? 0.1 : 1)
                .opacity(dimmed ? 0.55 : 1)
                .accessibilityHidden(true)
        } else {
            drawing
        }
    }

    /// The vector drawing: monochrome widgets, and a fallback should the renders be missing.
    private var drawing: some View {
        Group {
            if let demoClock, animated, state == .printing, !dimmed {
                canvas(time: demoClock.timeIntervalSinceReferenceDate)
            } else if animated && state == .printing && !dimmed {
                TimelineView(.animation(minimumInterval: 1 / 30)) { context in
                    canvas(time: context.date.timeIntervalSinceReferenceDate)
                }
            } else {
                canvas(time: 0)
            }
        }
        .aspectRatio(100 / 120, contentMode: .fit)
        .saturation(dimmed && !monochrome ? 0.1 : 1)
        .opacity(dimmed ? 0.55 : 1)
        .accessibilityHidden(true)
    }

    private func canvas(time: Double) -> some View {
        Canvas { ctx, size in
            let s = min(size.width / 100, size.height / 120)
            ctx.translateBy(x: (size.width - 100 * s) / 2, y: (size.height - 120 * s) / 2)
            ctx.scaleBy(x: s, y: s)
            switch (shape.isBedSlinger, monochrome) {
            case (true, false): drawBedSlinger(&ctx, time: time)
            case (true, true): drawBedSlingerMonochrome(&ctx)
            case (false, true): drawMonochrome(&ctx)
            case (false, false): draw(&ctx, time: time)
            }
        }
    }

    private func draw(_ ctx: inout GraphicsContext, time: Double) {
        let ams = !spools.isEmpty
        let top: CGFloat = ams ? 22 : 8

        // Soft floor shadow.
        ctx.fill(Path(ellipseIn: CGRect(x: 10, y: 112, width: 80, height: 7)),
                 with: .radialGradient(Gradient(colors: [.black.opacity(0.35), .clear]),
                                       center: CGPoint(x: 50, y: 115.5), startRadius: 0, endRadius: 40))

        if ams { drawAMS(&ctx, y: 6) }

        let open = shape == .openFrame
        let inset: CGFloat = shape == .wide ? 3 : 8

        // Enclosure (for the P1P, just its frame).
        let body = CGRect(x: inset, y: top, width: 100 - inset * 2, height: 114 - top)
        let bodyPath = Path(roundedRect: body, cornerRadius: 7, style: .continuous)
        let chamber = CGRect(x: inset + 6, y: top + 6, width: 100 - inset * 2 - 12, height: 114 - top - 18)
        let chamberPath = Path(roundedRect: chamber, cornerRadius: 3.5, style: .continuous)
        if open {
            var frame = bodyPath
            frame.addPath(chamberPath)
            ctx.fill(frame, with: .linearGradient(
                Gradient(colors: [Color(white: 0.34), Color(white: 0.18)]),
                startPoint: CGPoint(x: 50, y: top), endPoint: CGPoint(x: 50, y: 114)), style: FillStyle(eoFill: true))
        } else {
            ctx.fill(bodyPath, with: .linearGradient(
                Gradient(colors: [Color(white: 0.30), Color(white: 0.16), Color(white: 0.11)]),
                startPoint: CGPoint(x: inset, y: top), endPoint: CGPoint(x: 100 - inset, y: 114)))
        }
        ctx.stroke(Path(roundedRect: body.insetBy(dx: 0.4, dy: 0.4), cornerRadius: 6.8, style: .continuous),
                   with: .linearGradient(Gradient(colors: [.white.opacity(0.35), .white.opacity(0.04)]),
                                         startPoint: CGPoint(x: 50, y: top), endPoint: CGPoint(x: 50, y: 114)),
                   lineWidth: 0.8)
        if shape == .wide {
            // The H2's vented top edge.
            for i in 0..<7 {
                ctx.fill(Path(roundedRect: CGRect(x: 36 + CGFloat(i) * 4.4, y: top + 2, width: 2.4, height: 1.2), cornerRadius: 0.6),
                         with: .color(.black.opacity(0.45)))
            }
        }

        // Chamber (behind the door).
        if !open {
            ctx.fill(chamberPath, with: .linearGradient(
                Gradient(colors: [Color(white: 0.07), Color(white: 0.12)]),
                startPoint: CGPoint(x: 50, y: chamber.minY), endPoint: CGPoint(x: 50, y: chamber.maxY)))
        }

        var inner = ctx
        inner.clip(to: chamberPath)

        // Chamber light: a warm wash from the top.
        if lightOn && !dimmed && !open {
            inner.fill(chamberPath, with: .linearGradient(
                Gradient(colors: [Color(red: 1, green: 0.95, blue: 0.85).opacity(0.38), .clear]),
                startPoint: CGPoint(x: 50, y: chamber.minY), endPoint: CGPoint(x: 50, y: chamber.minY + chamber.height * 0.75)))
            inner.fill(Path(roundedRect: CGRect(x: chamber.minX + 6, y: chamber.minY + 0.8, width: chamber.width - 12, height: 1.2), cornerRadius: 0.6),
                       with: .color(Color(red: 1, green: 0.97, blue: 0.9).opacity(0.95)))
        }

        // Gantry rails.
        let gantryY = chamber.minY + 9
        inner.fill(Path(CGRect(x: chamber.minX, y: gantryY - 0.6, width: chamber.width, height: 1.2)), with: .color(Color(white: 0.32)))

        // Build plate.
        let plateY = chamber.maxY - 11
        let plate = CGRect(x: chamber.minX + 5, y: plateY, width: chamber.width - 10, height: 2.6)
        inner.fill(Path(roundedRect: plate, cornerRadius: 0.8), with: .linearGradient(
            Gradient(colors: [Color(red: 0.42, green: 0.40, blue: 0.36), Color(red: 0.22, green: 0.21, blue: 0.20)]),
            startPoint: CGPoint(x: plate.minX, y: plate.minY), endPoint: CGPoint(x: plate.maxX, y: plate.maxY)))
        inner.fill(Path(CGRect(x: chamber.minX + 9, y: plateY + 2.6, width: chamber.width - 18, height: 4)), with: .color(Color(white: 0.16)))

        // The part.
        let maxHeight = plateY - gantryY - 9
        let p = max(0, min(1, progress))
        let showPart = state != .idle || p > 0
        let partHeight = showPart ? max(state.isActive ? 1.2 : 0, maxHeight * CGFloat(state == .finished ? 1 : p)) : 0
        let tint = filament ?? Palette.accent
        if partHeight > 0 {
            drawPart(&inner, baseY: plateY, height: partHeight, fullHeight: maxHeight, tint: tint)
        }

        // Toolhead rides at the top of the part while printing, otherwise parks at the back left.
        let headY: CGFloat = state.isActive ? max(gantryY + 1, plateY - partHeight - 7.5) : gantryY + 1
        let sweep = state == .printing ? sin(time * 2.6) * 0.5 + sin(time * 5.3) * 0.18 : 0
        let headX: CGFloat = state.isActive ? 50 + CGFloat(sweep) * 16 - 6 : chamber.minX + 3
        if state.isActive {
            inner.fill(Path(CGRect(x: chamber.minX, y: headY + 2.2, width: chamber.width, height: 1.1)), with: .color(Color(white: 0.36)))
        }
        drawToolhead(&inner, x: headX, y: headY, hot: state == .printing || state == .preparing, tint: tint, dual: shape == .wide)

        if !open { drawDoor(&ctx, chamber: chamber) }

        // Front strip with the status LED.
        let stripY = chamber.maxY + 5.2
        ctx.fill(Path(roundedRect: CGRect(x: 42, y: stripY - 0.7, width: 16, height: 1.4), cornerRadius: 0.7),
                 with: .color(statusLED.opacity(dimmed ? 0.3 : 0.95)))
        if !dimmed {
            ctx.fill(Path(ellipseIn: CGRect(x: 38, y: stripY - 3, width: 24, height: 6)),
                     with: .radialGradient(Gradient(colors: [statusLED.opacity(0.35), .clear]),
                                           center: CGPoint(x: 50, y: stripY), startRadius: 0, endRadius: 12))
        }
    }

    /// Door glass with a diagonal sheen and a handle.
    private func drawDoor(_ ctx: inout GraphicsContext, chamber: CGRect) {
        var door = ctx
        if doorOpen {
            door.translateBy(x: -4, y: 0)
            door.opacity = 0.7
        }
        let glass = Path(roundedRect: chamber.insetBy(dx: -1.5, dy: -1.5), cornerRadius: 4.5, style: .continuous)
        door.fill(glass, with: .color(Color(red: 0.55, green: 0.70, blue: 0.75).opacity(0.07)))
        var sheen = door
        sheen.clip(to: glass)
        var band = Path()
        band.move(to: CGPoint(x: chamber.minX + 14, y: chamber.minY - 2))
        band.addLine(to: CGPoint(x: chamber.minX + 30, y: chamber.minY - 2))
        band.addLine(to: CGPoint(x: chamber.minX + 2, y: chamber.maxY + 2))
        band.addLine(to: CGPoint(x: chamber.minX - 14, y: chamber.maxY + 2))
        band.closeSubpath()
        sheen.fill(band, with: .linearGradient(Gradient(colors: [.white.opacity(0.10), .white.opacity(0.02)]),
                                               startPoint: CGPoint(x: 30, y: chamber.minY), endPoint: CGPoint(x: 30, y: chamber.maxY)))
        door.stroke(glass, with: .color(.white.opacity(0.16)), lineWidth: 0.6)
        // Handle.
        door.fill(Path(roundedRect: CGRect(x: chamber.maxX - 4.5, y: chamber.midY - 7, width: 1.6, height: 14), cornerRadius: 0.8),
                  with: .color(.white.opacity(0.28)))
    }

    /// The same printer as line art: opacity carries the hierarchy, since the system tints everything one colour.
    private func drawMonochrome(_ ctx: inout GraphicsContext) {
        let white = Color.white
        let ams = !spools.isEmpty
        let top: CGFloat = ams ? 22 : 8
        if ams {
            let box = CGRect(x: 14, y: 6, width: 72, height: 14)
            ctx.stroke(Path(roundedRect: box, cornerRadius: 3.5, style: .continuous), with: .color(white.opacity(0.75)), lineWidth: 1.4)
            let slotW = (box.width - 8) / CGFloat(max(spools.count, 1))
            for (i, color) in spools.enumerated() {
                let r = CGRect(x: box.minX + 4 + CGFloat(i) * slotW + slotW / 2 - 3.6, y: box.midY - 3.6, width: 7.2, height: 7.2)
                ctx.fill(Path(ellipseIn: r), with: .color(white.opacity(color == nil ? 0.18 : 0.85)))
                if i == activeSpool {
                    ctx.stroke(Path(ellipseIn: r.insetBy(dx: -1.6, dy: -1.6)), with: .color(white), lineWidth: 1)
                }
            }
        }
        let body = CGRect(x: 8, y: top, width: 84, height: 114 - top)
        ctx.fill(Path(roundedRect: body, cornerRadius: 7, style: .continuous), with: .color(white.opacity(0.10)))
        ctx.stroke(Path(roundedRect: body.insetBy(dx: 0.9, dy: 0.9), cornerRadius: 6.4, style: .continuous),
                   with: .color(white.opacity(0.9)), lineWidth: 1.8)
        let chamber = CGRect(x: 14, y: top + 6, width: 72, height: 114 - top - 18)
        ctx.stroke(Path(roundedRect: chamber, cornerRadius: 3.5, style: .continuous), with: .color(white.opacity(0.45)), lineWidth: 1)
        if lightOn {
            ctx.fill(Path(roundedRect: CGRect(x: chamber.minX + 6, y: chamber.minY + 1.5, width: chamber.width - 12, height: 1.4), cornerRadius: 0.7),
                     with: .color(white.opacity(0.9)))
        }
        let gantryY = chamber.minY + 9
        ctx.fill(Path(CGRect(x: chamber.minX, y: gantryY - 0.5, width: chamber.width, height: 1)), with: .color(white.opacity(0.4)))
        let plateY = chamber.maxY - 11
        ctx.fill(Path(roundedRect: CGRect(x: chamber.minX + 5, y: plateY, width: chamber.width - 10, height: 2.6), cornerRadius: 0.8),
                 with: .color(white.opacity(0.7)))
        let maxHeight = plateY - gantryY - 9
        let p = CGFloat(max(0, min(1, progress)))
        let height = state == .finished ? maxHeight : (state == .idle && p == 0 ? 0 : max(state.isActive ? 1.2 : 0, maxHeight * p))
        if height > 0 {
            // A ghost of the whole part, filled up to the current layer.
            let full = CGRect(x: 36, y: plateY - maxHeight, width: 28, height: maxHeight)
            ctx.stroke(Path(roundedRect: full, cornerRadius: 4), with: .color(white.opacity(0.25)),
                       style: StrokeStyle(lineWidth: 0.8, dash: [2, 2]))
            ctx.fill(Path(roundedRect: CGRect(x: 36, y: plateY - height, width: 28, height: height), cornerRadius: 4),
                     with: .color(white.opacity(0.95)))
        }
        let headY: CGFloat = state.isActive ? max(gantryY + 1, plateY - height - 7.5) : gantryY + 1
        let headX: CGFloat = state.isActive ? 44 : chamber.minX + 3
        ctx.fill(Path(roundedRect: CGRect(x: headX, y: headY - 3, width: 12, height: 8), cornerRadius: 1.6, style: .continuous),
                 with: .color(white))
        ctx.fill(Path(roundedRect: CGRect(x: 42, y: chamber.maxY + 4.5, width: 16, height: 1.4), cornerRadius: 0.7),
                 with: .color(white.opacity(0.9)))
    }

    private var statusLED: Color {
        switch state {
        case .failed: return Palette.failed
        case .paused: return Palette.paused
        case .finished: return Palette.finished
        case .idle: return Color(white: 0.85)
        default: return Palette.accent
        }
    }

    /// A rounded vase, so the growing part reads as an object rather than a bar. Fine stripes suggest layers.
    private func drawPart(_ ctx: inout GraphicsContext, baseY: CGFloat, height: CGFloat, fullHeight: CGFloat, tint: Color) {
        let cx: CGFloat = 50
        func halfWidth(_ t: CGFloat) -> CGFloat {
            // t: 0 at the base, 1 at the full height.
            let belly = 15 + 4 * sin(t * .pi * 0.95)
            let neck = 1 - 0.42 * pow(max(0, t - 0.55) / 0.45, 1.6)
            return max(5, belly * neck)
        }
        var path = Path()
        let steps = 28
        let topT = height / fullHeight
        path.move(to: CGPoint(x: cx - halfWidth(0), y: baseY))
        for i in 0...steps {
            let t = topT * CGFloat(i) / CGFloat(steps)
            path.addLine(to: CGPoint(x: cx - halfWidth(t), y: baseY - t * fullHeight))
        }
        for i in stride(from: steps, through: 0, by: -1) {
            let t = topT * CGFloat(i) / CGFloat(steps)
            path.addLine(to: CGPoint(x: cx + halfWidth(t), y: baseY - t * fullHeight))
        }
        path.closeSubpath()

        ctx.drawLayer { layer in
            layer.fill(path, with: .color(tint))
            // Shading: light from the top-left, a soft edge on the right.
            layer.fill(path, with: .linearGradient(
                Gradient(stops: [.init(color: .white.opacity(0.28), location: 0), .init(color: .clear, location: 0.45),
                                 .init(color: .black.opacity(0.38), location: 1)]),
                startPoint: CGPoint(x: cx - 20, y: baseY), endPoint: CGPoint(x: cx + 20, y: baseY)))
            var stripes = layer
            stripes.clip(to: path)
            var y = baseY - 1.1
            while y > baseY - height {
                stripes.fill(Path(CGRect(x: cx - 30, y: y, width: 60, height: 0.35)), with: .color(.black.opacity(0.13)))
                y -= 1.1
            }
        }
        // The freshly extruded top edge glows a little while printing.
        if state == .printing {
            let t = topT
            let w = halfWidth(t)
            ctx.fill(Path(roundedRect: CGRect(x: cx - w, y: baseY - height - 0.4, width: w * 2, height: 0.9), cornerRadius: 0.45),
                     with: .color(.white.opacity(0.55)))
        }
    }

    private func drawToolhead(_ ctx: inout GraphicsContext, x: CGFloat, y: CGFloat, hot: Bool, tint: Color, dual: Bool = false) {
        let width: CGFloat = dual ? 16 : 12
        let head = CGRect(x: x - (width - 12) / 2, y: y - 3, width: width, height: 8)
        ctx.fill(Path(roundedRect: head, cornerRadius: 1.6, style: .continuous), with: .linearGradient(
            Gradient(colors: [Color(white: 0.92), Color(white: 0.70)]),
            startPoint: CGPoint(x: head.minX, y: head.minY), endPoint: CGPoint(x: head.maxX, y: head.maxY)))
        ctx.fill(Path(roundedRect: CGRect(x: head.minX + 1.5, y: y - 1.5, width: 5, height: 1), cornerRadius: 0.5),
                 with: .color(Palette.accent.opacity(0.9)))
        let centers: [CGFloat] = dual ? [head.minX + 4.5, head.maxX - 4.5] : [x + 6]
        for (i, cx) in centers.enumerated() {
            // On a dual head only the left nozzle is drawn hot.
            let isHot = hot && i == 0
            var nozzle = Path()
            nozzle.move(to: CGPoint(x: cx - 1.4, y: y + 5))
            nozzle.addLine(to: CGPoint(x: cx + 1.4, y: y + 5))
            nozzle.addLine(to: CGPoint(x: cx + 0.4, y: y + 7))
            nozzle.addLine(to: CGPoint(x: cx - 0.4, y: y + 7))
            nozzle.closeSubpath()
            ctx.fill(nozzle, with: .color(isHot ? Color(red: 1, green: 0.62, blue: 0.3) : Color(white: 0.6)))
            if isHot {
                ctx.fill(Path(ellipseIn: CGRect(x: cx - 3, y: y + 5, width: 6, height: 4)),
                         with: .radialGradient(Gradient(colors: [Color.orange.opacity(0.55), .clear]),
                                               center: CGPoint(x: cx, y: y + 7), startRadius: 0, endRadius: 3.5))
            }
        }
    }

    // MARK: - Bed-slingers (A1, A1 mini)

    /// An open printer: base, bed, Z column(s) and the X beam carrying the toolhead. The AMS Lite stands above as a rack.
    private func drawBedSlinger(_ ctx: inout GraphicsContext, time: Double) {
        let mini = shape == .mini
        let ams = !spools.isEmpty
        let top: CGFloat = ams ? 26 : 12
        let metal = GraphicsContext.Shading.linearGradient(Gradient(colors: [Color(white: 0.36), Color(white: 0.18)]),
                                                           startPoint: CGPoint(x: 0, y: top), endPoint: CGPoint(x: 0, y: 112))

        ctx.fill(Path(ellipseIn: CGRect(x: 8, y: 110, width: 84, height: 8)),
                 with: .radialGradient(Gradient(colors: [.black.opacity(0.35), .clear]),
                                       center: CGPoint(x: 50, y: 114), startRadius: 0, endRadius: 42))
        if ams { drawAMSLite(&ctx, y: 4) }

        // Z: an arch on the A1, one column on the mini.
        let leftPost = CGRect(x: mini ? 12 : 10, y: top, width: mini ? 7 : 5.5, height: 101 - top)
        ctx.fill(Path(roundedRect: leftPost, cornerRadius: 1.5), with: metal)
        if !mini {
            ctx.fill(Path(roundedRect: CGRect(x: 84.5, y: top, width: 5.5, height: 101 - top), cornerRadius: 1.5), with: metal)
            ctx.fill(Path(roundedRect: CGRect(x: 10, y: top, width: 80, height: 5), cornerRadius: 2), with: metal)
        } else {
            ctx.fill(Path(roundedRect: CGRect(x: 10.5, y: top - 2, width: 10, height: 4), cornerRadius: 1.5), with: .color(Color(white: 0.4)))
        }

        // Base with the screen.
        let base = CGRect(x: 6, y: 98, width: 88, height: 13)
        ctx.fill(Path(roundedRect: base, cornerRadius: 4, style: .continuous),
                 with: .linearGradient(Gradient(colors: [Color(white: 0.30), Color(white: 0.13)]),
                                       startPoint: CGPoint(x: 50, y: base.minY), endPoint: CGPoint(x: 50, y: base.maxY)))
        ctx.stroke(Path(roundedRect: base.insetBy(dx: 0.4, dy: 0.4), cornerRadius: 3.8, style: .continuous),
                   with: .color(.white.opacity(0.18)), lineWidth: 0.6)
        ctx.fill(Path(roundedRect: CGRect(x: 72, y: 101, width: 15, height: 7), cornerRadius: 1.2), with: .color(Color(white: 0.06)))
        ctx.fill(Path(roundedRect: CGRect(x: 73.5, y: 102.5, width: 12, height: 4), cornerRadius: 0.8),
                 with: .color(statusLED.opacity(dimmed ? 0.2 : 0.45)))

        // Bed on its Y carriage.
        let bedY: CGFloat = 92
        ctx.fill(Path(CGRect(x: 30, y: bedY + 2.6, width: 40, height: 3.4)), with: .color(Color(white: 0.14)))
        let plate = CGRect(x: mini ? 26 : 22, y: bedY, width: mini ? 48 : 56, height: 2.6)
        ctx.fill(Path(roundedRect: plate, cornerRadius: 0.8), with: .linearGradient(
            Gradient(colors: [Color(red: 0.42, green: 0.40, blue: 0.36), Color(red: 0.22, green: 0.21, blue: 0.20)]),
            startPoint: CGPoint(x: plate.minX, y: plate.minY), endPoint: CGPoint(x: plate.maxX, y: plate.maxY)))

        // The part.
        let beamTop = top + 8
        let maxHeight = bedY - beamTop - 12
        let p = CGFloat(max(0, min(1, progress)))
        let showPart = state != .idle || p > 0
        let partHeight = showPart ? max(state.isActive ? 1.2 : 0, maxHeight * (state == .finished ? 1 : p)) : 0
        let tint = filament ?? Palette.accent
        if partHeight > 0 {
            drawPart(&ctx, baseY: bedY, height: partHeight, fullHeight: maxHeight, tint: tint)
        }

        // X beam rides up with the part; the toolhead sweeps along it while printing.
        let headY: CGFloat = state.isActive ? max(beamTop + 3, bedY - partHeight - 7.5) : beamTop + 3
        let beamRight: CGFloat = mini ? 84 : 86
        ctx.fill(Path(roundedRect: CGRect(x: leftPost.minX, y: headY + 1.6, width: beamRight - leftPost.minX, height: 2.4), cornerRadius: 1.2),
                 with: .color(Color(white: 0.42)))
        let sweep = state == .printing ? sin(time * 2.6) * 0.5 + sin(time * 5.3) * 0.18 : 0
        let headX: CGFloat = state.isActive ? 44 + CGFloat(sweep) * 16 : beamRight - 14
        drawToolhead(&ctx, x: headX, y: headY, hot: state == .printing || state == .preparing, tint: tint)
    }

    /// The AMS Lite: spools standing on a rack.
    private func drawAMSLite(_ ctx: inout GraphicsContext, y: CGFloat) {
        let count = max(spools.count, 1)
        let span: CGFloat = 64
        let step = span / CGFloat(count)
        ctx.fill(Path(roundedRect: CGRect(x: 18, y: y + 13, width: 64, height: 2.4), cornerRadius: 1.2), with: .color(Color(white: 0.4)))
        for (i, color) in spools.enumerated() {
            let cx = 18 + step * (CGFloat(i) + 0.5)
            let r = CGRect(x: cx - 6.5, y: y, width: 13, height: 13)
            ctx.fill(Path(ellipseIn: r), with: .color(Color(white: 0.12)))
            if let color {
                ctx.fill(Path(ellipseIn: r.insetBy(dx: 1.4, dy: 1.4)), with: .color(color))
                ctx.fill(Path(ellipseIn: r.insetBy(dx: 1.4, dy: 1.4)),
                         with: .linearGradient(Gradient(colors: [.white.opacity(0.35), .black.opacity(0.25)]),
                                               startPoint: CGPoint(x: r.minX, y: r.minY), endPoint: CGPoint(x: r.maxX, y: r.maxY)))
            }
            ctx.fill(Path(ellipseIn: CGRect(x: cx - 1.6, y: y + 4.9, width: 3.2, height: 3.2)), with: .color(Color(white: 0.1)))
            if i == activeSpool {
                ctx.stroke(Path(ellipseIn: r.insetBy(dx: -0.6, dy: -0.6)), with: .color(Palette.accent), lineWidth: 1)
            }
        }
    }

    private func drawBedSlingerMonochrome(_ ctx: inout GraphicsContext) {
        let white = Color.white
        let mini = shape == .mini
        let ams = !spools.isEmpty
        let top: CGFloat = ams ? 26 : 12
        if ams {
            let step: CGFloat = 64 / CGFloat(max(spools.count, 1))
            for (i, color) in spools.enumerated() {
                let r = CGRect(x: 18 + step * (CGFloat(i) + 0.5) - 6, y: 4, width: 12, height: 12)
                ctx.fill(Path(ellipseIn: r), with: .color(white.opacity(color == nil ? 0.18 : 0.85)))
                if i == activeSpool { ctx.stroke(Path(ellipseIn: r.insetBy(dx: -1.4, dy: -1.4)), with: .color(white), lineWidth: 1) }
            }
        }
        let line = StrokeStyle(lineWidth: 1.8, lineCap: .round, lineJoin: .round)
        var frame = Path()
        frame.move(to: CGPoint(x: mini ? 15 : 13, y: 98))
        frame.addLine(to: CGPoint(x: mini ? 15 : 13, y: top))
        if !mini {
            frame.addLine(to: CGPoint(x: 87, y: top))
            frame.addLine(to: CGPoint(x: 87, y: 98))
        }
        ctx.stroke(frame, with: .color(white.opacity(0.85)), style: line)
        ctx.stroke(Path(roundedRect: CGRect(x: 6, y: 98, width: 88, height: 13), cornerRadius: 4), with: .color(white.opacity(0.85)), lineWidth: 1.8)
        let bedY: CGFloat = 92
        ctx.fill(Path(roundedRect: CGRect(x: mini ? 26 : 22, y: bedY, width: mini ? 48 : 56, height: 2.6), cornerRadius: 0.8),
                 with: .color(white.opacity(0.7)))
        let maxHeight = bedY - (top + 8) - 12
        let p = CGFloat(max(0, min(1, progress)))
        let height = state == .finished ? maxHeight : (state == .idle && p == 0 ? 0 : max(state.isActive ? 1.2 : 0, maxHeight * p))
        if height > 0 {
            ctx.stroke(Path(roundedRect: CGRect(x: 36, y: bedY - maxHeight, width: 28, height: maxHeight), cornerRadius: 4),
                       with: .color(white.opacity(0.25)), style: StrokeStyle(lineWidth: 0.8, dash: [2, 2]))
            ctx.fill(Path(roundedRect: CGRect(x: 36, y: bedY - height, width: 28, height: height), cornerRadius: 4),
                     with: .color(white.opacity(0.95)))
        }
        let headY: CGFloat = state.isActive ? max(top + 11, bedY - height - 7.5) : top + 11
        ctx.fill(Path(CGRect(x: mini ? 15 : 13, y: headY + 2, width: mini ? 69 : 74, height: 1.6)), with: .color(white.opacity(0.5)))
        ctx.fill(Path(roundedRect: CGRect(x: state.isActive ? 44 : 70, y: headY - 3, width: 12, height: 8), cornerRadius: 1.6),
                 with: .color(white))
    }

    private func drawAMS(_ ctx: inout GraphicsContext, y: CGFloat) {
        let box = CGRect(x: 14, y: y, width: 72, height: 14)
        let path = Path(roundedRect: box, cornerRadius: 3.5, style: .continuous)
        ctx.fill(path, with: .linearGradient(Gradient(colors: [Color(white: 0.34), Color(white: 0.18)]),
                                             startPoint: CGPoint(x: 50, y: box.minY), endPoint: CGPoint(x: 50, y: box.maxY)))
        ctx.stroke(path, with: .color(.white.opacity(0.18)), lineWidth: 0.6)
        let count = max(spools.count, 1)
        let slotW = (box.width - 8) / CGFloat(count)
        for (i, color) in spools.enumerated() {
            let r = CGRect(x: box.minX + 4 + CGFloat(i) * slotW + 1, y: box.minY + 3, width: slotW - 2, height: 8)
            let slot = Path(roundedRect: r, cornerRadius: 2, style: .continuous)
            ctx.fill(slot, with: .color(Color(white: 0.06)))
            if let color {
                let spool = Path(ellipseIn: CGRect(x: r.midX - 3.6, y: r.midY - 3.6, width: 7.2, height: 7.2))
                ctx.fill(spool, with: .color(color))
                ctx.fill(spool, with: .linearGradient(Gradient(colors: [.white.opacity(0.35), .black.opacity(0.25)]),
                                                      startPoint: CGPoint(x: r.midX - 3, y: r.midY - 3), endPoint: CGPoint(x: r.midX + 3, y: r.midY + 3)))
                ctx.fill(Path(ellipseIn: CGRect(x: r.midX - 1.1, y: r.midY - 1.1, width: 2.2, height: 2.2)), with: .color(Color(white: 0.1)))
            }
            if i == activeSpool {
                ctx.stroke(Path(roundedRect: r.insetBy(dx: -0.4, dy: -0.4), cornerRadius: 2.2, style: .continuous),
                           with: .color(Palette.accent), lineWidth: 0.9)
            }
        }
        // Lid sheen.
        ctx.fill(Path(roundedRect: CGRect(x: box.minX + 3, y: box.minY + 0.8, width: box.width - 6, height: 1.4), cornerRadius: 0.7),
                 with: .color(.white.opacity(0.12)))
    }
}

extension PrinterArt {
    /// The drawing for a status, with the first AMS unit on top.
    init(status: PrinterStatus?, model: String? = nil, dimmed: Bool = false, animated: Bool = true, lightOverride: Bool? = nil,
         monochrome: Bool = false) {
        let s = status
        let unit = s?.ams.first
        let active = s?.activeSpool
        self.init(state: s?.state ?? .idle,
                  progress: Double(s?.progress ?? 0) / 100,
                  filament: active.flatMap { Color(bambuHex: $0.color) },
                  spools: unit?.spools.map { $0.isLoaded ? Color(bambuHex: $0.color) : nil } ?? [],
                  activeSpool: unit.flatMap { u in u.spools.firstIndex { $0.id == s?.activeSpoolID } },
                  lightOn: lightOverride ?? s?.chamberLightOn ?? false,
                  doorOpen: s?.doorOpen ?? false,
                  dimmed: dimmed || s == nil,
                  animated: animated,
                  monochrome: monochrome,
                  shape: PrinterShape(model: model))
    }
}

private struct DemoClockKey: EnvironmentKey {
    static let defaultValue: Date? = nil
}

private struct PlainPanelKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    /// The frame clock while rendering README animations (nil: real time).
    var demoClock: Date? {
        get { self[DemoClockKey.self] }
        set { self[DemoClockKey.self] = newValue }
    }

    /// The menu bar panel draws no material of its own (the README frames composite their own frosted backdrop).
    var plainPanel: Bool {
        get { self[PlainPanelKey.self] }
        set { self[PlainPanelKey.self] = newValue }
    }
}
