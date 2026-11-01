import CoreGraphics
import Foundation
import ImageIO

/// The P1/A1 camera on port 6000. After an 80-byte login over TLS the printer streams JPEG frames, each behind
/// a 16-byte header that starts with the payload size (little-endian, three bytes used).
final class JPEGCameraClient: CameraClient {
    var onEvent: ((CameraEvent) -> Void)?
    var log: ((String) -> Void)?

    private let config: CameraConfig
    private let queue = DispatchQueue(label: "dev.rolottr.BambuBar.camera.jpeg")
    private var socket: CameraSocket?
    private var reader = JPEGFrameReader()
    private var frames = 0
    private var lastFrame = Date()
    private var watchdog: DispatchSourceTimer?
    private var finished = false

    init(_ config: CameraConfig) {
        self.config = config
    }

    func start() {
        queue.async { self.open() }
    }

    func stop() {
        queue.async { self.finish(nil) }
    }

    private func open() {
        let socket = CameraSocket(host: config.host, port: CameraTransport.jpeg.port, serial: config.serial,
                                  insecure: config.insecure, queue: queue)
        socket.log = log
        socket.onReady = { [weak self] in self?.login() }
        socket.onData = { [weak self] data in self?.received(data) }
        socket.onClose = { [weak self] failure in
            guard let self else { return }
            // The printer hangs up without a word when it doesn't like the login (or is busy with other viewers).
            if failure == .closed && self.frames == 0 {
                self.finish(.unreachable("The printer closed the camera connection before sending a picture. Check the access code, or close other apps that show this camera."))
            } else {
                self.finish(failure)
            }
        }
        self.socket = socket
        socket.open()
        lastFrame = Date()
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            // Idle P1 printers send about one frame a second; give a slow start a little longer.
            if Date().timeIntervalSince(self.lastFrame) > (self.frames == 0 ? 15 : 12) { self.finish(.timedOut) }
        }
        timer.resume()
        watchdog = timer
    }

    private func login() {
        var packet = Data()
        for word: UInt32 in [0x40, 0x3000, 0, 0] {
            withUnsafeBytes(of: word.littleEndian) { packet.append(contentsOf: $0) }
        }
        for field in ["bblp", config.accessCode] {
            var bytes = Array(field.utf8.prefix(32))
            bytes += [UInt8](repeating: 0, count: 32 - bytes.count)
            packet.append(contentsOf: bytes)
        }
        log?("sent login")
        socket?.send(packet)
    }

    private func received(_ data: Data) {
        guard let jpegs = reader.append(data) else { return finish(.malformed("frame header")) }
        jpegs.forEach(decode)
    }

    private func decode(_ jpeg: Data) {
        guard jpeg.starts(with: [0xFF, 0xD8]),
              let source = CGImageSourceCreateWithData(jpeg as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary)
        else {
            log?("skipped a frame that isn't a JPEG (\(jpeg.count) bytes)")
            return
        }
        frames += 1
        lastFrame = Date()
        if frames == 1 {
            log?("first frame: \(image.width)×\(image.height), \(jpeg.count) bytes")
            deliver(.started("JPEG · \(image.width)×\(image.height)", size: CGSize(width: image.width, height: image.height)))
        }
        deliver(.image(image))
    }

    private func finish(_ failure: CameraFailure?) {
        guard !finished else { return }
        finished = true
        watchdog?.cancel()
        watchdog = nil
        socket?.onClose = nil
        socket?.close()
        socket = nil
        if let failure {
            log?("closed: \(failure)")
            deliver(.failed(failure))
        }
    }

    private func deliver(_ event: CameraEvent) {
        DispatchQueue.main.async { [weak self] in self?.onEvent?(event) }
    }
}
