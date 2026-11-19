import CryptoKit
import Foundation

// The camera protocols' parsing, apart from the network code so the unit tests can compile it on its own.

// MARK: - Camera sources

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

/// A camera someone entered on the Printers page, with the credentials it needs.
struct ExternalCamera: Equatable {
    var url: RTSPURL
    var username: String
    var password: String
}

/// Where a camera stream comes from.
enum CameraSource: Equatable {
    /// The printer's own camera, served the way its model serves it.
    case printer(CameraTransport)
    /// A camera someone entered themselves, which replaces the printer's own.
    case external(ExternalCamera)

    var isExternal: Bool {
        if case .external = self { return true }
        return false
    }
}

enum CameraFailure: Error, Equatable {
    /// The printer refused the access code.
    case unauthorized
    /// An external camera refused the username and password.
    case credentialsRejected
    case unreachable(String)
    case timedOut
    case closed
    case unsupported(String)
    case malformed(String)

    /// Retrying won't help until something changes (the access code, the camera's credentials, the printer,
    /// the app).
    var isPermanent: Bool {
        switch self {
        case .unauthorized, .credentialsRejected, .unsupported: return true
        default: return false
        }
    }

    /// What to tell the person. Some of these name the printer, which would be wrong for a camera they
    /// pointed the app at themselves.
    func message(from source: CameraSource) -> String {
        switch self {
        case .unauthorized:
            return String(localized: "The printer didn't accept the access code for the camera.")
        case .credentialsRejected:
            return String(localized: "The camera refused the username and password.")
        case .unreachable(let reason):
            return reason
        case .timedOut:
            return String(localized: "The camera stopped sending video.")
        case .closed:
            return source.isExternal
                ? String(localized: "The camera closed the connection.")
                : String(localized: "The printer closed the camera connection.")
        case .unsupported(let reason):
            return reason
        case .malformed(let reason):
            return String(localized: "The camera sent something unexpected (\(reason)).",
                          comment: "A technical detail, in English")
        }
    }
}


// MARK: - RTSP messages

struct RTSPMessage: Equatable {
    static func == (a: RTSPMessage, b: RTSPMessage) -> Bool {
        a.statusLine == b.statusLine && a.fields.elementsEqual(b.fields) { $0 == $1 }
    }

    let statusLine: String
    let status: Int
    let isResponse: Bool
    private let fields: [(name: String, value: String)]

    init(_ head: String) {
        let lines = head.components(separatedBy: "\r\n")
        statusLine = lines.first ?? ""
        isResponse = statusLine.hasPrefix("RTSP/")
        status = isResponse ? Int(statusLine.split(separator: " ").dropFirst().first ?? "") ?? 0 : 0
        fields = lines.dropFirst().compactMap { line in
            guard let colon = line.firstIndex(of: ":") else { return nil }
            return (String(line[..<colon]).trimmingCharacters(in: .whitespaces),
                    String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces))
        }
    }

    func header(_ name: String) -> String? {
        fields.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    func headers(_ name: String) -> [String] {
        fields.filter { $0.name.caseInsensitiveCompare(name) == .orderedSame }.map(\.value)
    }
}

/// Splits what arrives on the RTSP connection into responses (or requests) and interleaved RTP/RTCP packets
/// (`$`, channel, 16-bit length, packet). Everything here comes from the network, so every length is checked
/// before it's used: a bad one is reported as an error instead of being trusted.
struct RTSPStreamReader {
    enum Item: Equatable {
        case message(RTSPMessage, body: [UInt8])
        case packet(channel: UInt8, [UInt8])
    }

    enum Failure: Error, Equatable, CustomStringConvertible {
        case headerTooLong
        case badContentLength(String)
        case bodyTooLarge(Int)

        var description: String {
            switch self {
            case .headerTooLong: return "RTSP header too long"
            case .badContentLength(let value): return "RTSP Content-Length \"\(value.prefix(32))\""
            case .bodyTooLarge(let size): return size == .max ? "RTSP body too large" : "RTSP body of \(size) bytes"
            }
        }
    }

    /// The most a message's header may take before its blank line.
    static let maxHeader = 64 * 1024
    /// The largest body accepted. Cameras send bodies only with DESCRIBE (an SDP, a few hundred bytes).
    static let maxBody = 64 * 1024

    private var buffer: [UInt8] = []

    /// Adds received bytes and returns the items they completed. After a failure, the stream can't be trusted
    /// any more and the reader shouldn't be used again.
    mutating func append(_ data: some Sequence<UInt8>) throws -> [Item] {
        buffer.append(contentsOf: data)
        var items: [Item] = []
        var offset = 0
        defer { if offset > 0 { buffer.removeFirst(min(offset, buffer.count)) } }
        while offset < buffer.count {
            if buffer[offset] == 0x24 {  // '$': an interleaved RTP or RTCP packet
                guard buffer.count - offset >= 4 else { break }
                let channel = buffer[offset + 1]
                let length = Int(buffer[offset + 2]) << 8 | Int(buffer[offset + 3])
                guard buffer.count - offset >= 4 + length else { break }
                items.append(.packet(channel: channel, Array(buffer[(offset + 4)..<(offset + 4 + length)])))
                offset += 4 + length
            } else if buffer[offset] >= 0x41 && buffer[offset] <= 0x5A {  // 'A'…'Z': "RTSP/1.0 200 OK" or a request
                guard let headerEnd = buffer[offset...].firstRange(of: [13, 10, 13, 10])?.lowerBound else {
                    if buffer.count - offset > Self.maxHeader { throw Failure.headerTooLong }
                    break
                }
                let message = RTSPMessage(String(decoding: buffer[offset..<headerEnd], as: UTF8.self))
                let length = try Self.contentLength(of: message)
                let bodyStart = headerEnd + 4
                guard buffer.count - bodyStart >= length else { break }
                items.append(.message(message, body: Array(buffer[bodyStart..<(bodyStart + length)])))
                offset = bodyStart + length
            } else {
                offset += 1  // resynchronise on the next '$' or message
            }
        }
        return items
    }

    /// The body's size: digits only (no sign, no spaces inside), at most `maxBody`.
    static func contentLength(of message: RTSPMessage) throws -> Int {
        guard let value = message.header("Content-Length") else { return 0 }
        guard !value.isEmpty, value.utf8.allSatisfy({ $0 >= 0x30 && $0 <= 0x39 }) else { throw Failure.badContentLength(value) }
        // More digits than any accepted size: reject before converting, which could overflow.
        guard value.utf8.count <= 6, let length = Int(value) else { throw Failure.bodyTooLarge(Int.max) }
        guard length <= maxBody else { throw Failure.bodyTooLarge(length) }
        return length
    }
}

/// Basic or Digest (RFC 2617, MD5) credentials for whichever the camera asks for; Digest preferred.
final class RTSPAuth {
    private enum Scheme {
        case basic
        case digest(realm: String, nonce: String, qop: String?, opaque: String?)
    }

    private let scheme: Scheme
    private let username: String
    private let password: String
    private var count = 0
    var hasFailed = false
    /// The client nonce for each Digest response; fixed in tests.
    var makeCnonce: () -> String = { String(UUID().uuidString.prefix(8)).lowercased() }

    init?(challenges: [String], username: String, password: String) {
        self.username = username
        self.password = password
        if let digest = challenges.first(where: { $0.lowercased().hasPrefix("digest") }) {
            let params = Self.parameters(String(digest.dropFirst("digest".count)))
            guard let realm = params["realm"], let nonce = params["nonce"] else { return nil }
            let offered = (params["qop"] ?? "").split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            let qop: String? = offered.contains("auth") ? "auth" : nil
            scheme = .digest(realm: realm, nonce: nonce, qop: qop, opaque: params["opaque"])
        } else if challenges.contains(where: { $0.lowercased().hasPrefix("basic") }) {
            scheme = .basic
        } else {
            return nil
        }
    }

    func header(method: String, uri: String) -> String {
        switch scheme {
        case .basic:
            return "Basic " + Data("\(username):\(password)".utf8).base64EncodedString()
        case .digest(let realm, let nonce, let qop, let opaque):
            let ha1 = Self.md5("\(username):\(realm):\(password)")
            let ha2 = Self.md5("\(method):\(uri)")
            var parts = ["username=\"\(username)\"", "realm=\"\(realm)\"", "nonce=\"\(nonce)\"", "uri=\"\(uri)\""]
            if let qop {
                count += 1
                let nc = String(format: "%08x", UInt32(count))
                let cnonce = makeCnonce()
                parts.append("response=\"\(Self.md5("\(ha1):\(nonce):\(nc):\(cnonce):\(qop):\(ha2)"))\"")
                parts += ["qop=\(qop)", "nc=\(nc)", "cnonce=\"\(cnonce)\""]
            } else {
                parts.append("response=\"\(Self.md5("\(ha1):\(nonce):\(ha2)"))\"")
            }
            if let opaque { parts.append("opaque=\"\(opaque)\"") }
            return "Digest " + parts.joined(separator: ", ")
        }
    }

    private static func md5(_ text: String) -> String {
        Insecure.MD5.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    /// `realm="x", nonce="y", stale=FALSE` → a dictionary (quoted values may contain commas).
    static func parameters(_ text: String) -> [String: String] {
        var result: [String: String] = [:]
        var rest = Substring(text)
        while true {
            rest = rest.drop(while: { $0 == " " || $0 == "," })
            guard let eq = rest.firstIndex(of: "=") else { break }
            let key = rest[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            rest = rest[rest.index(after: eq)...]
            let value: Substring
            if rest.first == "\"" {
                let afterQuote = rest.dropFirst()
                let close = afterQuote.firstIndex(of: "\"") ?? afterQuote.endIndex
                value = afterQuote[..<close]
                rest = close < afterQuote.endIndex ? afterQuote[afterQuote.index(after: close)...] : ""
            } else {
                let comma = rest.firstIndex(of: ",") ?? rest.endIndex
                value = rest[..<comma]
                rest = rest[comma...]
            }
            result[key] = value.trimmingCharacters(in: .whitespaces)
        }
        return result
    }
}

// MARK: - RTSP URLs

/// An RTSP address someone typed in for an external camera: `rtsp://` or `rtsps://`, with the credentials
/// held apart from the URL the requests carry, so a password never reaches a request line or a log.
struct RTSPURL: Equatable {
    var isTLS: Bool
    /// Without the brackets of an IPv6 literal.
    var host: String
    var port: UInt16
    /// Always starts with "/", and keeps any query string.
    var path: String
    /// "" when the URL carried none.
    var username: String
    var password: String

    /// The URL to put on the request line: scheme, host, port and path, never the credentials.
    var absolute: String {
        let authority = host.contains(":") ? "[\(host)]" : host
        return "\(isTLS ? "rtsps" : "rtsp")://\(authority):\(port)\(path)"
    }

    enum Problem: Error, Equatable {
        case scheme(String)
        case noHost
        case port(String)
        case malformed

        /// What to show beside the field.
        var message: String {
            switch self {
            case .scheme(let found):
                return String(localized: "Only rtsp:// and rtsps:// addresses work here (\(found)).",
                              comment: "The scheme the person typed")
            case .noHost:
                return String(localized: "The address has no host.")
            case .port(let found):
                return String(localized: "\(found) isn't a port number.",
                              comment: "What the person typed where a port belongs")
            case .malformed:
                return String(localized: "That isn't an RTSP address.")
            }
        }
    }

    /// Hand-written rather than left to `URLComponents`, which reads userinfo differently from the C#
    /// `System.Uri` the Windows port would otherwise use: the two platforms have to agree.
    static func parse(_ text: String) -> Result<RTSPURL, Problem> {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let separator = trimmed.range(of: "://") else { return .failure(.malformed) }
        let isTLS: Bool
        switch trimmed[..<separator.lowerBound].lowercased() {
        case "rtsp": isTLS = false
        case "rtsps": isTLS = true
        default: return .failure(.scheme(String(trimmed[..<separator.lowerBound])))
        }
        // The path starts at the first "/" after the authority; with none, the whole stream is at "/".
        let rest = trimmed[separator.upperBound...]
        let pathStart = rest.firstIndex(of: "/") ?? rest.endIndex
        var authority = rest[..<pathStart]
        let path = pathStart == rest.endIndex ? "/" : String(rest[pathStart...])

        // A password may hold "@" only percent-encoded, so the last "@" is what separates the credentials.
        var username = "", password = ""
        if let at = authority.lastIndex(of: "@") {
            let credentials = authority[..<at]
            authority = authority[authority.index(after: at)...]
            if let colon = credentials.firstIndex(of: ":") {
                username = decoded(credentials[..<colon])
                password = decoded(credentials[credentials.index(after: colon)...])
            } else {
                username = decoded(credentials)
            }
        }

        // An IPv6 literal is bracketed, and its colons are not the port's.
        let host: String
        var portText: Substring = ""
        if authority.hasPrefix("[") {
            guard let close = authority.firstIndex(of: "]") else { return .failure(.malformed) }
            host = String(authority[authority.index(after: authority.startIndex)..<close])
            let after = authority[authority.index(after: close)...]
            if after.hasPrefix(":") {
                portText = after.dropFirst()
            } else if !after.isEmpty {
                return .failure(.malformed)
            }
        } else if let colon = authority.lastIndex(of: ":") {
            host = String(authority[..<colon])
            portText = authority[authority.index(after: colon)...]
        } else {
            host = String(authority)
        }
        guard !host.isEmpty else { return .failure(.noHost) }

        var port: UInt16 = isTLS ? 322 : 554
        if !portText.isEmpty {
            guard let value = UInt16(portText), value > 0 else { return .failure(.port(String(portText))) }
            port = value
        }
        return .success(RTSPURL(isTLS: isTLS, host: host, port: port, path: path,
                                username: username, password: password))
    }

    /// Percent-decoded, or the text as typed when it isn't valid percent-encoding.
    private static func decoded(_ text: Substring) -> String {
        String(text).removingPercentEncoding ?? String(text)
    }
}


// MARK: - SDP

enum SDP {
    struct Video {
        var codec: VideoDepacketizer.Codec
        var payloadType: Int
        var control: String?
        var parameterSets: [[UInt8]]
    }

    /// The first H.264 or H.265 video track. A track whose `rtpmap` names another codec (VP8, MJPEG…) is not
    /// one; only an SDP that maps no payload type at all is taken to be H.264, as some cameras leave it out.
    static func video(in sdp: String) -> Video? {
        var inVideo = false
        var inMedia = false
        var payloadTypes: [Int] = []
        var codecs: [Int: VideoDepacketizer.Codec] = [:]
        var mapped: Set<Int> = []
        var fmtp: [Int: [String: String]] = [:]
        var control: String?
        var sessionControl: String?
        for raw in sdp.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.hasPrefix("m=") {
                if inVideo { break }  // only the first video section
                inMedia = true
                inVideo = line.hasPrefix("m=video")
                payloadTypes = inVideo ? line.split(separator: " ").dropFirst(3).compactMap { Int($0) } : []
                continue
            }
            if line.hasPrefix("a=control:") {
                let value = String(line.dropFirst("a=control:".count))
                if inVideo { control = value } else if !inMedia { sessionControl = value }
                continue
            }
            guard inVideo else { continue }
            if line.hasPrefix("a=rtpmap:") {
                let parts = line.dropFirst("a=rtpmap:".count).split(separator: " ", maxSplits: 1)
                guard parts.count == 2, let pt = Int(parts[0]) else { continue }
                mapped.insert(pt)
                let name = parts[1].split(separator: "/").first?.uppercased() ?? ""
                if name == "H264" { codecs[pt] = .h264 } else if name == "H265" || name == "HEVC" { codecs[pt] = .h265 }
            } else if line.hasPrefix("a=fmtp:") {
                let parts = line.dropFirst("a=fmtp:".count).split(separator: " ", maxSplits: 1)
                guard parts.count == 2, let pt = Int(parts[0]) else { continue }
                var params: [String: String] = [:]
                for item in parts[1].split(separator: ";") {
                    let pair = item.split(separator: "=", maxSplits: 1)
                    guard pair.count == 2 else { continue }
                    params[pair[0].trimmingCharacters(in: .whitespaces).lowercased()] = pair[1].trimmingCharacters(in: .whitespaces)
                }
                fmtp[pt] = params
            }
        }
        guard let pt = payloadTypes.first(where: { codecs[$0] != nil }) ?? (mapped.isEmpty ? payloadTypes.first : nil) else { return nil }
        let codec = codecs[pt] ?? .h264
        let params = fmtp[pt] ?? [:]
        let encoded: [String]
        switch codec {
        case .h264: encoded = (params["sprop-parameter-sets"] ?? "").split(separator: ",").map(String.init)
        case .h265: encoded = ["sprop-vps", "sprop-sps", "sprop-pps"].compactMap { params[$0] }
        }
        let sets = encoded.compactMap { base64($0) }.filter { !$0.isEmpty }
        return Video(codec: codec, payloadType: pt, control: control ?? sessionControl, parameterSets: sets)
    }

    static func resolve(_ control: String?, base: String) -> String {
        guard let control, !control.isEmpty, control != "*" else { return base }
        if control.lowercased().hasPrefix("rtsp") { return control }
        return base.hasSuffix("/") ? base + control : base + "/" + control
    }

    private static func base64(_ text: String) -> [UInt8]? {
        var padded = text.trimmingCharacters(in: .whitespaces)
        while padded.count % 4 != 0 { padded += "=" }
        return Data(base64Encoded: padded).map { [UInt8]($0) }
    }
}


// MARK: - JPEG stream

/// Splits the port-6000 stream into JPEG payloads. Each one comes behind a 16-byte header whose first three
/// bytes give its size (little-endian).
struct JPEGFrameReader {
    private var buffer: [UInt8] = []

    /// Adds received bytes; returns the payloads they completed, or nil if a header makes no sense.
    mutating func append(_ data: Data) -> [Data]? {
        buffer.append(contentsOf: data)
        var frames: [Data] = []
        var offset = 0
        while buffer.count - offset >= 16 {
            let size = Int(buffer[offset]) | Int(buffer[offset + 1]) << 8 | Int(buffer[offset + 2]) << 16
            guard size > 0 else { return nil }
            guard buffer.count - offset >= 16 + size else { break }
            frames.append(Data(buffer[(offset + 16)..<(offset + 16 + size)]))
            offset += 16 + size
        }
        if offset > 0 { buffer.removeFirst(offset) }
        return frames
    }
}
