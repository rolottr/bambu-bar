import CryptoKit
import Foundation

// The camera protocols' parsing, apart from the network code so the unit tests can compile it on its own.

// MARK: - RTSP messages

struct RTSPMessage {
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

// MARK: - SDP

enum SDP {
    struct Video {
        var codec: VideoDepacketizer.Codec
        var payloadType: Int
        var control: String?
        var parameterSets: [[UInt8]]
    }

    /// The first H.264 or H.265 video track.
    static func video(in sdp: String) -> Video? {
        var inVideo = false
        var inMedia = false
        var payloadTypes: [Int] = []
        var codecs: [Int: VideoDepacketizer.Codec] = [:]
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
        guard let pt = payloadTypes.first(where: { codecs[$0] != nil }) ?? (codecs.isEmpty ? payloadTypes.first : nil) else { return nil }
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
