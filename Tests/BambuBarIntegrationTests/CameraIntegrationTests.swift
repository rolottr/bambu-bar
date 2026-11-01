import XCTest

/// The camera clients over real TLS against the mock camera (scripts/camera/camera.py), one scenario per test:
/// the good streams of both protocols, and what a broken or hostile camera may send. Whatever arrives, the
/// client must end with a failure the app can show, never crash it (review finding P1).
final class CameraIntegrationTests: XCTestCase {
    enum Event: Equatable {
        case started(String)
        case video(keyframe: Bool)
        case image(width: Int, height: Int)
        case failed(CameraFailure)

        var isVideo: Bool { if case .video = self { return true } else { return false } }
        var isFailure: Bool { if case .failed = self { return true } else { return false } }
    }

    override func setUpWithError() throws {
        try Simulators.require()
    }

    /// Streams from the mock camera in `scenario` until `done` says so (or `timeout`), then stops the client.
    private func record(_ scenario: String, transport: CameraTransport, code: String = Simulators.cameraCode,
                        insecure: Bool = true, timeout: TimeInterval = 10,
                        until done: @escaping ([Event]) -> Bool) async throws -> [Event] {
        try await Simulators.setCameraScenario(scenario)
        let events = MainQueueBox<[Event]>([])
        let finished = expectation(description: "camera \(scenario)")
        var fulfilled = false
        let client = makeCameraClient(CameraConfig(host: Simulators.cameraHost, serial: Simulators.cameraSerial,
                                                   accessCode: code, transport: transport, insecure: insecure))
        client.onEvent = { event in
            let recorded: Event
            switch event {
            case .started(let text, _): recorded = .started(text)
            case .video(_, let keyframe): recorded = .video(keyframe: keyframe)
            case .image(let image): recorded = .image(width: image.width, height: image.height)
            case .failed(let failure): recorded = .failed(failure)
            }
            events.update { list in
                list.append(recorded)
                if !fulfilled, done(list) || recorded.isFailure {
                    fulfilled = true
                    finished.fulfill()
                }
            }
        }
        client.start()
        await fulfillment(of: [finished], timeout: timeout)
        client.stop()
        return await events.read()
    }

    private func failure(in events: [Event]) -> CameraFailure? {
        for case .failed(let failure) in events { return failure }
        return nil
    }

    private func requests(_ method: String) async throws -> [[String: Any]] {
        try await Simulators.cameraEvents().filter { $0["method"] as? String == method }
    }

    // MARK: - RTSPS: good streams

    private func assertPlays(_ scenario: String, codec: String, file: StaticString = #filePath, line: UInt = #line) async throws {
        let events = try await record(scenario, transport: .rtsps) { $0.filter(\.isVideo).count >= 12 }
        XCTAssertEqual(events.first, .started(codec), "\(events)", file: file, line: line)
        XCTAssertNil(failure(in: events), "\(events)", file: file, line: line)
        let video = events.filter(\.isVideo)
        XCTAssertGreaterThanOrEqual(video.count, 12, file: file, line: line)
        XCTAssertEqual(video.first, .video(keyframe: true), "a keyframe first", file: file, line: line)
        XCTAssertTrue(video.dropFirst().contains(.video(keyframe: false)), file: file, line: line)
        XCTAssertGreaterThanOrEqual(video.filter { $0 == .video(keyframe: true) }.count, 2, "the next keyframe too",
                                    file: file, line: line)
    }

    func testRTSPBasicAuthH264() async throws {
        try await assertPlays("h264-basic", codec: "H.264 · 32×32")
    }

    func testRTSPDigestAuthH264() async throws {
        try await assertPlays("h264-digest", codec: "H.264 · 32×32")
    }

    func testRTSPDigestAuthH265() async throws {
        try await assertPlays("h265", codec: "H.265 · 64×64")
    }

    /// Responses and packets cut into three-byte writes, so every boundary falls somewhere unexpected.
    func testRTSPStreamInTinyPieces() async throws {
        try await assertPlays("trickle", codec: "H.264 · 32×32")
    }

    /// Stopping tells the printer, which serves only a few viewers at a time.
    func testStoppingSendsTeardown() async throws {
        _ = try await record("h264-digest", transport: .rtsps) { $0.filter(\.isVideo).count >= 2 }
        var teardowns: [[String: Any]] = []
        for _ in 0..<20 where teardowns.isEmpty {
            try await Task.sleep(for: .milliseconds(100))
            teardowns = try await requests("TEARDOWN")
        }
        XCTAssertEqual(teardowns.count, 1)
    }

    // MARK: - RTSPS: refused and broken

    /// Credentials refused twice (the second time against a fresh challenge, in case the first nonce was stale)
    /// mean the code is wrong: no more tries.
    func testRTSPWrongCodeIsUnauthorizedWithoutRetryingForever() async throws {
        let events = try await record("h264-digest", transport: .rtsps, code: "WRONG000") { _ in false }
        XCTAssertEqual(failure(in: events), .unauthorized)
        try await Task.sleep(for: .milliseconds(300))
        let describes = try await requests("DESCRIBE")
        XCTAssertEqual(describes.map { $0["authorization"] as? Bool }, [false, true, true])
    }

    /// Review finding P1: this response crashed the app (an inverted range in the parser).
    func testNegativeContentLengthFailsWithoutCrashing() async throws {
        let events = try await record("negative-length", transport: .rtsps) { _ in false }
        guard case .malformed(let reason)? = failure(in: events) else { return XCTFail("\(events)") }
        XCTAssertTrue(reason.contains("Content-Length"), reason)
    }

    /// Would overflow the body's end offset.
    func testHugeContentLengthFailsWithoutCrashing() async throws {
        let events = try await record("huge-length", transport: .rtsps) { _ in false }
        guard case .malformed? = failure(in: events) else { return XCTFail("\(events)") }
    }

    /// Review finding P3: VP8 was handed to the H.264 depacketizer.
    func testUnsupportedCodecIsReported() async throws {
        let events = try await record("vp8", transport: .rtsps) { _ in false }
        guard case .unsupported? = failure(in: events) else { return XCTFail("\(events)") }
        let setups = try await requests("SETUP")
        XCTAssertTrue(setups.isEmpty, "no stream is set up for a codec that can't play")
    }

    /// Review finding P2: a unit's first fragment and its last carry different timestamps; they were glued into a
    /// bogus keyframe. Only the real keyframe and the delta frame after it may play.
    func testFragmentsFromDifferentFramesAreNotJoined() async throws {
        let events = try await record("split-fragment", transport: .rtsps) { $0.filter(\.isVideo).count >= 2 }
        try await Task.sleep(for: .seconds(1))  // anything else the camera sent has arrived by now
        XCTAssertNil(failure(in: events), "\(events)")
        XCTAssertEqual(events.filter(\.isVideo), [.video(keyframe: true), .video(keyframe: false)])
    }

    func testCameraThatIsNotStreamingIsUnreachable() async throws {
        let events = try await record("not-streaming", transport: .rtsps) { _ in false }
        guard case .unreachable(let reason)? = failure(in: events) else { return XCTFail("\(events)") }
        XCTAssertTrue(reason.contains("503"), reason)
    }

    /// The mock camera's certificate is self-signed: without `insecure` it must be refused before anything is sent.
    func testUntrustedCertificateIsRefused() async throws {
        let events = try await record("h264-digest", transport: .rtsps, insecure: false) { _ in false }
        guard case .certificate? = failure(in: events) else { return XCTFail("\(events)") }
        let describes = try await requests("DESCRIBE")
        XCTAssertTrue(describes.isEmpty, "the access code never left")
    }

    /// A connection to the same camera a moment ago must not let the next one skip the certificate check (a resumed
    /// TLS session has no certificate to check): the certificate is the only proof the camera is the expected printer.
    func testCertificateIsCheckedOnEveryConnection() async throws {
        let insecure = try await record("h264-digest", transport: .rtsps) { $0.filter(\.isVideo).count >= 2 }
        XCTAssertNil(failure(in: insecure), "\(insecure)")
        let secure = try await record("h264-digest", transport: .rtsps, insecure: false) { _ in false }
        guard case .certificate? = failure(in: secure) else { return XCTFail("\(secure)") }
    }

    /// The handshake gets no answer: the client gives up after 20 seconds instead of waiting forever.
    func testStalledCameraTimesOut() async throws {
        let events = try await record("stall", transport: .rtsps, timeout: 30) { _ in false }
        XCTAssertEqual(failure(in: events), .timedOut)
    }

    // MARK: - JPEG stream

    func testJPEGStream() async throws {
        let events = try await record("jpeg", transport: .jpeg) { $0.count >= 4 }
        XCTAssertEqual(events.first, .started("JPEG · 64×48"))
        XCTAssertEqual(Array(events.dropFirst()), Array(repeating: .image(width: 64, height: 48), count: events.count - 1))
    }

    func testJPEGStreamInSmallPieces() async throws {
        let events = try await record("jpeg-trickle", transport: .jpeg) { $0.count >= 4 }
        XCTAssertEqual(events.first, .started("JPEG · 64×48"))
        XCTAssertNil(failure(in: events), "\(events)")
    }

    /// The printer hangs up on a wrong code without a word.
    func testJPEGWrongCodeIsReported() async throws {
        let events = try await record("jpeg", transport: .jpeg, code: "WRONG000") { _ in false }
        guard case .unreachable(let reason)? = failure(in: events) else { return XCTFail("\(events)") }
        XCTAssertTrue(reason.contains("access code"), reason)
    }

    func testJPEGFrameOfNoBytesIsMalformed() async throws {
        let events = try await record("jpeg-zero", transport: .jpeg) { _ in false }
        XCTAssertEqual(failure(in: events), .malformed("frame header"))
    }
}
