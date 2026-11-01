import AVFoundation
import AppKit
import SwiftUI

// MARK: - Surface

/// Draws a camera feed: H.264/H.265 through `AVSampleBufferDisplayLayer`, JPEG stills as layer contents.
/// It tells its feed when it comes and goes on screen, so a closed window or panel stops the stream.
final class CameraSurfaceView: NSView {
    weak var feed: CameraFeed?
    /// Missed frames while off screen; the feed catches it up when it's back.
    var isBehind = false
    private let videoLayer = AVSampleBufferDisplayLayer()
    private let imageLayer = CALayer()
    private var needsKeyframe = true
    private var occlusionObserver: NSObjectProtocol?

    override init(frame: NSRect) {
        super.init(frame: frame)
        // Layer-hosting: the view owns a plain layer and draws nothing itself.
        layer = CALayer()
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.masksToBounds = true
        videoLayer.videoGravity = .resizeAspect
        imageLayer.contentsGravity = .resizeAspect
        for sublayer in [videoLayer, imageLayer] {
            sublayer.frame = bounds
            sublayer.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
            layer?.addSublayer(sublayer)
        }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.frame = bounds
        imageLayer.frame = bounds
        CATransaction.commit()
    }

    var cornerRadius: CGFloat {
        get { layer?.cornerRadius ?? 0 }
        set { layer?.cornerRadius = newValue; layer?.cornerCurve = .continuous }
    }

    /// In a visible window and not hidden.
    var isOnScreen: Bool {
        guard let window, window.isVisible, window.occlusionState.contains(.visible) else { return false }
        return !isHiddenOrHasHiddenAncestor
    }

    func show(_ sample: CMSampleBuffer, keyframe: Bool) {
        let renderer = videoLayer.sampleBufferRenderer
        if renderer.status == .failed || renderer.requiresFlushToResumeDecoding {
            renderer.flush()
            needsKeyframe = true
        }
        if needsKeyframe {
            guard keyframe else { return }
            needsKeyframe = false
        }
        if imageLayer.contents != nil { imageLayer.contents = nil }
        renderer.enqueue(sample)
    }

    /// Starts decoding afresh from `samples`, which begin with a keyframe. Keeps the picture shown until then.
    func restart(from samples: [CMSampleBuffer]) {
        videoLayer.sampleBufferRenderer.flush()
        needsKeyframe = true
        for (index, sample) in samples.enumerated() { show(sample, keyframe: index == 0) }
    }

    func show(_ image: CGImage) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        imageLayer.contents = image
        CATransaction.commit()
    }

    func clear() {
        videoLayer.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
        needsKeyframe = true
        imageLayer.contents = nil
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let occlusionObserver { NotificationCenter.default.removeObserver(occlusionObserver) }
        occlusionObserver = nil
        if let window {
            occlusionObserver = NotificationCenter.default.addObserver(
                forName: NSWindow.didChangeOcclusionStateNotification, object: window, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated { self?.feed?.viewersChanged() }
            }
        }
        feed?.viewersChanged()
    }

    override func viewDidHide() {
        super.viewDidHide()
        feed?.viewersChanged()
    }

    override func viewDidUnhide() {
        super.viewDidUnhide()
        feed?.viewersChanged()
    }

    deinit {
        if let occlusionObserver { NotificationCenter.default.removeObserver(occlusionObserver) }
    }
}

struct CameraSurface: NSViewRepresentable {
    var feed: CameraFeed
    var cornerRadius: CGFloat

    func makeNSView(context: Context) -> CameraSurfaceView {
        let view = CameraSurfaceView(frame: .zero)
        view.cornerRadius = cornerRadius
        feed.attach(view)
        return view
    }

    func updateNSView(_ view: CameraSurfaceView, context: Context) {
        view.cornerRadius = cornerRadius
        if view.feed !== feed {
            view.feed?.detach(view)
            view.clear()
            feed.attach(view)
        }
    }

    static func dismantleNSView(_ view: CameraSurfaceView, coordinator: ()) {
        view.feed?.detach(view)
    }
}

// MARK: - Player

/// The feed with its state on top: a spinner while connecting, or why it isn't showing.
struct CameraPlayer: View {
    var session: PrinterSession
    var cornerRadius: CGFloat = 16
    /// Smaller type for the menu bar panel.
    var compact = false

    var body: some View {
        let feed = session.camera!
        ZStack {
            CameraSurface(feed: feed, cornerRadius: cornerRadius)
            stateOverlay(feed)
        }
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            .strokeBorder(Color.primary.opacity(0.10), lineWidth: 0.75))
        .onChange(of: session.isOnline) { feed.update() }
        .onChange(of: session.status?.liveViewEnabled) { feed.update() }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(session.config.name) camera")
    }

    @ViewBuilder
    private func stateOverlay(_ feed: CameraFeed) -> some View {
        switch feed.state {
        case .live:
            EmptyView()
        case .idle, .connecting:
            message(symbol: nil, text: "Connecting to the camera…", busy: true)
        case .unavailable(let reason):
            message(symbol: "video.slash", text: reason)
        case .failed(let reason, let retrying):
            message(symbol: "exclamationmark.triangle", text: reason, retry: retrying ? nil : { feed.tryAgain() },
                    busy: retrying)
        }
    }

    private func message(symbol: String?, text: String, retry: (() -> Void)? = nil, busy: Bool = false) -> some View {
        ZStack {
            Color.black.opacity(0.55)
            VStack(spacing: compact ? 6 : 9) {
                if busy {
                    ProgressView().controlSize(.small).environment(\.colorScheme, .dark)
                } else if let symbol {
                    Image(systemName: symbol).font(.system(size: compact ? 16 : 20, weight: .medium))
                }
                Text(text)
                    .font(.system(size: compact ? 11 : 12, weight: .medium))
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                if let retry {
                    Button("Try Again", action: retry).controlSize(.small)
                }
            }
            .foregroundStyle(.white.opacity(0.9))
            .padding(compact ? 12 : 20)
            .frame(maxWidth: 420)
        }
    }
}

// MARK: - Placements

/// Full width of whatever holds it, as tall as the picture's aspect ratio makes it. `aspectRatio(_:contentMode:)`
/// sizes an AppKit-backed view from its (tiny) ideal size when the height is open, as in the menu bar panel.
struct CameraFrame<Content: View>: View {
    var ratio: CGFloat
    @ViewBuilder var content: Content

    var body: some View {
        WidthDrivenLayout(ratio: ratio) { content }
    }
}

private struct WidthDrivenLayout: Layout {
    var ratio: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width.flatMap { $0.isFinite && $0 > 0 ? $0 : nil } ?? 480
        return CGSize(width: width, height: (width / max(ratio, 0.1)).rounded())
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for subview in subviews { subview.place(at: bounds.origin, proposal: ProposedViewSize(bounds.size)) }
    }
}

/// The camera on the Printer page: a heading with the stream details, hide/show and pop-out buttons.
struct CameraCard: View {
    var session: PrinterSession
    @AppStorage("cameraOnPrinterPage") private var shown = true

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                SectionLabel("Camera", trailing: shown ? session.camera.detail : nil)
                Button { CameraWindow.show(session) } label: { Image(systemName: "pip.enter") }
                    .buttonStyle(.borderless)
                    .help("Open the camera in its own window")
                Button(shown ? "Hide" : "Show") { withAnimation(.smooth(duration: 0.25)) { shown.toggle() } }
                    .buttonStyle(.link)
                    .font(.system(size: 11))
            }
            if shown {
                CameraFrame(ratio: session.camera.aspectRatio) {
                    CameraPlayer(session: session, cornerRadius: 18)
                }
                .transition(.opacity)
            }
        }
    }
}

/// The small preview in the menu bar panel. Collapsed, it's one line and nothing streams.
struct CameraPreview: View {
    var session: PrinterSession
    @AppStorage("cameraInMenuBar") private var expanded = true

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Button { withAnimation(.smooth(duration: 0.25)) { expanded.toggle() } } label: {
                    // Symbols in the label's own font, on its baseline, sit centred on the text; a smaller
                    // font centred on the text's frame (which includes descender room) looks low.
                    HStack(alignment: .firstTextBaseline, spacing: 5) {
                        Image(systemName: "chevron.right")
                            .font(.system(size: 11.5, weight: .bold))
                            .imageScale(.small)
                            .frame(width: 10)
                            .rotationEffect(.degrees(expanded ? 90 : 0))
                        Image(systemName: "video.fill")
                            .font(.system(size: 11.5))
                            .imageScale(.small)
                        Text("Camera").font(.system(size: 11.5, weight: .semibold))
                        Spacer(minLength: 0)
                    }
                    .foregroundStyle(.secondary)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(expanded ? "Hide the camera" : "Show the camera")
                Button { CameraWindow.show(session) } label: { Image(systemName: "pip.enter") }
                    .buttonStyle(.borderless)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .help("Open the camera in its own window")
            }
            .padding(.horizontal, 4)
            if expanded {
                CameraFrame(ratio: session.camera.aspectRatio) {
                    CameraPlayer(session: session, cornerRadius: 14, compact: true)
                }
                .transition(.opacity.combined(with: .move(edge: .top)))
            }
        }
    }
}

// MARK: - Window

/// A resizable window per printer with just the camera, kept above other windows like picture in picture.
@MainActor
enum CameraWindow {
    private static var windows: [String: NSWindow] = [:]
    private static var observers: [String: NSObjectProtocol] = [:]

    static func show(_ session: PrinterSession) {
        if let window = windows[session.id] {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 800, height: 450),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered, defer: false)
        window.title = "\(session.config.name) Camera"
        window.titlebarAppearsTransparent = true
        // The view draws the name itself, in white over a shade; the system title vanishes over a bright picture.
        window.titleVisibility = .hidden
        window.isReleasedWhenClosed = false
        // A picture-in-picture window: it stays above other windows while you work elsewhere.
        window.level = .floating
        window.backgroundColor = .black
        window.contentMinSize = NSSize(width: 320, height: 180)
        window.contentAspectRatio = NSSize(width: session.camera.aspectRatio, height: 1)
        window.contentViewController = NSHostingController(
            rootView: CameraWindowView(session: session)
                .environment(PrinterStore.shared))
        window.setContentSize(NSSize(width: 800, height: (800 / session.camera.aspectRatio).rounded()))
        window.center()
        window.setFrameAutosaveName("BambuBarCamera-\(session.id)")
        let serial = session.id
        windows[serial] = window
        observers[serial] = NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification, object: window, queue: .main
        ) { note in
            MainActor.assumeIsolated { CameraWindow.closed(note.object as? NSWindow, serial: serial) }
        }
        NSApp.setActivationPolicy(.regular)
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Drops the window's view so its surface detaches and the stream can stop.
    private static func closed(_ window: NSWindow?, serial: String) {
        window?.contentViewController = nil
        windows[serial] = nil
        if let observer = observers.removeValue(forKey: serial) { NotificationCenter.default.removeObserver(observer) }
    }

    /// Matches the window's shape to the picture once the stream reports its size.
    static func setAspectRatio(_ ratio: CGFloat, for serial: String) {
        guard let window = windows[serial], let content = window.contentView else { return }
        window.contentAspectRatio = NSSize(width: ratio, height: 1)
        let width = content.frame.width
        window.setContentSize(NSSize(width: width, height: (width / ratio).rounded()))
    }

}

struct CameraWindowView: View {
    var session: PrinterSession
    @State private var hovering = false

    var body: some View {
        ZStack(alignment: .bottom) {
            CameraPlayer(session: session, cornerRadius: 0)
            titleBar
            if hovering {
                controls
                    .padding(12)
                    .transition(.opacity)
            }
        }
        .ignoresSafeArea()
        .background(.black)
        .onHover { inside in withAnimation(.smooth(duration: 0.2)) { hovering = inside } }
        .onChange(of: session.camera.aspectRatio) { _, ratio in CameraWindow.setAspectRatio(ratio, for: session.id) }
    }

    /// The printer's name centred in the title bar, over a shade so it reads on any picture.
    private var titleBar: some View {
        VStack(spacing: 0) {
            ZStack {
                LinearGradient(colors: [.black.opacity(0.6), .black.opacity(0)], startPoint: .top, endPoint: .bottom)
                    .frame(height: 52)
                    .frame(maxHeight: .infinity, alignment: .top)
                Text(session.config.name)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
                    .shadow(color: .black.opacity(0.5), radius: 2)
                    .lineLimit(1)
                    .padding(.horizontal, 90)
                    .frame(height: 28)
                    .frame(maxHeight: .infinity, alignment: .top)
            }
            .frame(height: 52)
            Spacer(minLength: 0)
        }
        .allowsHitTesting(false)
    }

    private var controls: some View {
        HStack(spacing: 10) {
            if let status = session.status, session.isOnline {
                Text(status.state.isActive ? "\(status.state.title) · \(status.progress)% · \(Format.duration(minutes: status.remainingMinutes)) left" : status.stateTitle)
                    .font(.system(size: 12, weight: .medium))
                    .monospacedDigit()
            }
            Spacer()
            if let detail = session.camera.detail {
                Text(detail).font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Capsule().fill(.black.opacity(0.5)))
        .environment(\.colorScheme, .dark)
    }
}
