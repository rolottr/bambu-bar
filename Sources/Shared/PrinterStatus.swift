import Foundation

/// What the printer is doing, folded from `print.gcode_state`.
enum JobState: String, Codable, Equatable, Sendable {
    case idle, preparing, printing, paused, finished, failed

    init(gcodeState: String) {
        switch gcodeState.uppercased() {
        case "PREPARE", "SLICING": self = .preparing
        case "RUNNING": self = .printing
        case "PAUSE": self = .paused
        case "FINISH": self = .finished
        case "FAILED": self = .failed
        default: self = .idle
        }
    }

    /// A job is on the plate and not over yet.
    var isActive: Bool { self == .preparing || self == .printing || self == .paused }

    var title: String {
        switch self {
        case .idle: return String(localized: "Ready", comment: "Printer state")
        case .preparing: return String(localized: "Preparing", comment: "Printer state")
        case .printing: return String(localized: "Printing", comment: "Printer state")
        case .paused: return String(localized: "Paused", comment: "Printer state")
        case .finished: return String(localized: "Finished", comment: "Printer state")
        case .failed: return String(localized: "Failed", comment: "Printer state")
        }
    }
}

/// The four motion profiles (`spd_lvl`).
enum SpeedLevel: Int, Codable, CaseIterable, Identifiable, Sendable {
    case silent = 1, standard, sport, ludicrous

    var id: Int { rawValue }
    var title: String {
        switch self {
        case .silent: return String(localized: "Silent", comment: "Print speed profile")
        case .standard: return String(localized: "Standard", comment: "Print speed profile")
        case .sport: return String(localized: "Sport", comment: "Print speed profile")
        case .ludicrous: return String(localized: "Ludicrous", comment: "Print speed profile")
        }
    }
    /// Nominal speed relative to Standard, as Bambu Studio shows it.
    var percent: Int {
        switch self {
        case .silent: return 50
        case .standard: return 100
        case .sport: return 124
        case .ludicrous: return 166
        }
    }
    var symbol: String {
        switch self {
        case .silent: return "tortoise.fill"
        case .standard: return "figure.walk"
        case .sport: return "figure.run"
        case .ludicrous: return "hare.fill"
        }
    }
}

struct Temperature: Codable, Equatable, Sendable {
    var current: Double
    /// Zero when the heater is off.
    var target: Double

    var isHeating: Bool { target > 0 }
    /// Within a couple of degrees of the target.
    var isAtTarget: Bool { target > 0 && abs(current - target) <= 2 }
}

/// One filament slot: an AMS tray or the external spool holder.
struct Spool: Codable, Equatable, Identifiable, Sendable {
    /// Global slot number: `unit * 4 + slot` for AMS units 0-3, `128 + n` for AMS HT, 254 for the external spool.
    var id: Int
    var unit: Int
    var slot: Int
    /// "PLA", "PETG"…; empty when the slot is empty or unknown.
    var material: String
    /// "PLA Matte"…; empty for third-party spools.
    var name: String
    /// `RRGGBBAA`.
    var color: String
    /// More than one entry for multicolour spools.
    var colors: [String]
    /// Percent remaining, nil when the printer can't tell (non-RFID spools).
    var remain: Int?
    /// Net filament weight of a full spool in grams (`tray_weight`), from the Bambu RFID tag.
    var weight: Int? = nil
    var isLoaded: Bool
    var isBambu: Bool
    /// The Bambu Studio filament preset set for the slot (`tray_info_idx`, "GFA01").
    var preset: String? = nil
    /// "Bambu Lab" for RFID spools, else the maker of the preset the slot is set to ("SUNLU").
    var brand: String? = nil
    /// Bambu Lab's colour name ("Grass Green"), for their spools and colours picked from their list.
    var colorName: String? = nil
    /// Nozzle temperatures the filament prints at, from the tag or the preset.
    var nozzleRange: ClosedRange<Int>? = nil

    static let externalID = 254
    /// The AMS 3H's slots are 1024 + slot, past the four-slot units (unit * 4 + slot) and the AMS HTs (128-135);
    /// the AMS-B's follow.
    static let ams3hBase = 1024
    static let amsBBase = 1056

    var isExternal: Bool { id == Self.externalID }
    var remainText: String {
        if !isLoaded { return String(localized: "Empty", comment: "Filament slot") }
        if let remain { return "\(remain)%" }
        return "—"
    }
    var displayName: String { name.isEmpty ? material : name }


    /// Filament left in grams: the full spool's weight from its tag times the share the AMS measured left.
    var gramsLeft: Int? {
        guard isLoaded, let remain, let weight, weight > 0 else { return nil }
        return Int((Double(weight) * Double(remain) / 100).rounded())
    }

    /// "≈ 810 g" when the weight is known, else the percentage.
    var amountText: String {
        if let grams = gramsLeft { return Format.grams(grams) }
        return remainText
    }

    /// "81% · ≈ 810 g", for tooltips.
    var remainDetail: String {
        guard isLoaded else { return String(localized: "Empty", comment: "Filament slot") }
        guard let remain else { return String(localized: "Not measured", comment: "Filament left on a spool without RFID") }
        guard let grams = gramsLeft else { return "\(remain)%" }
        return "\(remain)% · \(Format.grams(grams))"
    }
    /// "A1", "A2"… like the printer's screen; "Ext" for the external spool; 1, 2… for the H3C's rack AMS (AMS 3H
    /// or AMS-B), as Bambu Studio numbers its slots.
    var label: String {
        if isExternal { return String(localized: "Ext", comment: "Short label for the external spool, beside AMS slots like A1") }
        if id >= Self.ams3hBase { return "\(slot + 1)" }
        if unit >= 128 { return "HT\(unit - 127)" }
        let letter = String(UnicodeScalar(UInt8(65 + min(unit, 25))))
        return "\(letter)\(slot + 1)"
    }

    /// The label in a sentence: "A1", "HT1" and "Ext" read on their own, a bare number doesn't ("printing with 3
    /// PETG"), so the rack AMS's slots read "Slot 3".
    var slotName: String {
        if id >= Self.ams3hBase { return String(localized: "Slot \(slot + 1)", comment: "A numbered AMS slot, on the H3C's AMS") }
        return label
    }
}

enum AMSKind: String, Codable, Sendable {
    /// `ams3h`: the H3C's 9-slot AMS, wired to its hotend rack (Bambu Studio's ZMULTI_A). `amsB`: the other one,
    /// for rack B (ZMULTI_B), without a feeder motor; known only from Bambu Studio's source, not from a report.
    case ams, lite, pro2, ht, ams3h, amsB

    var title: String {
        switch self {
        case .ams: return "AMS"
        case .lite: return "AMS Lite"
        case .pro2: return "AMS 2 Pro"
        case .ht: return "AMS HT"
        case .ams3h: return "AMS 3H"
        case .amsB: return "AMS-B"
        }
    }
}

struct AMSUnit: Codable, Equatable, Identifiable, Sendable {
    var id: Int
    var kind: AMSKind
    /// Relative humidity in percent (`humidity_raw`), when the unit reports it.
    var humidity: Int?
    /// 1 (dry) to 5 (wet), Bambu Studio's scale.
    var humidityLevel: Int?
    var temperature: Double?
    var dryingMinutesLeft: Int
    var spools: [Spool]

    /// "AMS A", "AMS 2 Pro B"…
    var title: String {
        if kind == .ams3h || kind == .amsB { return kind.title }
        if id >= 128 { return "\(kind.title) \(id - 127)" }
        return "\(kind.title) \(String(UnicodeScalar(UInt8(65 + min(id, 25)))))"
    }
}

enum IssueSeverity: Int, Codable, Comparable, Sendable {
    case fatal = 1, serious, common, info

    static func < (a: IssueSeverity, b: IssueSeverity) -> Bool { a.rawValue < b.rawValue }

    var title: String {
        switch self {
        case .fatal: return String(localized: "Error", comment: "Issue severity")
        case .serious: return String(localized: "Warning", comment: "Issue severity")
        case .common: return String(localized: "Notice", comment: "Issue severity")
        case .info: return String(localized: "Info", comment: "Issue severity")
        }
    }
}

/// A Health Management System message or the current print error.
struct PrinterIssue: Codable, Equatable, Identifiable, Sendable {
    /// `HMS_0300_0100_0001_0007` or `0700_8006`.
    var id: String
    var severity: IssueSeverity
    var message: String
    var wikiURL: URL?
}

/// Everything Bambu Bar shows about the printer, derived from the merged MQTT report.
struct PrinterStatus: Codable, Equatable, Sendable {
    var state: JobState = .idle
    var stageID: Int?
    var jobName: String = ""
    var progress: Int = 0
    var remainingMinutes: Int = 0
    var layer: Int = 0
    var totalLayers: Int = 0
    var nozzle = Temperature(current: 0, target: 0)
    var bed = Temperature(current: 0, target: 0)
    var chamber: Double?
    /// The chamber heater's target on models that heat the chamber (H2, X1E, X2D); zero when off.
    var chamberTarget: Double?
    /// The left nozzle on two-nozzle models (`nozzle` is the right one, as Bambu Studio numbers them).
    var secondNozzle: Temperature?
    /// On two-nozzle models, whether the left nozzle is the one printing.
    var leftNozzleInUse: Bool?
    /// Fan speeds in percent.
    var partFan: Int?
    var auxFan: Int?
    var exhaustFan: Int?
    var heatbreakFan: Int?
    /// The H3C's second aux fan (`airduct` part 10, "Left(Aux)" in Bambu Studio); `auxFan` is then the right one.
    var leftAuxFan: Int?
    /// The H3C's chamber fan (`airduct` part 15, "Chamber" in Bambu Studio), besides the exhaust (`exhaustFan`).
    var circulationFan: Int?
    var speed: SpeedLevel = .standard
    var chamberLightOn: Bool?
    var doorOpen: Bool?
    var wifiSignal: Int?
    var ams: [AMSUnit] = []
    var externalSpool: Spool?
    var activeSpoolID: Int?
    var issues: [PrinterIssue] = []
    /// The printer refuses unsigned print commands (Developer Mode off).
    var controlsLocked: Bool = false
    var firmware: String?
    var nozzleDiameter: Double?
    var nozzleType: String?
    var liveViewEnabled: Bool?
    var hasSDCard: Bool?
    var plateIndex: Int?
    var updateAvailable: Bool = false
    /// The printer's id for the current job (`task_id`, else `subtask_id`).
    var taskID: String?
    /// The job ended because someone stopped it (print_error 0300_400C), not because of a fault.
    var canceled: Bool = false
    /// When this status was last refreshed from the printer.
    var updatedAt = Date()

    /// The nozzle that's printing: the only one, or whichever of the two is in use.
    var activeNozzle: Temperature { leftNozzleInUse == true ? secondNozzle ?? nozzle : nozzle }
    /// The other nozzle on two-nozzle models.
    var idleNozzle: Temperature? { secondNozzle.map { leftNozzleInUse == true ? nozzle : $0 } }
    /// "Nozzle", or which of the two is printing.
    var activeNozzleTitle: String {
        secondNozzle == nil ? String(localized: "Nozzle") : leftNozzleInUse == true ? String(localized: "Left nozzle") : String(localized: "Right nozzle")
    }
    var idleNozzleTitle: String { leftNozzleInUse == true ? String(localized: "Right nozzle") : String(localized: "Left nozzle") }

    /// "Canceled" for a job someone stopped, otherwise the state's own title.
    var stateTitle: String { state == .failed && canceled ? String(localized: "Canceled", comment: "Printer state") : state.title }

    var stageText: String? {
        guard let stageID, stageID != 255, stageID != -1 else { return nil }
        return Self.stageNames[stageID]
    }

    /// Why the printer paused, short for the small widget ("Filament ran out", "By user").
    var pauseReason: String? {
        guard let stageID else { return nil }
        return Self.pauseReasons[stageID]
    }

    /// Stages where the printer has paused the print.
    private static var pausedStages: Set<Int> { Set(pauseReasons.keys) }

    static let pauseReasons: [Int: String] = [
        6: String(localized: "Filament ran out", comment: "Pause reason"),
        16: String(localized: "By user", comment: "Pause reason"),
        17: String(localized: "Front cover fell off", comment: "Pause reason"),
        20: String(localized: "Nozzle temperature fault", comment: "Pause reason"),
        21: String(localized: "Heatbed temperature fault", comment: "Pause reason"),
        23: String(localized: "Skipped steps", comment: "Pause reason"),
        26: String(localized: "AMS offline", comment: "Pause reason"),
        27: String(localized: "Heatbreak fan too slow", comment: "Pause reason"),
        28: String(localized: "Chamber temperature fault", comment: "Pause reason"),
        30: String(localized: "By G-code", comment: "Pause reason"),
        32: String(localized: "Nozzle clumping", comment: "Pause reason"),
        33: String(localized: "Cutter error", comment: "Pause reason"),
        34: String(localized: "First layer error", comment: "Pause reason"),
        35: String(localized: "Nozzle clog", comment: "Pause reason"),
    ]

    /// The line under the job name: "Heating hotend", "Paused: filament runout", "Layer 170 of 305"…
    var detailText: String {
        switch state {
        case .printing:
            if let stageText, stageID != 0 { return stageText }
            if totalLayers > 0 { return String(localized: "Layer \(layer) of \(totalLayers)") }
            return state.title
        case .preparing:
            return stageText ?? state.title
        case .paused:
            if let stageText, let stageID, Self.pausedStages.contains(stageID) { return stageText }
            return state.title
        case .finished: return state.title
        case .failed: return canceled ? stateTitle : issues.first?.message ?? String(localized: "Print failed")
        case .idle: return stageText ?? String(localized: "Ready to print")
        }
    }

    var activeSpool: Spool? {
        guard let activeSpoolID else { return nil }
        if activeSpoolID == Spool.externalID { return externalSpool }
        return ams.lazy.flatMap(\.spools).first { $0.id == activeSpoolID }
    }

    var allSpools: [Spool] { ams.flatMap(\.spools) + (externalSpool.map { [$0] } ?? []) }

    var mostSevereIssue: PrinterIssue? { issues.min { $0.severity < $1.severity } }

    /// Estimated completion, from the remaining minutes at `updatedAt`.
    var estimatedEnd: Date? {
        guard state.isActive, remainingMinutes > 0 else { return nil }
        return updatedAt.addingTimeInterval(TimeInterval(remainingMinutes * 60))
    }

    /// Progress expected at `date`, extrapolated linearly over the remaining time. Lets widgets advance between updates.
    func projectedProgress(at date: Date) -> Double {
        let now = Double(progress) / 100
        guard state == .printing, let end = estimatedEnd, end > updatedAt else { return now }
        let fraction = min(1, max(0, date.timeIntervalSince(updatedAt) / end.timeIntervalSince(updatedAt)))
        return min(0.99, now + (1 - now) * fraction)
    }

    static let stageNames: [Int: String] = [
        0: String(localized: "Printing", comment: "Printer stage"),
        1: String(localized: "Auto bed leveling", comment: "Printer stage"),
        2: String(localized: "Preheating heatbed", comment: "Printer stage"),
        3: String(localized: "Vibration compensation", comment: "Printer stage"),
        4: String(localized: "Changing filament", comment: "Printer stage"),
        5: String(localized: "M400 pause", comment: "Printer stage"),
        6: String(localized: "Paused: filament ran out", comment: "Printer stage"),
        7: String(localized: "Heating hotend", comment: "Printer stage"),
        8: String(localized: "Calibrating extrusion", comment: "Printer stage"),
        9: String(localized: "Scanning bed surface", comment: "Printer stage"),
        10: String(localized: "Inspecting first layer", comment: "Printer stage"),
        11: String(localized: "Identifying build plate", comment: "Printer stage"),
        12: String(localized: "Calibrating Micro Lidar", comment: "Printer stage"),
        13: String(localized: "Homing toolhead", comment: "Printer stage"),
        14: String(localized: "Cleaning nozzle tip", comment: "Printer stage"),
        15: String(localized: "Checking extruder temperature", comment: "Printer stage"),
        16: String(localized: "Paused by user", comment: "Printer stage"),
        17: String(localized: "Paused: front cover fell off", comment: "Printer stage"),
        18: String(localized: "Calibrating Micro Lidar", comment: "Printer stage"),
        19: String(localized: "Calibrating flow ratio", comment: "Printer stage"),
        20: String(localized: "Paused: nozzle temperature fault", comment: "Printer stage"),
        21: String(localized: "Paused: heatbed temperature fault", comment: "Printer stage"),
        22: String(localized: "Unloading filament", comment: "Printer stage"),
        23: String(localized: "Paused: skipped steps", comment: "Printer stage"),
        24: String(localized: "Loading filament", comment: "Printer stage"),
        25: String(localized: "Calibrating motor noise", comment: "Printer stage"),
        26: String(localized: "Paused: AMS offline", comment: "Printer stage"),
        27: String(localized: "Paused: heatbreak fan too slow", comment: "Printer stage"),
        28: String(localized: "Paused: chamber temperature fault", comment: "Printer stage"),
        29: String(localized: "Cooling chamber", comment: "Printer stage"),
        30: String(localized: "Paused by G-code", comment: "Printer stage"),
        31: String(localized: "Motor noise showoff", comment: "Printer stage"),
        32: String(localized: "Paused: nozzle clumping", comment: "Printer stage"),
        33: String(localized: "Paused: cutter error", comment: "Printer stage"),
        34: String(localized: "Paused: first layer error", comment: "Printer stage"),
        35: String(localized: "Paused: nozzle clog", comment: "Printer stage"),
        36: String(localized: "Measuring motion precision", comment: "Printer stage"),
        37: String(localized: "Enhancing motion precision", comment: "Printer stage"),
        38: String(localized: "Measuring motion accuracy", comment: "Printer stage"),
        39: String(localized: "Calibrating nozzle offset", comment: "Printer stage"),
        40: String(localized: "High-temperature bed leveling", comment: "Printer stage"),
        41: String(localized: "Checking quick-release lever", comment: "Printer stage"),
        42: String(localized: "Checking door and top cover", comment: "Printer stage"),
        43: String(localized: "Calibrating laser", comment: "Printer stage"),
        44: String(localized: "Checking platform", comment: "Printer stage"),
        45: String(localized: "Checking BirdsEye camera", comment: "Printer stage"),
        46: String(localized: "Calibrating BirdsEye camera", comment: "Printer stage"),
        47: String(localized: "Bed leveling, phase 1", comment: "Printer stage"),
        48: String(localized: "Bed leveling, phase 2", comment: "Printer stage"),
        49: String(localized: "Heating chamber", comment: "Printer stage"),
        50: String(localized: "Cooling heatbed", comment: "Printer stage"),
        51: String(localized: "Printing calibration lines", comment: "Printer stage"),
        52: String(localized: "Checking material", comment: "Printer stage"),
        53: String(localized: "Calibrating Live View camera", comment: "Printer stage"),
        54: String(localized: "Waiting for heatbed temperature", comment: "Printer stage"),
        55: String(localized: "Checking material position", comment: "Printer stage"),
        56: String(localized: "Calibrating cutter offset", comment: "Printer stage"),
        57: String(localized: "Measuring surface", comment: "Printer stage"),
        58: String(localized: "Thermal preconditioning", comment: "Printer stage"),
        59: String(localized: "Homing blade holder", comment: "Printer stage"),
        60: String(localized: "Calibrating camera offset", comment: "Printer stage"),
        61: String(localized: "Calibrating blade holder", comment: "Printer stage"),
        62: String(localized: "Hotend pick-and-place test", comment: "Printer stage"),
        63: String(localized: "Waiting for chamber temperature", comment: "Printer stage"),
        64: String(localized: "Preparing hotend", comment: "Printer stage"),
        65: String(localized: "Calibrating clumping detection", comment: "Printer stage"),
        66: String(localized: "Purifying chamber air", comment: "Printer stage"),
        67: String(localized: "Measuring rotary attachment", comment: "Printer stage"),
        68: String(localized: "Moving to purge chute", comment: "Printer stage"),
        69: String(localized: "Cooling nozzle", comment: "Printer stage"),
        70: String(localized: "Centering toolhead", comment: "Printer stage"),
        71: String(localized: "Arc fitting", comment: "Printer stage"),
        72: String(localized: "Detecting hotend type", comment: "Printer stage"),
        73: String(localized: "Detecting build plate alignment", comment: "Printer stage"),
        74: String(localized: "Checking heatbed for objects", comment: "Printer stage"),
        75: String(localized: "Checking heatbed underside", comment: "Printer stage"),
        76: String(localized: "Pre-extruding", comment: "Printer stage"),
        77: String(localized: "Preparing AMS", comment: "Printer stage"),
    ]
}

/// The last job the printer ran, kept so an idle printer can still say how its last print went.
struct JobRecord: Codable, Equatable, Identifiable, Sendable {
    var id: UUID = UUID()
    var name: String
    var startedAt: Date
    var endedAt: Date?
    var outcome: Outcome
    var totalLayers: Int
    var filamentColor: String?
    var filamentMaterial: String?
    /// The printer's task id, to tell two jobs with the same name apart.
    var taskID: String?
    /// Which printer ran it.
    var printerSerial: String?

    enum Outcome: String, Codable, Sendable {
        case running, finished, failed, canceled
        /// The app wasn't running when it ended, so how it ended isn't known.
        case ended

        var title: String {
            switch self {
            case .running: return String(localized: "In progress", comment: "How a print ended")
            case .finished: return String(localized: "Finished", comment: "How a print ended")
            case .failed: return String(localized: "Failed", comment: "How a print ended")
            case .canceled: return String(localized: "Canceled", comment: "How a print ended")
            case .ended: return String(localized: "Ended", comment: "How a print ended; the app wasn't running to see how")
            }
        }
    }

    var duration: TimeInterval? { endedAt.map { $0.timeIntervalSince(startedAt) } }
}
