import Foundation
import OSLog

/// What the app hands the widget about one printer.
struct SharedSnapshot: Codable, Equatable, Sendable {
    enum Link: String, Codable, Sendable {
        /// No printer set up yet.
        case unconfigured
        case connecting
        case online
        /// Configured, but the printer isn't answering.
        case offline
        case unauthorized
    }

    var serial: String = ""
    var printerName: String
    var model: String
    var link: Link
    var status: PrinterStatus?
    var lastJob: JobRecord?
    var savedAt: Date

    static let placeholder = SharedSnapshot(
        printerName: "P2S", model: "P2S", link: .online, status: .sample, lastJob: nil, savedAt: Date())

    static func unconfigured(_ now: Date = Date()) -> SharedSnapshot {
        SharedSnapshot(printerName: "Bambu Bar", model: "P2S", link: .unconfigured, status: nil, lastJob: nil, savedAt: now)
    }
}

/// Every printer, written by the app as one JSON file in the shared app group container.
struct SharedFleet: Codable, Equatable, Sendable {
    var printers: [SharedSnapshot]
    /// The printer the menu bar shows; widgets without a chosen printer follow it.
    var menuBarSerial: String?
    var savedAt: Date

    /// The snapshot for a widget: its chosen printer, else the menu bar's, else the first.
    func snapshot(for serial: String?) -> SharedSnapshot {
        if let serial, let match = printers.first(where: { $0.serial == serial }) { return match }
        if let menuBarSerial, let match = printers.first(where: { $0.serial == menuBarSerial }) { return match }
        return printers.first ?? .unconfigured(savedAt)
    }
}

/// Reads and writes the snapshot. The app is the only writer.
enum SharedStore {
    static let fileName = "printers.json"
    private static let log = Logger(subsystem: "dev.rolottr.BambuBar", category: "widget")

    /// `<TeamID>.dev.rolottr.BambuBar`, read from Info.plist so the team lives in one place (the build settings).
    static var appGroup: String {
        Bundle.main.object(forInfoDictionaryKey: "BambuBarAppGroup") as? String ?? "PCZ6F8Q9DZ.dev.rolottr.BambuBar"
    }

    static var containerURL: URL? {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroup)
    }

    static func load() -> SharedFleet? {
        guard let url = containerURL?.appendingPathComponent(fileName),
              let data = try? Data(contentsOf: url) else { return nil }
        return try? decoder.decode(SharedFleet.self, from: data)
    }

    @discardableResult
    static func save(_ fleet: SharedFleet) -> Bool {
        guard let dir = containerURL else {
            log.error("No app group container for \(appGroup, privacy: .public); the widgets can't be updated")
            return false
        }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        // The single-printer file of earlier versions.
        try? FileManager.default.removeItem(at: dir.appendingPathComponent("snapshot.json"))
        do {
            try encoder.encode(fleet).write(to: dir.appendingPathComponent(fileName), options: .atomic)
            return true
        } catch {
            // The widgets go on showing the last file written, so say why this one wasn't.
            log.error("Couldn't write the widget snapshot: \(String(describing: error), privacy: .public)")
            return false
        }
    }

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .secondsSince1970
        return e
    }()

    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .secondsSince1970
        return d
    }()
}

extension PrinterStatus {
    /// A mid-print P2S with an AMS 2 Pro, for previews, placeholders and the demo.
    static var sample: PrinterStatus {
        var s = PrinterStatus()
        s.state = .printing
        s.stageID = 0
        s.jobName = "Rear Dry Pod"
        s.progress = 64
        s.remainingMinutes = 62
        s.layer = 170
        s.totalLayers = 305
        s.nozzle = Temperature(current: 220, target: 220)
        s.bed = Temperature(current: 55, target: 55)
        s.chamber = 38
        s.partFan = 90
        s.auxFan = 40
        s.speed = .standard
        s.chamberLightOn = true
        s.doorOpen = false
        s.wifiSignal = -52
        s.ams = [AMSUnit(id: 0, kind: .pro2, humidity: 16, humidityLevel: 1, temperature: 28.4, dryingMinutesLeft: 0, spools: [
            Spool(id: 0, unit: 0, slot: 0, material: "PLA", name: "PLA Matte", color: "E8DBB7FF", colors: ["E8DBB7FF"], remain: 81, weight: 1000,
                  isLoaded: true, isBambu: true, preset: "GFA01", brand: "Bambu Lab", colorName: "Desert Tan", nozzleRange: 190...230),
            Spool(id: 1, unit: 0, slot: 1, material: "PLA", name: "PLA Basic", color: "000000FF", colors: ["000000FF"], remain: 39, weight: 1000,
                  isLoaded: true, isBambu: true, preset: "GFA00", brand: "Bambu Lab", colorName: "Black", nozzleRange: 190...230),
            Spool(id: 2, unit: 0, slot: 2, material: "PLA", name: "PLA+", color: "3F8E43FF", colors: ["3F8E43FF"], remain: nil,
                  isLoaded: true, isBambu: false, preset: "GFSNL03", brand: "SUNLU", nozzleRange: 200...230),
            Spool(id: 3, unit: 0, slot: 3, material: "PLA", name: "PLA Silk+", color: "F4A925FF", colors: ["F4A925FF"], remain: 22, weight: 1000,
                  isLoaded: true, isBambu: true, preset: "GFA06", brand: "Bambu Lab", colorName: "Gold", nozzleRange: 200...240),
        ])]
        s.activeSpoolID = 3
        s.externalSpool = Spool(id: Spool.externalID, unit: 255, slot: 0, material: "PETG", name: "PETG HF", color: "1F79E5FF",
                                colors: ["1F79E5FF"], remain: nil, weight: 1000, isLoaded: true, isBambu: true, preset: "GFG02",
                                brand: "Bambu Lab", colorName: "Lake Blue", nozzleRange: 230...270)
        s.firmware = "01.03.00.00"
        s.nozzleDiameter = 0.4
        s.nozzleType = "HS01"
        s.hasSDCard = true
        return s
    }
}
