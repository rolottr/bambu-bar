import CoreGraphics
import CoreMedia
import Foundation
import Network

/// How a printer serves its camera on the LAN.
enum CameraTransport: Equatable {
    /// RTSP over TLS on port 322 with H.264/H.265 video (X1, H2, P2S, X2D). Needs "LAN Only Liveview" on.
    case rtsps
    /// A proprietary TLS stream of JPEG frames on port 6000 (P1, A1).
    case jpeg

    static func forModel(_ modelName: String) -> CameraTransport {
        switch modelName {
        case "P1P", "P1S", "A1", "A1 mini", "A2L": return .jpeg
        default: return .rtsps
        }
    }

    var port: UInt16 { self == .rtsps ? 322 : 6000 }
}

struct CameraConfig {
    var host: String
    var serial: String
    var accessCode: String
    var transport: CameraTransport
    /// Accept any certificate (`--insecure-tls`, the local emulator only).
    var insecure = false
}

enum CameraFailure: Error, Equatable {
    case unauthorized
    case certificate(String)
    case unreachable(String)
    case timedOut
    case closed
    case unsupported(String)
    case malformed(String)

    /// Retrying won't help until something changes (the access code, the printer, the app).
    var isPermanent: Bool {
        switch self {
        case .unauthorized, .certificate, .unsupported: return true
        default: return false
        }
    }

    var message: String {
        switch self {
        case .unauthorized: return "The printer didn't accept the access code for the camera."
        case .certificate(let reason): return "The camera's certificate couldn't be verified: \(reason)"
        case .unreachable(let reason): return reason
        case .timedOut: return "The camera stopped sending video."
        case .closed: return "The printer closed the camera connection."
        case .unsupported(let reason): return reason
        case .malformed(let reason): return "The camera sent something unexpected (\(reason))."
        }
    }
}

enum CameraEvent {
    /// The first frame is on its way: a short description of the stream ("H.264 · 1920×1080") and its picture size.
    case started(String, size: CGSize)
    /// A compressed video frame for `AVSampleBufferDisplayLayer`.
    case video(CMSampleBuffer, keyframe: Bool)
    /// A decoded still (the JPEG stream).
    case image(CGImage)
    case failed(CameraFailure)
}

/// One connection to a printer's camera. Events are delivered on the main queue; `log` is called on the
/// client's own queue (for `--probe-camera`).
protocol CameraClient: AnyObject {
    var onEvent: ((CameraEvent) -> Void)? { get set }
    var log: ((String) -> Void)? { get set }
    func start()
    func stop()
}

func makeCameraClient(_ config: CameraConfig) -> CameraClient {
    switch config.transport {
    case .rtsps: return RTSPCameraClient(config)
    case .jpeg: return JPEGCameraClient(config)
    }
}

/// A TLS connection to one of the printer's camera ports, trusted the same way as MQTT (see `BambuTrust`).
/// Every callback runs on the queue passed in.
final class CameraSocket {
    var onReady: (() -> Void)?
    var onData: ((Data) -> Void)?
    /// Called once, unless the owner closed the socket itself.
    var onClose: ((CameraFailure) -> Void)?
    var log: ((String) -> Void)?

    private let host: String
    private let port: UInt16
    private let serial: String
    private let insecure: Bool
    private let queue: DispatchQueue
    private var connection: NWConnection?
    private var verdict: BambuTrust.Verdict?
    private var closed = false

    init(host: String, port: UInt16, serial: String, insecure: Bool, queue: DispatchQueue) {
        self.host = host
        self.port = port
        self.serial = serial
        self.insecure = insecure
        self.queue = queue
    }

    /// Call on `queue`.
    func open(timeout: TimeInterval = 10) {
        let tls = MQTTClient.tlsOptions(queue: queue) { [weak self] trust in
            guard let self else { return false }
            if self.insecure { return true }
            let verdict = BambuTrust.evaluate(trust, serial: self.serial)
            self.verdict = verdict
            self.log?("certificate on port \(self.port): \(verdict)")
            return !verdict.isRejected
        }
        let tcp = NWProtocolTCP.Options()
        tcp.connectionTimeout = Int(timeout)
        tcp.noDelay = true
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 10
        let conn = NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: port)!,
                                using: NWParameters(tls: tls, tcp: tcp))
        connection = conn
        conn.stateUpdateHandler = { [weak self] state in self?.handle(state) }
        log?("connecting to \(host):\(port)")
        conn.start(queue: queue)
    }

    func send(_ data: Data) {
        guard let connection, !closed else { return }
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            if let error { self?.fail(.unreachable(MQTTClient.describe(error))) }
        })
    }

    /// Sends a last message, then closes without reporting it.
    func close(after last: Data? = nil) {
        guard let connection, !closed else { return }
        closed = true
        connection.stateUpdateHandler = nil
        if let last {
            connection.send(content: last, completion: .contentProcessed { _ in connection.cancel() })
        } else {
            connection.cancel()
        }
        self.connection = nil
    }

    private func handle(_ state: NWConnection.State) {
        switch state {
        case .ready:
            log?("connected to port \(port)")
            onReady?()
            receive()
        case .waiting(let error), .failed(let error):
            if case .rejected(let reason)? = verdict {
                fail(.certificate(reason))
            } else {
                fail(.unreachable(MQTTClient.describe(error)))
            }
        case .cancelled:
            fail(.closed)
        default:
            break
        }
    }

    private func receive() {
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 512 * 1024) { [weak self] data, _, complete, error in
            guard let self, !self.closed else { return }
            if let data, !data.isEmpty { self.onData?(data) }
            guard !self.closed else { return }
            if let error {
                self.fail(.unreachable(MQTTClient.describe(error)))
            } else if complete {
                self.fail(.closed)
            } else {
                self.receive()
            }
        }
    }

    private func fail(_ failure: CameraFailure) {
        guard !closed else { return }
        close()
        onClose?(failure)
    }
}
