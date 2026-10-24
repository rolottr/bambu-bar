import XCTest

final class SDPTests: XCTestCase {
    func testH264TrackWithParameterSets() throws {
        let sdp = [
            "v=0", "o=- 1 1 IN IP4 0.0.0.0", "s=Live", "t=0 0", "a=control:*",
            "m=audio 0 RTP/AVP 97", "a=rtpmap:97 MPEG4-GENERIC/16000", "a=control:track0",
            "m=video 0 RTP/AVP 96", "c=IN IP4 0.0.0.0", "a=rtpmap:96 H264/90000",
            "a=fmtp:96 packetization-mode=1;profile-level-id=42C00A;sprop-parameter-sets=Z0LACtkJbARAAAADAEAAAAMBA8SJkg==,aMuDyyA=",
            "a=control:track1",
        ].joined(separator: "\r\n")
        let video = try XCTUnwrap(SDP.video(in: sdp))
        XCTAssertEqual(video.codec, .h264)
        XCTAssertEqual(video.payloadType, 96)
        XCTAssertEqual(video.control, "track1")
        XCTAssertEqual(video.parameterSets, [Fixtures.h264SPS, Fixtures.h264PPS])
    }

    func testH265TrackWithParameterSets() throws {
        let sdp = """
        v=0
        m=video 0 RTP/AVP 98
        a=rtpmap:98 H265/90000
        a=fmtp:98 sprop-vps=QAEMAf//AWAAAAMAkAAAAwAAAwAekoCQ; sprop-sps=QgEBAWAAAAMAkAAAAwAAAwAeoCCBBZZKkkyvAWgIAAADAAgAAAMAEEA; sprop-pps=RAHBcrQiQA==
        """
        let video = try XCTUnwrap(SDP.video(in: sdp))
        XCTAssertEqual(video.codec, .h265)
        XCTAssertEqual(video.payloadType, 98)
        XCTAssertNil(video.control)
        // Unpadded base64 is accepted too.
        XCTAssertEqual(video.parameterSets, [Fixtures.h265VPS, Fixtures.h265SPS, Fixtures.h265PPS])
    }

    func testSessionControlWhenTheTrackHasNone() throws {
        let sdp = "v=0\na=control:rtsps://printer/streaming/live/1\nm=video 0 RTP/AVP 96\na=rtpmap:96 H264/90000\n"
        XCTAssertEqual(try XCTUnwrap(SDP.video(in: sdp)).control, "rtsps://printer/streaming/live/1")
    }

    func testNoVideo() {
        XCTAssertNil(SDP.video(in: "v=0\nm=audio 0 RTP/AVP 97\na=rtpmap:97 PCMU/8000\n"))
    }

    func testResolve() {
        let base = "rtsps://192.168.0.5:322/streaming/live/1"
        XCTAssertEqual(SDP.resolve("track1", base: base), base + "/track1")
        XCTAssertEqual(SDP.resolve("track1", base: base + "/"), base + "/track1")
        XCTAssertEqual(SDP.resolve("rtsps://other/track2", base: base), "rtsps://other/track2")
        XCTAssertEqual(SDP.resolve("*", base: base), base)
        XCTAssertEqual(SDP.resolve(nil, base: base), base)
    }
}

final class RTSPMessageTests: XCTestCase {
    func testResponse() {
        let message = RTSPMessage([
            "RTSP/1.0 401 Unauthorized", "CSeq: 2",
            "WWW-Authenticate: Digest realm=\"x\", nonce=\"y\"", "www-authenticate: Basic realm=\"x\"",
            "Session: 1234ABCD;timeout=60",
        ].joined(separator: "\r\n"))
        XCTAssertTrue(message.isResponse)
        XCTAssertEqual(message.status, 401)
        XCTAssertEqual(message.statusLine, "RTSP/1.0 401 Unauthorized")
        XCTAssertEqual(message.header("cseq"), "2")
        XCTAssertEqual(message.header("Session"), "1234ABCD;timeout=60")
        XCTAssertEqual(message.headers("WWW-Authenticate").count, 2)
        XCTAssertNil(message.header("Content-Length"))
    }

    func testRequestFromTheServer() {
        let message = RTSPMessage("OPTIONS * RTSP/1.0\r\nCSeq: 7")
        XCTAssertFalse(message.isResponse)
        XCTAssertEqual(message.status, 0)
    }
}

final class RTSPAuthTests: XCTestCase {
    /// The worked example in RFC 2617, section 3.5.
    func testDigestWithQopMatchesRFC2617() throws {
        let auth = try XCTUnwrap(RTSPAuth(
            challenges: ["Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\""],
            username: "Mufasa", password: "Circle Of Life"))
        auth.makeCnonce = { "0a4f113b" }
        let header = auth.header(method: "GET", uri: "/dir/index.html")
        XCTAssertTrue(header.hasPrefix("Digest "))
        XCTAssertTrue(header.contains("response=\"6629fae49393a05397450978507c4ef1\""), header)
        XCTAssertTrue(header.contains("qop=auth"))
        XCTAssertTrue(header.contains("nc=00000001"))
        XCTAssertTrue(header.contains("cnonce=\"0a4f113b\""))
        XCTAssertTrue(header.contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\""))
        XCTAssertTrue(auth.header(method: "GET", uri: "/dir/index.html").contains("nc=00000002"))
    }

    func testDigestWithoutQop() throws {
        let auth = try XCTUnwrap(RTSPAuth(challenges: ["Basic realm=\"Bambu\"", "Digest realm=\"Bambu\", nonce=\"abc123\""],
                                          username: "bblp", password: "12345678"))
        let header = auth.header(method: "DESCRIBE", uri: "rtsps://192.168.0.5:322/streaming/live/1")
        XCTAssertTrue(header.hasPrefix("Digest "), "Digest is preferred over Basic")
        XCTAssertTrue(header.contains("response=\"2c512312cf09c400ef91e4c0e78c36ad\""), header)
        XCTAssertFalse(header.contains("qop="))
    }

    func testBasic() throws {
        let auth = try XCTUnwrap(RTSPAuth(challenges: ["Basic realm=\"Bambu\""], username: "bblp", password: "12345678"))
        XCTAssertEqual(auth.header(method: "DESCRIBE", uri: "rtsps://x"), "Basic YmJscDoxMjM0NTY3OA==")
    }

    func testUnknownOrIncompleteChallenge() {
        XCTAssertNil(RTSPAuth(challenges: ["Bearer realm=\"x\""], username: "bblp", password: "1"))
        XCTAssertNil(RTSPAuth(challenges: ["Digest realm=\"x\""], username: "bblp", password: "1"))
        XCTAssertNil(RTSPAuth(challenges: [], username: "bblp", password: "1"))
    }

    func testParametersKeepCommasInQuotes() {
        XCTAssertEqual(RTSPAuth.parameters(" realm=\"a, b\", nonce=xyz, stale=FALSE"),
                       ["realm": "a, b", "nonce": "xyz", "stale": "FALSE"])
    }
}

final class JPEGFrameReaderTests: XCTestCase {
    private func frame(_ payload: [UInt8]) -> [UInt8] {
        let size = payload.count
        return [UInt8(size & 0xFF), UInt8(size >> 8 & 0xFF), UInt8(size >> 16 & 0xFF), 0]
            + [UInt8](repeating: 0, count: 12) + payload
    }

    func testFramesInOneChunk() {
        var reader = JPEGFrameReader()
        let bytes = frame([0xFF, 0xD8, 1, 2]) + frame([0xFF, 0xD8, 3])
        XCTAssertEqual(reader.append(Data(bytes)), [Data([0xFF, 0xD8, 1, 2]), Data([0xFF, 0xD8, 3])])
    }

    func testFrameSplitAcrossChunks() {
        var reader = JPEGFrameReader()
        let payload = [UInt8](repeating: 0xAB, count: 300)
        let bytes = frame(payload)
        XCTAssertEqual(reader.append(Data(bytes[..<10])), [], "half a header")
        XCTAssertEqual(reader.append(Data(bytes[10..<200])), [])
        XCTAssertEqual(reader.append(Data(bytes[200...] + frame([7]).prefix(5))), [Data(payload)])
        XCTAssertEqual(reader.append(Data(frame([7]).dropFirst(5))), [Data([7])])
    }

    func testEmptyFrameIsMalformed() {
        var reader = JPEGFrameReader()
        XCTAssertNil(reader.append(Data([UInt8](repeating: 0, count: 16))))
    }
}
