import Foundation

/// A printer some slicer already knows the access code for.
struct SlicerPrinter: Equatable, Identifiable {
    var id: String { serial }
    var serial: String
    /// Best first. Bambu Studio keeps the code it got from the cloud (`access_code`) and the one typed in
    /// for LAN mode (`user_access_code`); either may be stale, so both are kept to try in turn.
    var accessCodes: [String]
    var host: String?
    var name: String?
    /// "Bambu Studio", "OrcaSlicer"…
    var source: String
}

/// Reads access codes out of Bambu Studio's and OrcaSlicer's settings, so setup needs no typing.
///
/// Both slicers save `<Application Support>/<App>/<App>.conf` as JSON (on Windows with an MD5 line after it)
/// with one object per section: `access_code` and `user_access_code` map serial → code, `ip_address` and
/// `user_access_dev_ip` map serial → IP (BambuStudio `DevManager.cpp`, `AppConfig.cpp`). Newer Bambu Studio
/// versions save `user_access_dev_ip` scrambled with the install's `app.slicer_uuid` (see `decodeIP`).
/// Names and models aren't saved; they come from the printer's network announcements.
enum SlicerImport {
    static let knownApps = [("BambuStudio", "Bambu Studio"), ("BambuStudioBeta", "Bambu Studio Beta"),
                            ("OrcaSlicer", "OrcaSlicer"), ("OrcaSlicerNightly", "OrcaSlicer Nightly")]

    /// This Mac's `~/Library/Application Support`.
    static var localSupport: URL? {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
    }

    /// Where the file panel should open: Bambu Studio's folder when there is one, else Application Support.
    static var suggestedFolder: URL? {
        guard let support = localSupport else { return nil }
        for (folder, _) in knownApps {
            let dir = support.appendingPathComponent(folder)
            if FileManager.default.fileExists(atPath: dir.path) { return dir }
        }
        return support
    }

    /// Every printer with a stored access code in this Mac's slicers.
    static func scan() -> [SlicerPrinter] {
        guard let support = localSupport else { return [] }
        return read(supportFolder: support, suffix: nil)
    }

    private static func read(supportFolder: URL, suffix: String?) -> [SlicerPrinter] {
        var found: [SlicerPrinter] = []
        for (folder, title) in knownApps {
            let url = supportFolder.appendingPathComponent(folder).appendingPathComponent("\(folder).conf")
            let source = suffix.map { String(localized: "\(title) on \($0)", comment: "Slicer app, then a user name or a disk") } ?? title
            for printer in read(url, source: source) where !found.contains(where: { $0.serial == printer.serial }) {
                found.append(printer)
            }
        }
        return found
    }

    /// Reads a settings file, or a folder holding one (Bambu Studio's own folder, Application Support, a home
    /// folder or a whole disk), as picked or dropped by the person.
    static func read(picked url: URL) -> [SlicerPrinter] {
        var isFolder: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isFolder) else { return [] }
        guard isFolder.boolValue else {
            let source = url.lastPathComponent.lowercased().hasPrefix("orca") ? "OrcaSlicer" : "Bambu Studio"
            return read(url, source: String(localized: "\(source) settings file", comment: "Bambu Studio or OrcaSlicer"))
        }
        let candidates = [url, url.appendingPathComponent("Library/Application Support")]
            + knownApps.map { url.appendingPathComponent($0.0) }
        for candidate in candidates {
            // Bambu Studio's own folder: the .conf sits right inside it.
            for (folder, title) in knownApps {
                let conf = candidate.appendingPathComponent("\(folder).conf")
                let printers = read(conf, source: title)
                if !printers.isEmpty { return printers }
            }
            let printers = read(supportFolder: candidate, suffix: nil)
            if !printers.isEmpty { return printers }
        }
        // A whole disk.
        let users = url.appendingPathComponent("Users")
        for user in (try? FileManager.default.contentsOfDirectory(at: users, includingPropertiesForKeys: nil)) ?? [] {
            let printers = read(supportFolder: user.appendingPathComponent("Library/Application Support"), suffix: nil)
            if !printers.isEmpty { return printers }
        }
        return []
    }

    /// Parses one config file.
    static func read(_ url: URL, source: String) -> [SlicerPrinter] {
        guard let data = try? Data(contentsOf: url), var text = String(data: data, encoding: .utf8) else { return [] }
        // Drop anything after the JSON object (the "# MD5 checksum …" line some builds append).
        if let end = text.lastIndex(of: "}") { text = String(text[...end]) }
        guard let json = try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any] else { return [] }

        func section(_ name: String) -> [String: String] {
            guard let dict = json[name] as? [String: Any] else { return [:] }
            return dict.compactMapValues { value -> String? in
                let s = (value as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                return s.isEmpty ? nil : s
            }
        }
        let cloud = section("access_code"), typed = section("user_access_code")
        let uuid = (json["app"] as? [String: Any])?["slicer_uuid"] as? String ?? ""
        let ips = section("ip_address"), typedIPs = section("user_access_dev_ip").compactMapValues { decodeIP($0, key: uuid) }
        let serials = Set(cloud.keys).union(typed.keys)
        return serials.sorted().compactMap { serial in
            var codes: [String] = []
            for code in [cloud[serial], typed[serial]].compactMap({ $0 }) where !codes.contains(code) { codes.append(code) }
            guard !codes.isEmpty else { return nil }
            // Store the serial uppercased to match the network announcement and the certificate (both uppercase),
            // so one printer isn't taken for two. `serial` itself stays as the slicer's own lowercase key for the
            // code and IP lookups above.
            return SlicerPrinter(serial: serial.uppercased(), accessCodes: codes,
                                 host: typedIPs[serial] ?? ips[serial], name: nil, source: source)
        }
    }

    /// Undoes `BBLCrossTalk::Encode_DevIp` (BambuStudio `BBLUtil.cpp`): hex of the IP XORed with the low byte of
    /// successive `std::mt19937` outputs, seeded with the 32-bit FNV-1a hash of the slicer's UUID. Plain IPs
    /// (older versions, OrcaSlicer) come back as they are.
    static func decodeIP(_ value: String, key: String) -> String? {
        func isAddress(_ text: String) -> Bool {
            !text.isEmpty && text.count <= 64 && text.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || ".:-".contains($0)) }
                && text.contains { ".:".contains($0) }
        }
        if isAddress(value) && value.contains(".") { return value }
        guard !key.isEmpty, value.count % 2 == 0, value.allSatisfy(\.isHexDigit) else { return isAddress(value) ? value : nil }
        var seed: UInt32 = 2_166_136_261
        for byte in key.utf8 { seed = (seed ^ UInt32(byte)) &* 16_777_619 }
        var rng = MersenneTwister(seed: seed)
        var bytes: [UInt8] = []
        var index = value.startIndex
        while index < value.endIndex {
            let next = value.index(index, offsetBy: 2)
            guard let byte = UInt8(value[index..<next], radix: 16) else { return nil }
            bytes.append(byte ^ UInt8(truncatingIfNeeded: rng.next()))
            index = next
        }
        let plain = String(decoding: bytes, as: UTF8.self)
        return isAddress(plain) ? plain : nil
    }
}

/// The 32-bit Mersenne Twister, matching C++'s `std::mt19937`.
struct MersenneTwister {
    private var state = [UInt32](repeating: 0, count: 624)
    private var index = 624

    init(seed: UInt32) {
        state[0] = seed
        for i in 1..<624 { state[i] = 1_812_433_253 &* (state[i - 1] ^ (state[i - 1] >> 30)) &+ UInt32(i) }
    }

    mutating func next() -> UInt32 {
        if index >= 624 {
            for i in 0..<624 {
                let y = (state[i] & 0x8000_0000) | (state[(i + 1) % 624] & 0x7FFF_FFFF)
                state[i] = state[(i + 397) % 624] ^ (y >> 1) ^ (y & 1 == 0 ? 0 : 0x9908_B0DF)
            }
            index = 0
        }
        var y = state[index]
        index += 1
        y ^= y >> 11
        y ^= (y << 7) & 0x9D2C_5680
        y ^= (y << 15) & 0xEFC6_0000
        y ^= y >> 18
        return y
    }
}
