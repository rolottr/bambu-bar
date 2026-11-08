import AppKit
import Metal
import SceneKit
import SwiftUI

/// The printer as a live 3D model (`Resources/Printers/<shape>.usdz`, built in Blender): the print grows slice by
/// slice in the active filament, the bed (CoreXY) or gantry (bed slingers) follows the layers, the toolhead moves
/// while printing, spools take their filament colours and the chamber light follows the printer's. Drag to turn
/// it; double-click to reset. Falls back to the rendered drawing when the model can't load.
struct PrinterModelView: View {
    var art: PrinterArt

    var body: some View {
        if Bundle.main.url(forResource: art.shape.assetName, withExtension: "usdz") == nil {
            art
        } else if ReadmeFrames.isActive {
            ScriptedPrinterModel(art: art)
        } else {
            PrinterModelCanvas(art: art)
                .opacity(art.dimmed ? 0.6 : 1)
                .accessibilityElement()
                .accessibilityLabel("Printer model")
                .accessibilityValue(art.state.isActive ? String(localized: "\(art.state.title) \(Int(art.progress * 100)) percent", comment: "VoiceOver: state, then progress") : art.state.title)
        }
    }
}

/// README frames: the model rendered at the frame's clock, turning slowly, since SceneKit views don't draw
/// when a window is captured offscreen.
private struct ScriptedPrinterModel: View {
    var art: PrinterArt
    @Environment(\.demoClock) private var clock

    var body: some View {
        GeometryReader { geo in
            if geo.size.width > 0, let image = Self.render(art, size: geo.size, clock: clock) {
                Image(nsImage: image).resizable().interpolation(.high)
            } else {
                art
            }
        }
    }

    @MainActor private static var views: [PrinterShape: PrinterSceneView] = [:]

    @MainActor private static func render(_ art: PrinterArt, size: CGSize, clock: Date?) -> NSImage? {
        let view = views[art.shape] ?? {
            let view = PrinterSceneView(frame: .zero)
            view.scripted = true
            view.load(art.shape)
            return view
        }()
        views[art.shape] = view
        view.apply(art)
        let t = clock.map(ReadmeFrames.elapsed) ?? 0
        return view.image(size: size, scale: 2, time: t, turn: CGFloat(sin(t * 0.6)) * 0.34)
    }
}

private struct PrinterModelCanvas: NSViewRepresentable {
    var art: PrinterArt

    func makeNSView(context: Context) -> PrinterSceneView {
        let view = PrinterSceneView.reused(art.shape)
        view.apply(art)
        return view
    }

    func updateNSView(_ view: PrinterSceneView, context: Context) {
        if view.shape != art.shape { view.load(art.shape) }
        view.apply(art)
    }

    static func dismantleNSView(_ view: PrinterSceneView, coordinator: ()) {
        PrinterSceneView.keep(view)
    }
}

final class PrinterSceneView: SCNView {
    private(set) var shape: PrinterShape?
    private let pivot = SCNNode()
    private var content: SCNNode?
    private var parts: [SCNNode] = []
    private var spools: [(root: SCNNode, filament: SCNMaterial)] = []
    private var ams: SCNNode?
    private var partMaterial = SCNMaterial()
    private var lampMaterial: SCNMaterial?
    private let lampLight = SCNNode()
    private var carriage: (node: SCNNode, base: SCNVector3)?
    private var gantry: (node: SCNNode, base: SCNVector3)?
    private var bed: (node: SCNNode, base: SCNVector3)?
    private var head: (node: SCNNode, base: SCNVector3)?
    /// Height of the finished print, in model units.
    private var partHeight: CGFloat = 0
    /// The printer is printing, so the toolhead should move…
    private var moving = false
    /// …and it does while the view is on screen. Closed windows and the hidden menu bar panel stay alive,
    /// and SceneKit keeps drawing for as long as an action runs.
    private var animating = false
    private var onScreen = false
    private var occlusionObserver: NSObjectProtocol?
    /// The A series has no chamber light to switch.
    private var hasLamp = false

    private static let restYaw: CGFloat = 0.42
    private static let restPitch: CGFloat = 0.16
    private var yaw = restYaw
    private var pitch = restPitch
    private var lastDrag: NSPoint?
    /// Posed by `image(size:scale:time:turn:)` instead of animated (README frames).
    var scripted = false

    override init(frame: NSRect, options: [String: Any]? = nil) {
        super.init(frame: frame, options: options)
        backgroundColor = .clear
        antialiasingMode = .multisampling4X
        // The toolhead's eased moves look the same at 20 fps, and each frame costs the same CPU whatever
        // the antialiasing or shadows.
        preferredFramesPerSecond = 20
        autoenablesDefaultLighting = false
        allowsCameraControl = false
        scene = Self.studio(pivot: pivot, lamp: lampLight)
        pointOfView = scene?.rootNode.childNode(withName: "camera", recursively: false)
        orient()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// Loading a model takes a moment, and the menu bar panel drops its contents each time it closes,
    /// so views leaving the screen wait here for the next one.
    private static var spares: [PrinterShape: PrinterSceneView] = [:]

    static func reused(_ shape: PrinterShape) -> PrinterSceneView {
        if let view = spares.removeValue(forKey: shape) { return view }
        let view = PrinterSceneView(frame: .zero)
        view.load(shape)
        return view
    }

    static func keep(_ view: PrinterSceneView) {
        guard let shape = view.shape else { return }
        view.yaw = restYaw
        view.pitch = restPitch
        view.orient()
        spares[shape] = view
    }

    deinit {
        if let occlusionObserver { NotificationCenter.default.removeObserver(occlusionObserver) }
    }

    override var isOpaque: Bool { false }
    override var acceptsFirstResponder: Bool { true }

    // MARK: - Scene

    private static func studio(pivot: SCNNode, lamp: SCNNode) -> SCNScene {
        let scene = SCNScene()
        scene.rootNode.addChildNode(pivot)
        // A studio dome for reflections: dark floor, bright overhead.
        let size = NSSize(width: 256, height: 128)
        let dome = NSImage(size: size)
        dome.lockFocus()
        NSGradient(colors: [NSColor(white: 0.02, alpha: 1), NSColor(white: 0.14, alpha: 1), NSColor(white: 0.95, alpha: 1)],
                   atLocations: [0, 0.55, 1], colorSpace: .sRGB)?.draw(in: NSRect(origin: .zero, size: size), angle: 90)
        dome.unlockFocus()
        scene.lightingEnvironment.contents = dome
        scene.lightingEnvironment.intensity = 1.3

        func light(_ type: SCNLight.LightType, _ position: SCNVector3, _ color: NSColor, _ intensity: CGFloat, shadow: Bool = false) {
            let node = SCNNode()
            node.light = SCNLight()
            node.light?.type = type
            node.light?.color = color
            node.light?.intensity = intensity
            if shadow {
                node.light?.castsShadow = true
                node.light?.shadowMode = .deferred
                node.light?.shadowColor = NSColor(white: 0, alpha: 0.55)
                node.light?.shadowRadius = 10
                node.light?.shadowSampleCount = 16
                node.light?.orthographicScale = 1
                node.light?.automaticallyAdjustsShadowProjection = true
            }
            node.position = position
            node.look(at: SCNVector3Zero)
            scene.rootNode.addChildNode(node)
        }
        light(.directional, SCNVector3(-0.6, 1.0, 0.9), NSColor(red: 1, green: 0.97, blue: 0.93, alpha: 1), 950, shadow: true)
        light(.directional, SCNVector3(0.8, 0.25, 0.5), NSColor(red: 0.85, green: 0.9, blue: 1, alpha: 1), 280)
        light(.directional, SCNVector3(0.4, 0.6, -0.9), NSColor(red: 0.8, green: 0.88, blue: 1, alpha: 1), 650)

        // The chamber light: a warm, wide spot pointing down from the ceiling.
        lamp.light = SCNLight()
        lamp.light?.type = .spot
        lamp.light?.color = NSColor(red: 1, green: 0.93, blue: 0.82, alpha: 1)
        lamp.light?.spotInnerAngle = 50
        lamp.light?.spotOuterAngle = 120
        lamp.light?.attenuationStartDistance = 0.05
        lamp.light?.attenuationEndDistance = 0.5
        lamp.light?.attenuationFalloffExponent = 1.5
        lamp.light?.intensity = 0
        lamp.eulerAngles.x = -.pi / 2
        pivot.addChildNode(lamp)

        let camera = SCNNode()
        camera.name = "camera"
        camera.camera = SCNCamera()
        camera.camera?.fieldOfView = 17
        camera.camera?.zNear = 0.05
        camera.camera?.zFar = 20
        camera.camera?.wantsHDR = true
        camera.camera?.wantsExposureAdaptation = false
        scene.rootNode.addChildNode(camera)
        return scene
    }

    func load(_ shape: PrinterShape) {
        self.shape = shape
        content?.removeFromParentNode()
        content = nil
        parts = []
        spools = []
        guard let url = Bundle.main.url(forResource: shape.assetName, withExtension: "usdz"),
              let product = try? SCNScene(url: url) else { return }
        let root = product.rootNode.clone()
        let (lo, hi) = root.boundingBox
        let centre = SCNVector3((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, (lo.z + hi.z) / 2)
        root.position = SCNVector3(-centre.x, -centre.y, -centre.z)
        pivot.addChildNode(root)
        content = root

        prepareMaterials(root)
        parts = (0..<64).compactMap { root.childNode(withName: String(format: "Part_%02d", $0), recursively: true) }
        partMaterial = SCNMaterial()
        partMaterial.lightingModel = .physicallyBased
        partMaterial.roughness.contents = 0.42
        partMaterial.metalness.contents = 0.0
        for part in parts { part.enumerateHierarchy { node, _ in node.geometry = node.geometry.map(Self.retinted(self.partMaterial)) } }
        if let first = parts.first, let last = parts.last {
            partHeight = CGFloat(root.convertPosition(last.boundingBox.max, from: last).y - root.convertPosition(first.boundingBox.min, from: first).y)
        }

        ams = root.childNode(withName: "AMS", recursively: true)
        spools = (0..<9).compactMap { i -> (SCNNode, SCNMaterial)? in
            guard let spool = root.childNode(withName: "Spool_\(i)", recursively: true),
                  let filament = spool.childNode(withName: "Filament_\(i)", recursively: true) else { return nil }
            let material = SCNMaterial()
            material.lightingModel = .physicallyBased
            material.roughness.contents = 0.35
            filament.enumerateHierarchy { node, _ in node.geometry = node.geometry.map(Self.retinted(material)) }
            return (spool, material)
        }

        func track(_ name: String) -> (SCNNode, SCNVector3)? {
            root.childNode(withName: name, recursively: true).map { ($0, $0.position) }
        }
        carriage = track("BedCarriage")
        gantry = track("Gantry")
        bed = track("Bed")
        head = track("Toolhead")
        moving = false
        animating = false

        hasLamp = false
        if let lamp = root.childNode(withName: "Lamp", recursively: true) {
            hasLamp = true
            lampLight.position = pivot.convertPosition(SCNVector3Zero, from: lamp)
        }
        addShadowFloor(below: CGFloat(lo.y - centre.y))
        frameCamera(radius: CGFloat(max(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z)) * 0.62)
    }

    /// Glass gets fresh materials with real transparency: the USD export carries no transmission, and the imported
    /// glass materials don't draw at all. The lamp keeps its own material so its emission can be switched.
    private func prepareMaterials(_ root: SCNNode) {
        func glass(smoke: Bool) -> SCNMaterial {
            let material = SCNMaterial()
            material.lightingModel = .physicallyBased
            material.diffuse.contents = NSColor(white: smoke ? 0.02 : 0.08, alpha: 1)
            material.roughness.contents = smoke ? 0.08 : 0.05
            material.metalness.contents = 0.0
            material.writesToDepthBuffer = false
            material.isDoubleSided = true
            return material
        }
        let door = glass(smoke: false), lid = glass(smoke: true)
        root.enumerateHierarchy { node, _ in
            guard let geometry = node.geometry else { return }
            geometry.materials = geometry.materials.map { material in
                let name = material.name ?? ""
                // Node opacity, not material transparency: only that blends reliably here.
                if name.hasPrefix("BB_Glass") { node.opacity = 0.16; node.renderingOrder = 10; return door }
                if name.hasPrefix("BB_Smoke") { node.opacity = 0.55; node.renderingOrder = 10; return lid }
                material.lightingModel = .physicallyBased
                if name.hasPrefix("BB_Lamp") { lampMaterial = material }
                return material
            }
        }
    }

    private static func retinted(_ material: SCNMaterial) -> (SCNGeometry) -> SCNGeometry {
        { geometry in
            let copy = geometry.copy() as! SCNGeometry
            copy.materials = [material]
            return copy
        }
    }

    private func addShadowFloor(below y: CGFloat) {
        pivot.childNode(withName: "floor", recursively: false)?.removeFromParentNode()
        let plane = SCNPlane(width: 2, height: 2)
        let material = SCNMaterial()
        material.lightingModel = .shadowOnly
        plane.materials = [material]
        let floor = SCNNode(geometry: plane)
        floor.name = "floor"
        floor.eulerAngles.x = -.pi / 2
        floor.position = SCNVector3(0, y + 0.001, 0)
        pivot.addChildNode(floor)
    }

    private func frameCamera(radius: CGFloat) {
        guard let camera = pointOfView, let fov = camera.camera?.fieldOfView else { return }
        let distance = radius / sin(fov / 2 * .pi / 180) * 0.92
        camera.position = SCNVector3(0, 0, distance)
        camera.camera?.zFar = distance * 4
    }

    // MARK: - State

    func apply(_ art: PrinterArt) {
        let fraction = shownFraction(art)
        let visible = Int((fraction * Double(parts.count)).rounded(.up))
        for (i, part) in parts.enumerated() { part.isHidden = i >= visible }
        partMaterial.diffuse.contents = NSColor(art.filament ?? Palette.accent)

        ams?.isHidden = art.spools.isEmpty
        for (i, spool) in spools.enumerated() {
            let color = i < art.spools.count ? art.spools[i] : nil
            spool.root.isHidden = color == nil
            if let color { spool.filament.diffuse.contents = NSColor(color) }
        }

        let lit = art.lightOn && !art.dimmed
        lampLight.light?.intensity = lit && hasLamp ? 4 : 0
        lampMaterial?.emission.contents = lit ? NSColor(red: 1, green: 0.95, blue: 0.86, alpha: 1) : NSColor.black

        // The bed (CoreXY) drops, or the gantry (bed slinger) rises, by what has been printed.
        let rest = partHeight * CGFloat(1 - fraction)
        if let carriage, let parent = carriage.node.parent, let content {
            carriage.node.position = carriage.base + parent.convertVector(SCNVector3(0, rest, 0), from: content)
        }
        if let gantry, let parent = gantry.node.parent, let content {
            gantry.node.position = gantry.base - parent.convertVector(SCNVector3(0, rest, 0), from: content)
        }

        moving = art.animated && art.state == .printing && !art.dimmed
        if scripted { return }
        updatePlayback()
        needsDisplay = true
    }

    // MARK: - Visibility

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard !scripted else { return }
        if let occlusionObserver { NotificationCenter.default.removeObserver(occlusionObserver) }
        occlusionObserver = window.map { window in
            NotificationCenter.default.addObserver(forName: NSWindow.didChangeOcclusionStateNotification, object: window,
                                                   queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.updatePlayback() }
            }
        }
        updatePlayback()
    }

    /// Animates (at the display's frame rate) only while printing and on screen; otherwise SceneKit draws
    /// a frame when something changes, and nothing at all while hidden.
    private func updatePlayback() {
        let visible = window?.occlusionState.contains(.visible) ?? false
        if visible && !onScreen { needsDisplay = true }
        onScreen = visible
        let animate = moving && visible
        if animate != animating {
            animating = animate
            animate ? startMotion() : stopMotion(easing: visible)
        }
        if isPlaying != animate { isPlaying = animate }
        if rendersContinuously != animate { rendersContinuously = animate }
    }

    private func shownFraction(_ art: PrinterArt) -> Double {
        switch art.state {
        case .finished: return 1
        case .idle, .failed: return min(1, art.progress)
        case .preparing: return art.progress > 0 ? max(0.03, art.progress) : 0
        case .printing, .paused: return max(0.03, min(1, art.progress))
        }
    }

    /// The toolhead traces the print's outline; on bed slingers the bed slides under it.
    private func startMotion() {
        guard let content else { return }
        if let head, let parent = head.node.parent {
            let x = parent.convertVector(SCNVector3(0.028, 0, 0), from: content)
            let z = parent.convertVector(SCNVector3(0, 0, 0.018), from: content)
            let path = SCNAction.sequence([
                .move(to: head.base + x, duration: 0.7), .move(to: head.base + x + z, duration: 0.45),
                .move(to: head.base - x + z, duration: 0.9), .move(to: head.base - x, duration: 0.45),
                .move(to: head.base, duration: 0.5),
            ])
            path.timingMode = .easeInEaseOut
            head.node.runAction(.repeatForever(path), forKey: "print")
        }
        if let bed, let parent = bed.node.parent {
            let z = parent.convertVector(SCNVector3(0, 0, 0.022), from: content)
            let slide = SCNAction.sequence([.move(to: bed.base + z, duration: 1.1), .move(to: bed.base - z, duration: 1.4),
                                            .move(to: bed.base, duration: 0.6)])
            slide.timingMode = .easeInEaseOut
            bed.node.runAction(.repeatForever(slide), forKey: "print")
        }
    }

    /// The model at a scripted moment: `time` places the toolhead (and a bed slinger's bed) along its path,
    /// `turn` adds to the resting angle. Renders the scene itself rather than through the view.
    func image(size: CGSize, scale: CGFloat, time: TimeInterval, turn: CGFloat) -> NSImage? {
        yaw = Self.restYaw + turn
        orient()
        if let content {
            func along(_ tracked: (node: SCNNode, base: SCNVector3)?, _ offset: SCNVector3) {
                guard let tracked, let parent = tracked.node.parent else { return }
                tracked.node.position = tracked.base + (moving ? parent.convertVector(offset, from: content) : SCNVector3Zero)
            }
            along(head, SCNVector3(0.028 * CGFloat(sin(time * 2.4)), 0, 0.018 * CGFloat(sin(time * 1.7))))
            along(bed, SCNVector3(0, 0, 0.022 * CGFloat(sin(time * 1.3))))
        }
        let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice(), options: nil)
        renderer.scene = scene
        renderer.pointOfView = pointOfView
        return renderer.snapshot(atTime: time, with: CGSize(width: size.width * scale, height: size.height * scale),
                                 antialiasingMode: .multisampling4X)
    }

    /// Parks the toolhead (and bed): eased back when the print stops in view, at once when the view hides.
    private func stopMotion(easing: Bool) {
        for tracked in [head, bed].compactMap({ $0 }) {
            tracked.node.removeAction(forKey: "print")
            if easing {
                tracked.node.runAction(.move(to: tracked.base, duration: 0.4))
            } else {
                tracked.node.position = tracked.base
            }
        }
    }

    // MARK: - Turning

    private func orient() {
        pivot.eulerAngles = SCNVector3(pitch, yaw, 0)
        needsDisplay = true
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .openHand)
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 {
            SCNTransaction.begin()
            SCNTransaction.animationDuration = 0.5
            yaw = Self.restYaw
            pitch = Self.restPitch
            orient()
            SCNTransaction.commit()
            return
        }
        lastDrag = convert(event.locationInWindow, from: nil)
        NSCursor.closedHand.push()
    }

    override func mouseDragged(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        guard let last = lastDrag else { return }
        yaw += (point.x - last.x) * 0.012
        pitch = min(0.7, max(-0.15, pitch - (point.y - last.y) * 0.008))
        lastDrag = point
        orient()
    }

    override func mouseUp(with event: NSEvent) {
        if lastDrag != nil { NSCursor.pop() }
        lastDrag = nil
    }
}

private func + (a: SCNVector3, b: SCNVector3) -> SCNVector3 { SCNVector3(a.x + b.x, a.y + b.y, a.z + b.z) }
private func - (a: SCNVector3, b: SCNVector3) -> SCNVector3 { SCNVector3(a.x - b.x, a.y - b.y, a.z - b.z) }
