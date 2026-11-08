import Foundation

/// The printer sends one full `print` report after `pushall`, then only the fields that changed.
/// `ReportDocument` keeps the merged picture.
struct ReportDocument {
    private(set) var print: [String: Any] = [:]
    /// From `get_version`: module name → (product name, software version).
    private(set) var modules: [String: (product: String, version: String)] = [:]
    private(set) var hasFullReport = false

    /// Merges one message from `device/<serial>/report`. Returns true if the print state changed.
    @discardableResult
    mutating func apply(_ message: [String: Any]) -> Bool {
        var changed = false
        if let p = message["print"] as? [String: Any] {
            let command = p["command"] as? String
            // Command acknowledgements echo the request back; only status pushes describe the printer.
            if command == nil || command == "push_status" {
                if (p["msg"] as? Int) == 0, p["gcode_state"] != nil {
                    // A full report replaces everything, so unplugged AMS units and cleared errors go away.
                    hasFullReport = true
                    print = p
                } else {
                    print = Self.merge(print, p)
                }
                changed = true
            }
        }
        if let info = message["info"] as? [String: Any], info["command"] as? String == "get_version",
           let list = info["module"] as? [[String: Any]] {
            for m in list {
                guard let name = m["name"] as? String else { continue }
                modules[name] = ((m["product_name"] as? String) ?? "", (m["sw_ver"] as? String) ?? "")
            }
            changed = true
        }
        return changed
    }

    mutating func reset() {
        print = [:]
        modules = [:]
        hasFullReport = false
    }

    /// Recursive merge. Arrays of objects carrying an `id` (or, for `lights_report`, a `node`) merge entry by
    /// entry; other arrays are replaced.
    static func merge(_ base: [String: Any], _ delta: [String: Any]) -> [String: Any] {
        var out = base
        for (key, new) in delta {
            let idKey = key == "lights_report" ? "node" : "id"
            if let newDict = new as? [String: Any], let oldDict = out[key] as? [String: Any] {
                out[key] = merge(oldDict, newDict)
            } else if let newArray = new as? [[String: Any]], let oldArray = out[key] as? [[String: Any]],
                      !newArray.isEmpty, newArray.allSatisfy({ $0[idKey] != nil }) {
                var merged = oldArray
                for item in newArray {
                    let id = "\(item[idKey]!)"
                    if let i = merged.firstIndex(where: { "\($0[idKey] ?? "")" == id }) {
                        merged[i] = merge(merged[i], item)
                    } else {
                        merged.append(item)
                    }
                }
                out[key] = merged
            } else {
                out[key] = new
            }
        }
        return out
    }
}

/// Turns the merged report into a `PrinterStatus`.
enum ReportParser {
    static func status(from doc: ReportDocument, hms: HMSCatalog) -> PrinterStatus {
        let p = doc.print
        var s = PrinterStatus()
        s.updatedAt = Date()
        s.state = JobState(gcodeState: string(p["gcode_state"]) ?? "IDLE")
        s.stageID = int(p["stg_cur"]) ?? int((p["stage"] as? [String: Any])?["_id"])
        if s.stageID == 0, string(p["print_type"]) == "idle", !s.state.isActive { s.stageID = 255 }
        s.taskID = [string(p["task_id"]), string(p["subtask_id"])].compactMap { $0 }.first { !$0.isEmpty && $0 != "0" }
        s.jobName = cleanJobName(string(p["subtask_name"]) ?? "", file: string(p["gcode_file"]) ?? "")
        s.progress = max(0, min(100, int(p["mc_percent"]) ?? 0))
        s.remainingMinutes = max(0, int(p["mc_remaining_time"]) ?? 0)
        s.layer = int(p["layer_num"]) ?? 0
        s.totalLayers = int(p["total_layer_num"]) ?? 0

        // Temperatures: the newer `device` block packs current (low 16 bits) and target (high 16 bits).
        let device = p["device"] as? [String: Any]
        let extruders = extruders(device)
        if let packed = int(extruders.byID[0]?["temp"]) {
            s.nozzle = unpack(packed)
            if let left = int(extruders.byID[1]?["temp"]) {
                s.secondNozzle = unpack(left)
                s.leftNozzleInUse = extruders.current == 1
            }
        } else {
            s.nozzle = Temperature(current: double(p["nozzle_temper"]) ?? 0, target: double(p["nozzle_target_temper"]) ?? 0)
        }
        if let packed = int(((device?["bed"] as? [String: Any])?["info"] as? [String: Any])?["temp"]) ?? int(device?["bed_temp"]) {
            s.bed = unpack(packed)
        } else {
            s.bed = Temperature(current: double(p["bed_temper"]) ?? 0, target: double(p["bed_target_temper"]) ?? 0)
        }
        if let packed = int(((device?["ctc"] as? [String: Any])?["info"] as? [String: Any])?["temp"]) {
            let chamber = unpack(packed)
            s.chamber = chamber.current
            s.chamberTarget = chamber.target
        } else if let t = double((p["info"] as? [String: Any])?["temp"]) ?? double(p["chamber_temper"]) {
            s.chamber = t
            // The X1E's heater target, in the older report format (Bambu Studio's DevChamber reads it too).
            s.chamberTarget = double(p["ctt"])
        }
        if let c = s.chamber, c <= 0 || c > 120 { s.chamber = nil; s.chamberTarget = nil }

        // Fans: the `airduct` parts already report percent; the legacy fields are 0-15. Bambu Studio names a part
        // by bits 4-11 of its id: 1 part cooling, 2 aux, 3 exhaust; the H3C adds 10 (left aux) and 15 (chamber).
        if let parts = (device?["airduct"] as? [String: Any])?["parts"] as? [[String: Any]], !parts.isEmpty {
            for part in parts {
                guard let id = int(part["id"]), let state = int(part["state"]) else { continue }
                let pct = max(0, min(100, state & 0xFF))
                switch (id >> 4) & 0xFF {
                case 1: s.partFan = pct
                case 2: s.auxFan = pct
                case 3: s.exhaustFan = pct
                case 10: s.leftAuxFan = pct
                case 15: s.circulationFan = pct
                default: break
                }
            }
        } else {
            // Every model sends all three, "0" for fans it doesn't have (an A1 has no aux or chamber fan); the
            // printer's model drops those (`PrinterStatus.fit(to:)`), so a stopped fan that exists still shows.
            s.partFan = fan(p["cooling_fan_speed"])
            s.auxFan = fan(p["big_fan1_speed"])
            s.exhaustFan = fan(p["big_fan2_speed"])
        }
        if s.partFan == nil { s.partFan = fan(p["cooling_fan_speed"]) }
        s.heatbreakFan = fan(p["heatbreak_fan_speed"])

        s.speed = SpeedLevel(rawValue: int(p["spd_lvl"]) ?? 2) ?? .standard
        if let lights = p["lights_report"] as? [[String: Any]],
           let chamber = lights.first(where: { string($0["node"]) == "chamber_light" }) {
            s.chamberLightOn = string(chamber["mode"]) != "off"
        }
        // The door, only from printers that say they have a door sensor (`fun` bit 12, as Bambu Studio checks):
        // others leave the bit at 0, which would read as a door that's always closed.
        if let fun = string(p["fun"]).flatMap({ UInt64($0, radix: 16) }), fun & (1 << 12) != 0,
           let stat = string(p["stat"]).flatMap({ UInt64($0, radix: 16) }) {
            s.doorOpen = stat & (1 << 23) != 0
        }
        if let wifi = string(p["wifi_signal"]), let dbm = Int(wifi.replacingOccurrences(of: "dBm", with: "")) {
            s.wifiSignal = dbm
        }
        if let fun = string(p["fun"]), let bits = UInt64(fun, radix: 16) {
            s.controlsLocked = bits & 0x2000_0000 != 0
        }
        s.hasSDCard = p["sdcard"] as? Bool
        s.plateIndex = int(p["plate_idx"])
        if let ipcam = p["ipcam"] as? [String: Any], let url = string(ipcam["rtsp_url"]) {
            s.liveViewEnabled = url != "disable" && !url.isEmpty
        }
        // The nozzle on the printing extruder: its `id` is the extruder's. The H3C also lists the hotends in its rack
        // (ids 16 and up), in rack order, so the first entry isn't necessarily the mounted one.
        if let nozzles = (device?["nozzle"] as? [String: Any])?["info"] as? [[String: Any]],
           let n = nozzles.first(where: { int($0["id"]) == extruders.current }) ?? nozzles.first {
            s.nozzleDiameter = double(n["diameter"])
            s.nozzleType = string(n["type"])
        } else {
            s.nozzleDiameter = double(p["nozzle_diameter"])
            s.nozzleType = string(p["nozzle_type"])
        }
        if let upgrade = p["upgrade_state"] as? [String: Any], int(upgrade["new_version_state"]) == 1 {
            s.updateAvailable = true
        }
        s.firmware = doc.modules["ota"]?.version

        parseFilament(p, device: device, doc: doc, into: &s)
        s.issues = issues(p, hms: hms, state: s.state)
        s.canceled = s.state == .failed && wasCanceled(doc)
        return s
    }

    // MARK: - Filament

    private static func parseFilament(_ p: [String: Any], device: [String: Any]?, doc: ReportDocument, into s: inout PrinterStatus) {
        let amsRoot = p["ams"] as? [String: Any]
        let extruders = extruders(device)
        let units = (amsRoot?["ams"] as? [[String: Any]]) ?? []
        let existBits = string(amsRoot?["tray_exist_bits"]).flatMap { UInt64($0, radix: 16) }
        let rfidBits = string(amsRoot?["tray_is_bbl_bits"]).flatMap { UInt64($0, radix: 16) }
        s.ams = units.compactMap { u -> AMSUnit? in
            guard let id = int(u["id"]) else { return nil }
            let type = amsType(id: id, info: string(u["info"]), modules: doc.modules)
            let kind = type.kind
            let trays = (u["tray"] as? [[String: Any]]) ?? []
            let spools = trays.compactMap { t -> Spool? in
                guard let slot = int(t["id"]) else { return nil }
                var spool = spoolFrom(t, id: type.spoolID(unit: id, slot: slot), unit: id, slot: slot)
                if let bit = type.trayBit(unit: id, slot: slot), (0..<64).contains(bit) {
                    if let existBits, existBits & (1 << UInt64(bit)) == 0 { spool.isLoaded = false }
                    // Only ever narrows the tag check: the P2S sets every bit, Bambu spool or not.
                    if let rfidBits, rfidBits & (1 << UInt64(bit)) == 0 { spool.isBambu = false }
                }
                describe(&spool)
                return spool
            }.sorted { $0.slot < $1.slot }
            // The AMS 3H measures each of its drying zones instead of the whole unit; show the dampest one.
            let zones = (u["zone_info"] as? [[String: Any]]) ?? []
            let sensor = u["humidity_raw"] != nil || u["humidity"] != nil || zones.isEmpty
                ? u : zones.max { (int($0["humidity_raw"]) ?? -1) < (int($1["humidity_raw"]) ?? -1) } ?? u
            var humidity = int(sensor["humidity_raw"])
            if let h = humidity, h < 1 || h > 100 { humidity = nil }
            var level = int(sensor["humidity"])
            if let l = level, l < 1 || l > 5 { level = nil }
            var temp = double(sensor["temp"])
            if let t = temp, t <= 0 || t > 100 { temp = nil }
            // The AMS Lite has no humidity or temperature sensor; it reports "5" (the driest level) and "0.0".
            if kind == .lite { humidity = nil; level = nil; temp = nil }
            return AMSUnit(id: id, kind: kind, humidity: humidity, humidityLevel: level.map { 6 - $0 }, temperature: temp,
                           dryingMinutesLeft: int(u["dry_time"]) ?? 0, spools: spools)
        }.sorted { $0.id < $1.id }

        // The external spool of the printing nozzle: Bambu Studio numbers the virtual slots 255 down, one per extruder
        // (255 right, 254 left on an H2D) or, on the H3C, one per hotend rack position (255 - addr_id, 247-255).
        let slots = p["vir_slot"] as? [[String: Any]]
        let nozzles = ((device?["nozzle"] as? [String: Any])?["info"] as? [[String: Any]]) ?? []
        let mountedAddr = int(nozzles.first { int($0["id"]) == extruders.current }?["addr_id"])
        let wanted = 255 - (mountedAddr ?? extruders.current)
        let virtual = slots?.first { int($0["id"]) == wanted } ?? slots?.first ?? (p["vt_tray"] as? [String: Any])
        if let v = virtual {
            var ext = spoolFrom(v, id: Spool.externalID, unit: 255, slot: 0)
            ext.isLoaded = !(string(v["tray_type"]) ?? "").isEmpty
            // Nothing weighs the spool on the external holder; its `remain` is a placeholder.
            ext.remain = nil
            describe(&ext)
            s.externalSpool = ext
        }

        // Active slot: the printing extruder's `snow` (unit << 8 | slot) on newer firmware, else `tray_now`.
        if let snow = int(extruders.byID[extruders.current]?["snow"]), snow != 0xFFFF {
            let unit = snow >> 8, slot = snow & 0xFF
            if (247...255).contains(unit) {
                s.activeSpoolID = slot == 255 ? nil : Spool.externalID
            } else if let spool = s.ams.first(where: { $0.id == unit })?.spools.first(where: { $0.slot == slot }) {
                s.activeSpoolID = spool.id
            } else if unit >= 128 {
                s.activeSpoolID = unit
            } else {
                s.activeSpoolID = unit * 4 + (slot & 0x3)
            }
        } else if let now = int(amsRoot?["tray_now"]) {
            switch now {
            case 255: s.activeSpoolID = nil
            case 254: s.activeSpoolID = Spool.externalID
            default: s.activeSpoolID = now
            }
        }
        // Only meaningful while filament is actually in use.
        if !s.state.isActive && s.activeSpoolID != nil && s.activeSpool?.isLoaded != true { s.activeSpoolID = nil }
    }

    private static func spoolFrom(_ t: [String: Any], id: Int, unit: Int, slot: Int) -> Spool {
        let material = string(t["tray_type"]) ?? ""
        let state = int(t["state"])
        var loaded = !material.isEmpty
        if let state, state > 3 { loaded = state & 0x01 != 0 && state & 0x08 != 0 || state == 9 }
        let color = string(t["tray_color"]) ?? "00000000"
        let colors = (t["cols"] as? [String])?.filter { $0.count >= 6 } ?? []
        var remain = int(t["remain"])
        if let r = remain, r < 0 || r > 100 { remain = nil }
        let tag = string(t["tag_uid"]) ?? ""
        var weight = int(t["tray_weight"])
        if let w = weight, w <= 0 || w > 10_000 { weight = nil }
        var spool = Spool(id: id, unit: unit, slot: slot, material: material, name: string(t["tray_sub_brands"]) ?? "",
                          color: color, colors: colors.isEmpty ? [color] : colors, remain: remain, weight: weight, isLoaded: loaded,
                          isBambu: !tag.isEmpty && !tag.allSatisfy { $0 == "0" },
                          preset: string(t["tray_info_idx"]).flatMap { $0.isEmpty ? nil : $0 })
        spool.colorName = FilamentCatalog.colorName(tagCode: string(t["tray_id_name"]) ?? "", preset: spool.preset, hex: color)
        if let low = int(t["nozzle_temp_min"]), let high = int(t["nozzle_temp_max"]), low > 0, high >= low {
            spool.nozzleRange = low...high
        }
        return spool
    }

    /// Fills in the maker and product from the slot's preset once it's known whether the spool has a Bambu tag.
    /// A spool without one is whatever the slot was set to on the printer or in Bambu Studio.
    private static func describe(_ spool: inout Spool) {
        guard spool.isLoaded else {
            spool = Spool(id: spool.id, unit: spool.unit, slot: spool.slot, material: "", name: "", color: spool.color,
                          colors: spool.colors, remain: nil, isLoaded: false, isBambu: false)
            return
        }
        let preset = spool.preset.flatMap(FilamentCatalog.preset)
        spool.brand = spool.isBambu ? "Bambu Lab" : preset?.vendor
        if spool.name.isEmpty, let product = preset?.product, product.caseInsensitiveCompare(spool.material) != .orderedSame {
            spool.name = product
        }
    }

    /// Bambu Studio's AMS type (`DevAmsType`, bits 0-3 of the unit's `info`), which also places the unit's slots in
    /// `tray_exist_bits`.
    private enum AMSType {
        case ams, lite, pro2, ht
        /// The A2L's AMS Lite (type 5).
        case liteMixed
        /// The H3C's AMS 3H (type 6, ZMULTI_A).
        case ams3h
        /// The H3C's AMS-B (type 7, ZMULTI_B), for hotend rack B. Not seen in a report yet.
        case amsB

        var kind: AMSKind {
            switch self {
            case .ams: return .ams
            case .lite, .liteMixed: return .lite
            case .pro2: return .pro2
            case .ht: return .ht
            case .ams3h: return .ams3h
            case .amsB: return .amsB
            }
        }

        /// The slot's bit in `tray_exist_bits` and `tray_is_bbl_bits` (`DevAms::GetTrayId`).
        func trayBit(unit: Int, slot: Int) -> Int? {
            switch self {
            case .ams, .lite, .pro2: return unit * 4 + slot
            case .ht: return unit >= 128 ? 16 + (unit - 128) + slot : nil
            case .liteMixed: return 24 + slot
            case .ams3h: return 28 + slot
            case .amsB: return 37 + slot
            }
        }

        /// The app's spool id: unit * 4 + slot for the four-slot units, the unit id for an AMS HT (one slot), and
        /// past both ranges for the H3C's rack AMS.
        func spoolID(unit: Int, slot: Int) -> Int {
            switch self {
            case .ht where unit >= 128: return unit
            case .ams3h: return Spool.ams3hBase + slot
            case .amsB: return Spool.amsBBase + slot
            default: return unit * 4 + slot
            }
        }
    }

    private static func amsType(id: Int, info: String?, modules: [String: (product: String, version: String)]) -> AMSType {
        let type = info.flatMap { UInt64($0, radix: 16) }.map { $0 & 0xF }
        if type == 6 || modules["n10a/\(id)"] != nil { return .ams3h }
        if type == 7 { return .amsB }
        if type == 5 { return .liteMixed }
        if id >= 128 || modules["n3s/\(id)"] != nil { return .ht }
        if modules["n3f/\(id)"] != nil { return .pro2 }
        if modules["ams_f1/\(id)"] != nil { return .lite }
        if modules["ams/\(id)"] != nil { return .ams }
        switch type {
        case 2: return .lite
        case 3: return .pro2
        case 4: return .ht
        default: return .ams
        }
    }

    // MARK: - Issues

    private static func issues(_ p: [String: Any], hms catalog: HMSCatalog, state: JobState) -> [PrinterIssue] {
        var out: [PrinterIssue] = []
        for entry in (p["hms"] as? [[String: Any]]) ?? [] {
            guard let attr = int(entry["attr"]), let code = int(entry["code"]) else { continue }
            let a = UInt32(truncatingIfNeeded: attr), c = UInt32(truncatingIfNeeded: code)
            if c & 0xFFFF < 0x4000 && c >> 16 == 0 { continue }
            let key = String(format: "%08X%08X", a, c)
            let pretty = String(format: "HMS_%04X_%04X_%04X_%04X", a >> 16, a & 0xFFFF, c >> 16, c & 0xFFFF)
            let severity = IssueSeverity(rawValue: Int(c >> 16)) ?? .common
            out.append(PrinterIssue(id: pretty, severity: severity,
                                    message: catalog.hmsText(key) ?? String(localized: "The printer reported \(pretty).", comment: "An HMS code"),
                                    wikiURL: HMSCatalog.wikiURL(for: pretty)))
        }
        if let error = int(p["print_error"]), error != 0 {
            let e = UInt32(truncatingIfNeeded: error)
            let key = String(format: "%08X", e)
            // 0300_400C and 0500_400E are the printer's echo of a user cancel, not a fault.
            if key != "0300400C" && key != "0500400E" {
                let pretty = String(format: "%04X_%04X", e >> 16, e & 0xFFFF)
                let level = (e & 0xF000) >> 12
                let severity: IssueSeverity = level == 0x4 ? .fatal : level == 0x8 ? .serious : .common
                if !out.contains(where: { $0.id.hasSuffix(pretty) }) {
                    out.append(PrinterIssue(id: pretty, severity: severity,
                                            message: catalog.errorText(key) ?? String(localized: "The printer reported error \(pretty).", comment: "An error code"),
                                            wikiURL: HMSCatalog.wikiURL(for: pretty)))
                }
            }
        }
        return out.sorted { $0.severity < $1.severity }
    }

    /// True when `print_error` is the code for a job the user cancelled.
    static func wasCanceled(_ doc: ReportDocument) -> Bool {
        guard let e = int(doc.print["print_error"]) else { return false }
        let key = String(format: "%08X", UInt32(truncatingIfNeeded: e))
        return key == "0300400C" || key == "0500400E"
    }

    // MARK: - Helpers

    /// Bambu Studio names jobs after the 3MF; strip path and extensions for display.
    static func cleanJobName(_ name: String, file: String) -> String {
        var n = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if n.isEmpty {
            n = (file as NSString).lastPathComponent
            if n.hasPrefix("plate_") { n = "" }
        }
        for ext in [".gcode.3mf", ".3mf", ".gcode"] where n.lowercased().hasSuffix(ext) {
            n = String(n.dropLast(ext.count))
        }
        // Bambu Studio turns spaces into underscores when it names the job after the project file.
        if !n.contains(" ") { n = n.replacingOccurrences(of: "_", with: " ") }
        return n
    }

    /// The newer firmware's `device.extruder` block. Bambu Studio keys each extruder by its `id` (0 is the right
    /// nozzle, 1 the left on two-nozzle models), not by its place in the list; bits 4-7 of `state` say which one
    /// is printing.
    private static func extruders(_ device: [String: Any]?) -> (byID: [Int: [String: Any]], current: Int) {
        let root = device?["extruder"] as? [String: Any]
        var byID: [Int: [String: Any]] = [:]
        for (i, e) in ((root?["info"] as? [[String: Any]]) ?? []).enumerated() { byID[int(e["id"]) ?? i] = e }
        let current = int(root?["state"]).map { ($0 >> 4) & 0xF } ?? 0
        return (byID, byID[current] == nil ? 0 : current)
    }

    private static func unpack(_ packed: Int) -> Temperature {
        // Plain values (no target packed in) stay below 0x10000; a heater that's off reports target 0.
        Temperature(current: Double(packed & 0xFFFF), target: Double((packed >> 16) & 0xFFFF))
    }

    private static func fan(_ value: Any?) -> Int? {
        guard let raw = int(value) else { return nil }
        if raw <= 15 { return Int((Double(raw) / 15 * 100).rounded()) }
        return min(100, Int((Double(raw) / 255 * 100).rounded()))
    }

    static func int(_ value: Any?) -> Int? {
        switch value {
        case let v as Int: return v
        case let v as Double: return Int(exactly: v.rounded(.towardZero))
        case let v as NSNumber: return v.intValue
        // `Int(Double)` traps on "nan", "inf" or anything past Int's range; `exactly:` returns nil instead.
        case let v as String: return Int(v) ?? Double(v).flatMap { Int(exactly: $0.rounded(.towardZero)) }
        default: return nil
        }
    }

    /// Finite numbers only. `Double("nan")` parses, slips past every range check (comparisons with NaN are
    /// false) and makes JSONEncoder refuse the whole widget snapshot.
    static func double(_ value: Any?) -> Double? {
        let result: Double?
        switch value {
        case let v as Double: result = v
        case let v as Int: result = Double(v)
        case let v as NSNumber: result = v.doubleValue
        case let v as String: result = Double(v)
        default: result = nil
        }
        return result.flatMap { $0.isFinite ? $0 : nil }
    }

    static func string(_ value: Any?) -> String? {
        switch value {
        case let v as String: return v
        case let v as NSNumber: return v.stringValue
        default: return nil
        }
    }
}
