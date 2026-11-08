import AppKit
import SwiftUI

extension PrinterShape {
    /// Base name of this shape's renders and model in the bundle ("enclosed-body.png", "enclosed.usdz").
    var assetName: String {
        switch self {
        case .enclosed: return "enclosed"
        case .wide: return "wide"
        case .h3c: return "h3c"
        case .openFrame: return "openFrame"
        case .bedSlinger: return "bedSlinger"
        case .mini: return "mini"
        }
    }
}

/// The Blender renders of one printer shape (see `artwork/generate_printers.py`), loaded once.
final class PrinterRenderSet: @unchecked Sendable {
    struct Box: Decodable {
        var x: CGFloat, y: CGFloat, w: CGFloat, h: CGFloat
    }

    private struct Anchors: Decodable {
        var size: [CGFloat]
        var part: Box
        var model: Box?
    }

    let body: NSImage
    let dark: NSImage
    /// Without the AMS, for printers that have none.
    let bare: NSImage?
    let bareDark: NSImage?
    let part: NSImage
    let spools: [NSImage]
    let sheen: NSImage?
    let canvas: CGSize
    /// Where the finished print sits, in canvas pixels from the top left.
    let partBox: Box
    /// The whole model, for the contact shadow under it.
    let modelBox: Box?

    private init?(name: String) {
        func image(_ suffix: String) -> NSImage? {
            Bundle.main.url(forResource: "\(name)-\(suffix)", withExtension: "png").flatMap(NSImage.init(contentsOf:))
        }
        guard let body = image("body"), let dark = image("dark"), let part = image("part"),
              let url = Bundle.main.url(forResource: name, withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let anchors = try? JSONDecoder().decode(Anchors.self, from: data), anchors.size.count == 2 else { return nil }
        self.body = body
        self.dark = dark
        bare = image("bare")
        bareDark = image("baredark")
        self.part = part
        // Four AMS spools, or the H3C's nine.
        spools = (0..<9).compactMap { image("spool\($0)") }
        sheen = image("sheen")
        canvas = CGSize(width: anchors.size[0], height: anchors.size[1])
        partBox = anchors.part
        modelBox = anchors.model
    }

    /// `image` multiplied by `color` inside its own alpha: shading kept, filament colour applied. Done on the CPU
    /// rather than with SwiftUI's colour filters so it looks the same in widgets and offscreen snapshots.
    func tinted(_ image: NSImage, _ color: Color) -> NSImage {
        let ns = NSColor(color).usingColorSpace(.sRGB) ?? .white
        let key = "\(ObjectIdentifier(image).hashValue)-\(ns.redComponent)-\(ns.greenComponent)-\(ns.blueComponent)" as NSString
        if let hit = tints.object(forKey: key) { return hit }
        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let ctx = CGContext(data: nil, width: cg.width, height: cg.height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return image }
        let rect = CGRect(x: 0, y: 0, width: cg.width, height: cg.height)
        ctx.draw(cg, in: rect)
        ctx.setBlendMode(.multiply)
        ctx.setFillColor(ns.cgColor)
        ctx.fill(rect)
        ctx.setBlendMode(.destinationIn)
        ctx.draw(cg, in: rect)
        guard let out = ctx.makeImage() else { return image }
        let result = NSImage(cgImage: out, size: image.size)
        tints.setObject(result, forKey: key)
        return result
    }

    private let tints = NSCache<NSString, NSImage>()

    private static let lock = NSLock()
    nonisolated(unsafe) private static var cache: [String: PrinterRenderSet?] = [:]

    static func named(_ name: String) -> PrinterRenderSet? {
        lock.lock()
        defer { lock.unlock() }
        if let hit = cache[name] { return hit }
        let set = PrinterRenderSet(name: name)
        cache[name] = set
        return set
    }
}

/// The rendered printer: body with the chamber light on or off, the AMS spools tinted with their filament,
/// and the print revealed from the plate up as it progresses, in the active filament's colour.
struct PrinterRender: View {
    var renders: PrinterRenderSet
    var state: JobState
    var progress: Double
    var filament: Color?
    var spools: [Color?]
    var lightOn: Bool
    var dimmed: Bool

    var body: some View {
        GeometryReader { geo in
            let scale = min(geo.size.width / renders.canvas.width, geo.size.height / renders.canvas.height)
            let size = CGSize(width: renders.canvas.width * scale, height: renders.canvas.height * scale)
            ZStack(alignment: .topLeading) {
                if let box = renders.modelBox {
                    // Soft contact shadow where the printer meets the desk.
                    Ellipse()
                        .fill(RadialGradient(colors: [.black.opacity(0.55), .black.opacity(0.2), .clear],
                                             center: .center, startRadius: 0, endRadius: box.w * scale * 0.5))
                        .frame(width: box.w * scale * 1.05, height: box.w * scale * 0.16)
                        .offset(x: (box.x - box.w * 0.025) * scale, y: (box.y + box.h) * scale - box.w * scale * 0.11)
                }
                layer(bodyImage)
                ForEach(Array(renders.spools.enumerated()), id: \.offset) { index, image in
                    if index < spools.count, let color = spools[index] {
                        layer(renders.tinted(image, color))
                    }
                }
                if shownFraction > 0 {
                    layer(renders.tinted(renders.part, filament ?? Palette.accent))
                        .mask(alignment: .topLeading) {
                            let box = renders.partBox
                            let top = (box.y + box.h * (1 - shownFraction)) * scale
                            Rectangle()
                                .frame(width: size.width, height: max(0, size.height - top))
                                .offset(y: top)
                        }
                }
                if let sheen = renders.sheen {
                    layer(sheen).opacity(0.85)
                }
            }
            .frame(width: size.width, height: size.height)
            .frame(width: geo.size.width, height: geo.size.height)
        }
        .aspectRatio(renders.canvas.width / renders.canvas.height, contentMode: .fit)
    }

    private var bodyImage: NSImage {
        let lit = lightOn && !dimmed
        if spools.isEmpty, let bare = renders.bare, let bareDark = renders.bareDark { return lit ? bare : bareDark }
        return lit ? renders.body : renders.dark
    }

    /// How much of the print stands on the plate.
    private var shownFraction: Double {
        switch state {
        case .finished: return 1
        case .idle, .failed: return min(1, progress)
        case .preparing: return progress > 0 ? max(0.03, progress) : 0
        case .printing, .paused:
            // Something is always on the plate once printing starts.
            return max(0.03, min(1, progress))
        }
    }

    private func layer(_ image: NSImage) -> some View {
        Image(nsImage: image)
            .resizable()
            .interpolation(.high)
            .aspectRatio(contentMode: .fit)
    }
}
