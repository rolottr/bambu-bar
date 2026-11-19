import Foundation

/// Where to find the printer. The access code lives in the Keychain, not here.
struct PrinterConfig: Codable, Equatable {
    var host: String
    var serial: String
    var name: String
    /// SSDP model code ("N7" for the P2S).
    var modelCode: String
    /// The camera is on for this printer (Printers page). Off, or never set (printers saved by earlier
    /// versions), it's hidden; the switch in Settings has to be on too.
    var cameraEnabled: Bool? = nil
    /// Issue codes never to show for this printer (`HMS_0300_0100_0001_0007`, `0700_8006`), added with Ignore next
    /// to an issue and removed on the Printers page. Nil for printers saved by earlier versions.
    var ignoredIssues: [String]? = nil
    /// An external camera to show instead of the printer's own (Printers page), without its credentials. Nil:
    /// the printer's own camera.
    var cameraURL: String? = nil
    /// The username `cameraURL` needs, if it needs one; its password is in the keychain.
    var cameraUsername: String? = nil

    var modelName: String { PrinterModel.name(for: modelCode, serial: serial) }
    var model: PrinterModel? { PrinterModel.find(code: modelCode, serial: serial) }

    // MARK: - Camera

    /// Where this printer's camera stream comes from: an external camera when one is entered and its URL still
    /// parses, otherwise whatever this model serves. `password` comes from the keychain.
    func cameraSource(password: String?) -> CameraSource {
        guard let cameraURL, case .success(let url) = RTSPURL.parse(cameraURL) else {
            return .printer(CameraTransport.forModel(modelName))
        }
        return .external(ExternalCamera(url: url, username: cameraUsername ?? "", password: password ?? ""))
    }

    /// Why an entered camera URL can't be used, if it can't. A settings file edited by hand can hold one that no
    /// longer parses; saying so beats quietly showing the printer's own camera instead.
    var cameraSourceProblem: String? {
        guard let cameraURL, case .failure(let problem) = RTSPURL.parse(cameraURL) else { return nil }
        return problem.message
    }

    /// Printers listen on 8883; `host:port` reaches a mock printer (scripts/fleet).
    var endpoint: (host: String, port: UInt16) {
        let parts = host.split(separator: ":")
        guard parts.count == 2, let port = UInt16(parts[1]) else { return (host, 8883) }
        return (String(parts[0]), port)
    }

    // MARK: - Names

    /// What a printer is called before it has announced its own name: its model and the end of its serial
    /// ("P2S 495").
    static func placeholderName(serial: String, modelCode: String) -> String {
        let model = PrinterModel.find(code: modelCode, serial: serial)
        return "\(model?.name ?? String(localized: "Printer")) \(serial.suffix(3))"
    }

    /// The name is one Bambu Bar made up, not one the person chose: the bare serial (in any case) or the
    /// placeholder. The name the printer announces on the network may replace it.
    var hasGeneratedName: Bool {
        name.caseInsensitiveCompare(serial) == .orderedSame
            || name == Self.placeholderName(serial: serial, modelCode: modelCode)
            || name == Self.placeholderName(serial: serial, modelCode: "")
    }

    // MARK: - Saved printers

    /// Saved printers with uppercase serials, once each. Earlier versions could save one printer twice
    /// (a slicer that keeps a lowercase serial, imported twice), which showed it twice with one copy offline.
    /// The first copy wins: it's the one the person has been looking at.
    static func deduplicated(_ configs: [PrinterConfig]) -> [PrinterConfig] {
        var seen: Set<String> = []
        var result: [PrinterConfig] = []
        for var config in configs {
            config.serial = config.serial.uppercased()
            guard !config.serial.isEmpty, seen.insert(config.serial).inserted else { continue }
            result.append(config)
        }
        return result
    }
}
