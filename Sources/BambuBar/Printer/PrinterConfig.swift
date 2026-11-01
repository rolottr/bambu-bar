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

    var modelName: String { PrinterModel.name(for: modelCode, serial: serial) }
    var model: PrinterModel? { PrinterModel.find(code: modelCode, serial: serial) }

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
