import Foundation
import XCTest

/// The Docker simulators in scripts/integration/compose.yaml, which scripts/integration-test.sh starts and
/// describes to the tests through the environment. Without them every test skips.
enum Simulators {
    private static var environment: [String: String] { ProcessInfo.processInfo.environment }

    static func require() throws {
        guard environment["BAMBUBAR_INTEGRATION"] == "1" else {
            throw XCTSkip("Needs the Docker simulators: run scripts/integration-test.sh")
        }
    }

    // MARK: - Fleet (scripts/fleet, SERIAL_CASE=mixed)

    /// The Bambu Studio settings the fleet wrote: a stale code under each uppercase serial, the working code and
    /// the scrambled address under the lowercase one.
    static var fleetSettings: URL {
        URL(fileURLWithPath: environment["BAMBUBAR_FLEET_SETTINGS"] ?? "scripts/integration/out/BambuStudio.conf")
    }

    static let fleetSize = 17
    /// Listed in the settings, never answers: a printer that's switched off.
    static let switchedOffPrinter = "01PF00000000017"
    /// Where the fleet sends its announcements (compose.yaml).
    static let announcementPort: UInt16 = 2122

    // MARK: - Camera (scripts/camera)

    static let cameraHost = "127.0.0.1"
    static let cameraCode = "12345678"
    static let cameraSerial = "01P00C000000001"
    private static let control = URL(string: "http://127.0.0.1:18080")!

    /// What the camera does for the next connections (the list is in scripts/camera/camera.py).
    static func setCameraScenario(_ name: String) async throws {
        var request = URLRequest(url: control.appendingPathComponent("scenario/\(name)"))
        request.httpMethod = "PUT"
        let (_, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        try await clearCameraEvents()
    }

    /// What the camera received since the scenario was set: requests, logins, teardowns.
    static func cameraEvents() async throws -> [[String: Any]] {
        let (data, _) = try await URLSession.shared.data(from: control.appendingPathComponent("events"))
        return String(decoding: data, as: UTF8.self).split(separator: "\n").compactMap {
            try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any]
        }
    }

    static func clearCameraEvents() async throws {
        var request = URLRequest(url: control.appendingPathComponent("events"))
        request.httpMethod = "DELETE"
        _ = try await URLSession.shared.data(for: request)
    }
}

/// Values touched only on the main queue, where the clients deliver their events, read by an async test.
final class MainQueueBox<Value>: @unchecked Sendable {
    private var value: Value

    init(_ value: Value) { self.value = value }

    /// On the main queue.
    func update(_ change: (inout Value) -> Void) { change(&value) }

    func read() async -> Value {
        await withCheckedContinuation { continuation in
            DispatchQueue.main.async { continuation.resume(returning: self.value) }
        }
    }
}
