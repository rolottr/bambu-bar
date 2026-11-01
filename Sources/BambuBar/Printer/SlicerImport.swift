import Foundation

/// A printer some slicer already knows the access code for.
struct SlicerPrinter: Equatable, Identifiable {
    var id: String { serial }
    /// Uppercase, as the printer announces it and its certificate names it, whatever case the slicer saved.
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

    /// This Mac's `~/Library/Application Support`: the person's own, not the sandbox container's.
    static var localSupport: URL? {
        guard let entry = getpwuid(getuid()), let home = entry.pointee.pw_dir else {
            return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
        }
        return URL(fileURLWithPath: String(cString: home), isDirectory: true)
            .appendingPathComponent("Library/Application Support", isDirectory: true)
    }

    /// The App Store build runs in the App Sandbox, which only opens folders the person chose.
    static let isSandboxed = ProcessInfo.processInfo.environment["APP_SANDBOX_CONTAINER_ID"] != nil
    private static let bookmarkKey = "slicerFolderBookmark"

    /// Sandboxed and not yet given a slicer folder: setup asks for one.
    static var needsAccess: Bool {
        isSandboxed && UserDefaults.standard.data(forKey: bookmarkKey) == nil
    }

    /// Keeps the folder (or file) the person picked so later scans can read it again (sandboxed builds only).
    static func rememberAccess(to url: URL) {
        guard isSandboxed,
              let data = try? url.bookmarkData(options: [.withSecurityScope, .securityScopeAllowOnlyReadAccess],
                                               includingResourceValuesForKeys: nil, relativeTo: nil) else { return }
        UserDefaults.standard.set(data, forKey: bookmarkKey)
    }

    /// The remembered folder, or nil (and forgotten, so setup asks again) when it can't be resolved any more.
    private static var grantedFolder: URL? {
        guard let data = UserDefaults.standard.data(forKey: bookmarkKey) else { return nil }
        var stale = false
        guard let url = try? URL(resolvingBookmarkData: data, options: .withSecurityScope, bookmarkDataIsStale: &stale) else {
            UserDefaults.standard.removeObject(forKey: bookmarkKey)
            return nil
        }
        if stale { rememberAccess(to: url) }
        return url
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
        guard isSandboxed else {
            guard let support = localSupport else { return [] }
            return read(supportFolder: support, suffix: nil)
        }
        guard let folder = grantedFolder else { return [] }
        let scoped = folder.startAccessingSecurityScopedResource()
        defer { if scoped { folder.stopAccessingSecurityScopedResource() } }
        return read(picked: folder)
    }

    private static func read(supportFolder: URL, suffix: String?) -> [SlicerPrinter] {
        var found: [SlicerPrinter] = []
        for (folder, title) in knownApps {
            let url = supportFolder.appendingPathComponent(folder).appendingPathComponent("\(folder).conf")
            let source = suffix.map { String(localized: "\(title) on \($0)", comment: "Slicer app, then a user name or a disk") } ?? title
            found += read(url, source: source)
        }
        return merge(found)
    }

    /// One entry per printer, in the order first seen. When several slicers (or files) know the same printer,
    /// their codes are all kept, in order, so a stale code in one doesn't hide a working one in another; the
    /// address and source come from the first that has them.
    static func merge(_ printers: [SlicerPrinter]) -> [SlicerPrinter] {
        var merged: [SlicerPrinter] = []
        var index: [String: Int] = [:]
        for printer in printers {
            let serial = printer.serial.uppercased()
            guard let i = index[serial] else {
                index[serial] = merged.count
                var first = printer
                first.serial = serial
                merged.append(first)
                continue
            }
            for code in printer.accessCodes where !merged[i].accessCodes.contains(code) { merged[i].accessCodes.append(code) }
            if merged[i].host == nil { merged[i].host = printer.host }
            if merged[i].name == nil { merged[i].name = printer.name }
        }
        return merged
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

        // Each section maps serial → value. Slicers don't always agree with themselves on the serial's case (an
        // uppercase key in one section, lowercase in another, sometimes both in one), so keys are uppercased
        // before anything is matched up: one printer, whatever the spelling. Values under two spellings of the
        // same serial are all kept, the uppercase spelling's first.
        func section(_ name: String) -> [String: [String]] {
            guard let dict = json[name] as? [String: Any] else { return [:] }
            var result: [String: [String]] = [:]
            func rank(_ key: String) -> (Int, String) { (key == key.uppercased() ? 0 : 1, key) }
            for key in dict.keys.sorted(by: { rank($0) < rank($1) }) {
                let value = (dict[key] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                guard !value.isEmpty else { continue }
                let serial = key.trimmingCharacters(in: .whitespaces).uppercased()
                guard !serial.isEmpty, result[serial]?.contains(value) != true else { continue }
                result[serial, default: []].append(value)
            }
            return result
        }
        let cloud = section("access_code"), typed = section("user_access_code")
        let uuid = (json["app"] as? [String: Any])?["slicer_uuid"] as? String ?? ""
        let ips = section("ip_address")
        let typedIPs = section("user_access_dev_ip").mapValues { $0.compactMap { decodeIP($0, key: uuid) } }
        let serials = Set(cloud.keys).union(typed.keys)
        return serials.sorted().compactMap { serial in
            var codes: [String] = []
            for code in (cloud[serial] ?? []) + (typed[serial] ?? []) where !codes.contains(code) { codes.append(code) }
            guard !codes.isEmpty else { return nil }
            let host = typedIPs[serial]?.first ?? ips[serial]?.first
            return SlicerPrinter(serial: serial, accessCodes: codes, host: host, name: nil, source: source)
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
