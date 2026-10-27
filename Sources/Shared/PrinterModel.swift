import Foundation

/// What a Bambu Lab model has, so the app and widgets show only what the printer can report.
///
/// From the printer profiles Bambu Lab publishes with Bambu Studio: `resources/printers/<model>.json`
/// (`support_chamber`, `support_chamber_temp_display`, `support_chamber_temp_edit_range`, `support_aux_fan`,
/// `support_chamber_fan`, `support_ams_humidity`, `nozzle_temp_range`, `bed_temp_range`, the tool heads) and
/// `resources/profiles/BBL/machine/<name> 0.4 nozzle.json` (`printable_area`, `printable_height`).
struct PrinterModel: Equatable, Sendable {
    /// Bambu's model id, as SSDP and Bambu Studio name it ("N7").
    var code: String
    /// "P2S", "X1 Carbon"…
    var name: String
    var shape: PrinterShape
    /// The first three characters of its serial numbers.
    var serialPrefix: String
    /// A closed chamber with a door.
    var isEnclosed: Bool
    /// Reports a chamber temperature worth showing (the P1S has a chamber but no sensor).
    var hasChamberSensor: Bool
    /// Highest chamber temperature it can heat to; nil without active chamber heating.
    var chamberHeaterMax: Int?
    var hasAuxFan: Bool
    var hasChamberFan: Bool
    /// A switchable chamber light (none on the A series).
    var hasChamberLight: Bool { shape != .bedSlinger && shape != .mini }
    /// The AMS reports humidity (not the AMS Lite of the A series).
    var hasAMSHumidity: Bool
    /// Only the AMS Lite fits (A1, A1 mini).
    var usesAMSLite: Bool
    /// Two tool heads (H2D, H2C, X2D).
    var nozzleCount: Int
    var nozzleMax: Int
    var bedMax: Int
    /// Width × depth × height in millimetres.
    var buildVolume: (x: Int, y: Int, z: Int)
    var hasLidar: Bool
    /// Takes the laser and cutting modules (H2 family).
    var hasLaserModule: Bool

    static func == (a: PrinterModel, b: PrinterModel) -> Bool { a.code == b.code }

    var buildVolumeText: String { "\(buildVolume.x) × \(buildVolume.y) × \(buildVolume.z) mm" }

    var chamberText: String {
        if let max = chamberHeaterMax { return String(localized: "Heated, up to \(max) °C") }
        if hasChamberSensor { return String(localized: "Enclosed, with temperature sensor") }
        return isEnclosed ? String(localized: "Enclosed") : String(localized: "Open frame")
    }

    // MARK: - Catalog

    static let all: [PrinterModel] = [
        .init(code: "N7", name: "P2S", shape: .enclosed, serialPrefix: "22E", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: nil, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 1, nozzleMax: 300, bedMax: 110, buildVolume: (256, 256, 256), hasLidar: false, hasLaserModule: false),
        .init(code: "C12", name: "P1S", shape: .enclosed, serialPrefix: "01P", isEnclosed: true, hasChamberSensor: false,
              chamberHeaterMax: nil, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 1, nozzleMax: 300, bedMax: 100, buildVolume: (256, 256, 250), hasLidar: false, hasLaserModule: false),
        .init(code: "C11", name: "P1P", shape: .openFrame, serialPrefix: "01S", isEnclosed: false, hasChamberSensor: false,
              chamberHeaterMax: nil, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 1, nozzleMax: 300, bedMax: 100, buildVolume: (256, 256, 250), hasLidar: false, hasLaserModule: false),
        .init(code: "BL-P001", name: "X1 Carbon", shape: .enclosed, serialPrefix: "00M", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: nil, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 1, nozzleMax: 300, bedMax: 110, buildVolume: (256, 256, 250), hasLidar: true, hasLaserModule: false),
        .init(code: "BL-P002", name: "X1", shape: .enclosed, serialPrefix: "00W", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: nil, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 1, nozzleMax: 300, bedMax: 110, buildVolume: (256, 256, 250), hasLidar: true, hasLaserModule: false),
        .init(code: "C13", name: "X1E", shape: .enclosed, serialPrefix: "03W", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: 60, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 1, nozzleMax: 320, bedMax: 110, buildVolume: (256, 256, 250), hasLidar: true, hasLaserModule: false),
        .init(code: "N6", name: "X2D", shape: .enclosed, serialPrefix: "20P", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: 65, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 2, nozzleMax: 300, bedMax: 120, buildVolume: (256, 256, 261), hasLidar: false, hasLaserModule: false),
        .init(code: "N2S", name: "A1", shape: .bedSlinger, serialPrefix: "039", isEnclosed: false, hasChamberSensor: false,
              chamberHeaterMax: nil, hasAuxFan: false, hasChamberFan: false, hasAMSHumidity: false, usesAMSLite: true,
              nozzleCount: 1, nozzleMax: 300, bedMax: 100, buildVolume: (256, 256, 256), hasLidar: false, hasLaserModule: false),
        .init(code: "N1", name: "A1 mini", shape: .mini, serialPrefix: "030", isEnclosed: false, hasChamberSensor: false,
              chamberHeaterMax: nil, hasAuxFan: false, hasChamberFan: false, hasAMSHumidity: false, usesAMSLite: true,
              nozzleCount: 1, nozzleMax: 300, bedMax: 80, buildVolume: (180, 180, 180), hasLidar: false, hasLaserModule: false),
        .init(code: "N9", name: "A2L", shape: .bedSlinger, serialPrefix: "26A", isEnclosed: false, hasChamberSensor: false,
              chamberHeaterMax: nil, hasAuxFan: false, hasChamberFan: false, hasAMSHumidity: false, usesAMSLite: false,
              nozzleCount: 1, nozzleMax: 300, bedMax: 80, buildVolume: (330, 320, 325), hasLidar: false, hasLaserModule: false),
        .init(code: "O1D", name: "H2D", shape: .wide, serialPrefix: "094", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: 65, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 2, nozzleMax: 350, bedMax: 120, buildVolume: (350, 320, 325), hasLidar: false, hasLaserModule: true),
        .init(code: "O1E", name: "H2D Pro", shape: .wide, serialPrefix: "239", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: 65, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 2, nozzleMax: 350, bedMax: 120, buildVolume: (350, 320, 325), hasLidar: false, hasLaserModule: true),
        .init(code: "O1C", name: "H2C", shape: .wide, serialPrefix: "31B", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: 65, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 2, nozzleMax: 350, bedMax: 120, buildVolume: (330, 320, 325), hasLidar: false, hasLaserModule: true),
        .init(code: "O1S", name: "H2S", shape: .wide, serialPrefix: "093", isEnclosed: true, hasChamberSensor: true,
              chamberHeaterMax: 65, hasAuxFan: true, hasChamberFan: true, hasAMSHumidity: true, usesAMSLite: false,
              nozzleCount: 1, nozzleMax: 350, bedMax: 120, buildVolume: (340, 320, 340), hasLidar: false, hasLaserModule: true),
    ]

    /// Other ids the same models report (older firmware, Pro and V2 variants).
    private static let aliases: [String: String] = [
        "BL-P003": "C13", "A12": "N1", "A04": "N1", "A11": "N2S", "O2D": "O1E", "O1C2": "O1C",
    ]

    /// By serial prefix when it's one Bambu Lab uses, then by model id. The serial wins because setups before 1.2
    /// saved "N7" for printers typed in by hand, which named an A1 a P2S everywhere.
    static func find(code: String, serial: String = "") -> PrinterModel? {
        let prefix = serial.prefix(3).uppercased()
        if !prefix.isEmpty, let match = all.first(where: { $0.serialPrefix == prefix }) { return match }
        let upper = code.uppercased()
        let id = aliases[upper] ?? upper
        return all.first { $0.code == id }
    }

    /// By display name ("P2S", "A1 mini") or model id; what the widget gets in its snapshot.
    static func named(_ name: String?) -> PrinterModel? {
        guard let name, !name.isEmpty else { return nil }
        return all.first { $0.name.caseInsensitiveCompare(name) == .orderedSame } ?? find(code: name)
    }

    static func name(for code: String, serial: String = "") -> String {
        find(code: code, serial: serial)?.name ?? String(localized: "Bambu Lab printer")
    }
}

extension PrinterStatus {
    /// Drops readings the model has no sensor or fan for. Some firmware reports placeholders there (a P1S sends
    /// a chamber temperature without a sensor, an A1 sends fan fields it doesn't have).
    mutating func fit(to model: PrinterModel?) {
        guard let model else { return }
        if !model.hasChamberSensor { chamber = nil; chamberTarget = nil }
        if model.chamberHeaterMax == nil { chamberTarget = nil }
        if !model.hasAuxFan { auxFan = nil }
        if !model.hasChamberFan { exhaustFan = nil }
        if !model.isEnclosed { doorOpen = nil }
        if !model.hasChamberLight { chamberLightOn = nil }
        if model.nozzleCount < 2 { secondNozzle = nil; leftNozzleInUse = nil }
        if !model.hasAMSHumidity {
            for i in ams.indices where ams[i].kind == .lite { ams[i].humidity = nil; ams[i].humidityLevel = nil }
        }
    }
}
