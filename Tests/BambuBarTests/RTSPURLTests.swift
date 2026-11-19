import XCTest

/// The RTSP URL someone types in for an external camera: what the app accepts, what it rejects, and the URL
/// it puts on the request line. Mirrored in windows/tests/BambuBar.Core.Tests/Camera/CameraProtocolTests.cs.
final class RTSPURLTests: XCTestCase {
    private func parsed(_ text: String) throws -> RTSPURL {
        try XCTUnwrap(try? RTSPURL.parse(text).get(), "did not parse: \(text)")
    }

    func testPlainDefaultPort() throws {
        let url = try parsed("rtsp://192.168.1.40/stream1")
        XCTAssertEqual(url.isTLS, false)
        XCTAssertEqual(url.port, 554)
        XCTAssertEqual(url.path, "/stream1")
        XCTAssertEqual(url.absolute, "rtsp://192.168.1.40:554/stream1")
    }

    func testTLSDefaultPort() throws {
        XCTAssertEqual(try parsed("rtsps://cam.local/live").port, 322)
        XCTAssertEqual(try parsed("rtsps://cam.local/live").isTLS, true)
    }

    func testExplicitPortAndQuery() throws {
        let url = try parsed("rtsp://cam:8554/h264?channel=1&subtype=0")
        XCTAssertEqual(url.port, 8554)
        XCTAssertEqual(url.path, "/h264?channel=1&subtype=0")
    }

    func testCredentialsArePercentDecodedAndKeptOutOfAbsolute() throws {
        let url = try parsed("rtsp://admin:p%40ss%3Aword@cam:554/s")
        XCTAssertEqual(url.username, "admin")
        XCTAssertEqual(url.password, "p@ss:word")
        XCTAssertEqual(url.absolute, "rtsp://cam:554/s")
    }

    func testEmptyPathBecomesSlash() throws {
        XCTAssertEqual(try parsed("rtsp://cam").path, "/")
        XCTAssertEqual(try parsed("  rtsp://cam/  ").path, "/")
    }

    func testIPv6HostInBrackets() throws {
        let url = try parsed("rtsp://[fd00::1]:8554/s")
        XCTAssertEqual(url.host, "fd00::1")
        XCTAssertEqual(url.port, 8554)
        XCTAssertEqual(url.absolute, "rtsp://[fd00::1]:8554/s")
    }

    func testRejections() {
        XCTAssertEqual(RTSPURL.parse("http://cam/s"), .failure(.scheme("http")))
        XCTAssertEqual(RTSPURL.parse("rtsp:///s"), .failure(.noHost))
        XCTAssertEqual(RTSPURL.parse("rtsp://cam:0/s"), .failure(.port("0")))
        XCTAssertEqual(RTSPURL.parse("rtsp://cam:70000/s"), .failure(.port("70000")))
        XCTAssertEqual(RTSPURL.parse("rtsp://cam:abc/s"), .failure(.port("abc")))
        XCTAssertEqual(RTSPURL.parse(""), .failure(.malformed))
    }
}
