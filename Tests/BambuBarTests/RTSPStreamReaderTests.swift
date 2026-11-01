import XCTest

/// What arrives on the RTSP connection comes from the network: no length in it may crash the app
/// (review finding P1: `Content-Length: -1` trapped on an inverted range).
final class RTSPStreamReaderTests: XCTestCase {
    private func bytes(_ text: String) -> [UInt8] { Array(text.utf8) }

    private func interleaved(channel: UInt8, _ packet: [UInt8]) -> [UInt8] {
        [0x24, channel, UInt8(packet.count >> 8), UInt8(packet.count & 0xFF)] + packet
    }

    private func failure(_ response: String, file: StaticString = #filePath, line: UInt = #line) -> RTSPStreamReader.Failure? {
        var reader = RTSPStreamReader()
        do {
            _ = try reader.append(bytes(response))
            return nil
        } catch let failure as RTSPStreamReader.Failure {
            return failure
        } catch {
            XCTFail("unexpected \(error)", file: file, line: line)
            return nil
        }
    }

    func testResponseWithBody() throws {
        var reader = RTSPStreamReader()
        let items = try reader.append(bytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 5\r\n\r\nv=0\r\n"))
        XCTAssertEqual(items.count, 1)
        guard case .message(let message, let body) = items.first else { return XCTFail("\(items)") }
        XCTAssertEqual(message.status, 200)
        XCTAssertEqual(body, bytes("v=0\r\n"))
    }

    func testResponseWithoutContentLength() throws {
        var reader = RTSPStreamReader()
        let items = try reader.append(bytes("RTSP/1.0 200 OK\r\nCSeq: 3\r\n\r\n"))
        guard case .message(let message, let body) = items.first else { return XCTFail("\(items)") }
        XCTAssertEqual(message.header("CSeq"), "3")
        XCTAssertTrue(body.isEmpty)
    }

    func testMessagesAndPacketsSplitAtEveryByte() throws {
        let packet: [UInt8] = [0x80, 0xE0, 0, 1, 0, 0, 0, 9, 0, 0, 0, 1, 0x65, 0x88]
        let stream = bytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 3\r\n\r\nabc")
            + interleaved(channel: 0, packet) + interleaved(channel: 1, [1, 2, 3])
            + bytes("RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n")
        for boundary in 0...stream.count {
            var reader = RTSPStreamReader()
            let items = try reader.append(stream[..<boundary]) + reader.append(stream[boundary...])
            XCTAssertEqual(items.count, 4, "split at byte \(boundary)")
            guard items.count == 4 else { continue }
            XCTAssertEqual(items[1], .packet(channel: 0, packet), "split at byte \(boundary)")
            XCTAssertEqual(items[2], .packet(channel: 1, [1, 2, 3]), "split at byte \(boundary)")
        }
    }

    func testGarbageBetweenItemsIsSkipped() throws {
        var reader = RTSPStreamReader()
        let items = try reader.append([0x00, 0xFF, 0x0A] + interleaved(channel: 0, [9, 9]) + [0x01])
        XCTAssertEqual(items, [.packet(channel: 0, [9, 9])])
    }

    func testNegativeContentLengthIsAnError() {
        XCTAssertEqual(failure("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: -1\r\n\r\n"), .badContentLength("-1"))
    }

    func testContentLengthThatOverflowsIsAnError() {
        XCTAssertEqual(failure("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 9223372036854775807\r\n\r\n"), .bodyTooLarge(.max))
        XCTAssertEqual(failure("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 99999999999999999999999\r\n\r\n"), .bodyTooLarge(.max))
    }

    func testOversizedBodyIsAnError() {
        XCTAssertEqual(failure("RTSP/1.0 200 OK\r\nContent-Length: 70000\r\n\r\n"), .bodyTooLarge(70000))
    }

    func testContentLengthThatIsNotANumberIsAnError() {
        XCTAssertEqual(failure("RTSP/1.0 200 OK\r\nContent-Length: abc\r\n\r\n"), .badContentLength("abc"))
        XCTAssertEqual(failure("RTSP/1.0 200 OK\r\nContent-Length: +5\r\n\r\n"), .badContentLength("+5"))
        XCTAssertEqual(failure("RTSP/1.0 200 OK\r\nContent-Length: 1 2\r\n\r\n"), .badContentLength("1 2"))
        XCTAssertEqual(failure("RTSP/1.0 200 OK\r\nContent-Length:\r\n\r\n"), .badContentLength(""))
    }

    func testEndlessHeaderIsAnError() {
        var reader = RTSPStreamReader()
        XCTAssertNoThrow(try reader.append(bytes("RTSP/1.0 200 OK\r\n")))
        let filler = bytes("X-Filler: " + String(repeating: "a", count: 1000) + "\r\n")
        var thrown: Error?
        for _ in 0..<80 where thrown == nil {
            do { _ = try reader.append(filler) } catch { thrown = error }
        }
        XCTAssertEqual(thrown as? RTSPStreamReader.Failure, .headerTooLong)
    }

    func testWaitsForTheWholeBody() throws {
        var reader = RTSPStreamReader()
        XCTAssertEqual(try reader.append(bytes("RTSP/1.0 200 OK\r\nContent-Length: 4\r\n\r\nab")), [])
        let items = try reader.append(bytes("cd"))
        guard case .message(_, let body) = items.first else { return XCTFail("\(items)") }
        XCTAssertEqual(body, bytes("abcd"))
    }

    func testFailureDescriptionIsReadable() {
        XCTAssertEqual("\(RTSPStreamReader.Failure.badContentLength("-1"))", "RTSP Content-Length \"-1\"")
        XCTAssertEqual("\(RTSPStreamReader.Failure.bodyTooLarge(70000))", "RTSP body of 70000 bytes")
    }
}
