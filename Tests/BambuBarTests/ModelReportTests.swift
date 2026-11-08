import XCTest

/// Every Bambu Lab model's report, as the mock fleet sends it (ModelReports, written by
/// `python3 scripts/fleet/fleet.py --dump Tests/BambuBarTests/ModelReports`), through the app's parser and the
/// model's capabilities: the app shows what the printer has and nothing it doesn't, placeholders included (a P1S
/// sends a chamber temperature of 5 without a sensor; an A1 sends "0" for fans it doesn't have).
final class ModelReportTests: XCTestCase {
    private static let folder = URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("ModelReports")

    private func reports() throws -> [(model: PrinterModel, status: PrinterStatus, print: [String: Any])] {
        let files = try FileManager.default.contentsOfDirectory(at: Self.folder, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }
        XCTAssertFalse(files.isEmpty, "no reports in \(Self.folder.path)")
        return try files.sorted { $0.path < $1.path }.map { file in
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any])
            let code = try XCTUnwrap(json["model"] as? String)
            let serial = try XCTUnwrap(json["serial"] as? String)
            let model = try XCTUnwrap(PrinterModel.find(code: code, serial: serial), "\(code) isn't in PrinterModel.all")
            XCTAssertEqual(model.code, code, "\(serial) found as \(model.name)")
            var doc = ReportDocument()
            doc.apply(["print": try XCTUnwrap(json["print"] as? [String: Any])])
            var status = ReportParser.status(from: doc, hms: .shared)
            status.fit(to: model)
            return (model, status, doc.print)
        }
    }

    func testEveryModelHasAReport() throws {
        let covered = Set(try reports().map(\.model.code))
        XCTAssertEqual(covered, Set(PrinterModel.all.map(\.code)), "a model without a fleet printer, or the other way round")
    }

    func testOnlyWhatTheModelHasShows() throws {
        for (model, s, _) in try reports() {
            let name = model.name
            XCTAssertEqual(s.chamber != nil, model.hasChamberSensor, "\(name): chamber temperature")
            XCTAssertEqual(s.chamberTarget != nil, model.chamberHeaterMax != nil, "\(name): chamber heater target")
            XCTAssertEqual(s.auxFan != nil, model.hasAuxFan, "\(name): aux fan")
            XCTAssertEqual(s.exhaustFan != nil, model.hasChamberFan, "\(name): chamber fan")
            XCTAssertNotNil(s.partFan, "\(name): part cooling fan")
            XCTAssertEqual(s.chamberLightOn != nil, model.hasChamberLight, "\(name): chamber light")
            XCTAssertEqual(s.secondNozzle != nil, model.nozzleCount == 2, "\(name): second nozzle")
            if !model.isEnclosed { XCTAssertNil(s.doorOpen, "\(name): door on an open printer") }
            for unit in s.ams where unit.kind == .lite {
                XCTAssertNil(unit.humidity, "\(name): AMS Lite humidity")
                XCTAssertNil(unit.humidityLevel, "\(name): AMS Lite humidity level")
                XCTAssertNil(unit.temperature, "\(name): AMS Lite temperature")
            }
            XCTAssertNotNil(s.externalSpool, "\(name): external spool (vt_tray or vir_slot)")
            XCTAssertGreaterThan(s.nozzle.target, 0, "\(name): printing nozzle target")
        }
    }

    /// The door shows only for printers that say they have a sensor (`fun` bit 12), as Bambu Studio checks.
    func testDoorFollowsTheSensorFlag() throws {
        for (model, s, print) in try reports() {
            let fun = (print["fun"] as? String).flatMap { UInt64($0, radix: 16) } ?? 0
            XCTAssertEqual(s.doorOpen != nil, fun & (1 << 12) != 0 && print["stat"] != nil, model.name)
        }
    }

    /// The X1E reports its chamber heater in the flat format: `chamber_temper` and `ctt`.
    func testX1EChamberTargetFromTheFlatFormat() throws {
        let x1e = try XCTUnwrap(try reports().first { $0.model.code == "C13" })
        XCTAssertNil(x1e.print["device"])
        XCTAssertNotNil(x1e.status.chamber)
        XCTAssertNotNil(x1e.status.chamberTarget)
    }

    /// A real H3C report (scripts/emulator/h3c-sample.json, idle, from ha-bambulab issue 2209): its AMS 3H has nine
    /// slots and measures humidity per drying zone, nine hotends sit in its rack, nine virtual slots, five fans.
    func testRealH3CReport() throws {
        let file = Self.folder.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("scripts/emulator/h3c-sample.json")
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any])
        var doc = ReportDocument()
        doc.apply(try XCTUnwrap(json["get_version"] as? [String: Any]))
        doc.apply(try XCTUnwrap(json["pushall"] as? [String: Any]))
        var s = ReportParser.status(from: doc, hms: .shared)
        s.fit(to: PrinterModel.find(code: "N10"))

        XCTAssertEqual(s.ams.count, 1)
        let unit = try XCTUnwrap(s.ams.first)
        XCTAssertEqual(unit.kind, .ams3h)
        XCTAssertEqual(unit.title, "AMS 3H")
        XCTAssertEqual(unit.spools.map(\.slot), Array(0...8))
        XCTAssertFalse(unit.spools.contains(where: \.isLoaded), "every slot is empty in this report")
        XCTAssertTrue(Set(unit.spools.map(\.id)).isDisjoint(with: [Spool.externalID] + Array(0..<16) + Array(128..<136)))
        // The dampest of its two zones: 46 %, level 1 of Bambu Studio's 5 (wettest) shown as 5, at 25.2 °C.
        XCTAssertEqual(unit.humidity, 46)
        XCTAssertEqual(unit.humidityLevel, 5)
        XCTAssertEqual(unit.temperature, 25.2)

        XCTAssertEqual(s.chamber, 22)
        XCTAssertEqual(s.doorOpen, false)
        XCTAssertEqual(s.chamberLightOn, false)
        XCTAssertEqual(s.nozzleType, "HS01")
        XCTAssertEqual(s.nozzleDiameter, 0.4)
        XCTAssertNil(s.secondNozzle)
        for (name, fan) in [("part", s.partFan), ("right aux", s.auxFan), ("left aux", s.leftAuxFan),
                            ("exhaust", s.exhaustFan), ("chamber", s.circulationFan)] {
            XCTAssertEqual(fan, 0, name)
        }
        let external = try XCTUnwrap(s.externalSpool)
        XCTAssertFalse(external.isLoaded)
        XCTAssertNil(s.activeSpoolID)
    }

    /// The fleet's H3C, printing from the AMS 3H's first slot: the slot comes from the extruder's `snow`
    /// (unit 32 << 8 | slot), the loaded slots from `tray_exist_bits` bits 28 and up.
    func testH3CPrintingFromTheAMS3H() throws {
        let h3c = try XCTUnwrap(try reports().first { $0.status.ams.first?.kind == .ams3h }).status
        let unit = try XCTUnwrap(h3c.ams.first)
        XCTAssertEqual(unit.spools.filter(\.isLoaded).map(\.slot), [0, 1, 2, 3])
        let active = try XCTUnwrap(h3c.activeSpool)
        XCTAssertEqual(active.slot, 0)
        XCTAssertEqual(active.material, "ABS")
        XCTAssertNotNil(h3c.leftAuxFan)
        XCTAssertNotNil(h3c.circulationFan)
    }

    /// The fleet's H3C with an AMS-B instead (reconstructed from Bambu Studio's source, no report yet): type 7, its
    /// slots at `tray_exist_bits` 37 and up, numbered 1, 2…, and no humidity or temperature it didn't send.
    func testH3CWithTheAMSB() throws {
        let h3c = try XCTUnwrap(try reports().first { $0.status.ams.first?.kind == .amsB }).status
        let unit = try XCTUnwrap(h3c.ams.first)
        XCTAssertEqual(unit.title, "AMS-B")
        XCTAssertEqual(unit.spools.filter(\.isLoaded).map(\.slot), [0, 2, 3, 5])
        XCTAssertEqual(unit.spools.map(\.label), (1...9).map(String.init))
        XCTAssertNil(unit.humidity)
        XCTAssertNil(unit.humidityLevel)
        XCTAssertNil(unit.temperature)
        let active = try XCTUnwrap(h3c.activeSpool)
        XCTAssertEqual(active.slot, 2)
        XCTAssertEqual(active.material, "PETG")
    }

    /// Numbers that aren't: "nan" and "inf" from a printer must not reach the widget snapshot (JSONEncoder
    /// refuses non-finite doubles) or crash the integer conversion.
    func testNonFiniteNumbersAreDropped() {
        XCTAssertNil(ReportParser.double("nan"))
        XCTAssertNil(ReportParser.double("inf"))
        XCTAssertNil(ReportParser.double(Double.infinity))
        XCTAssertEqual(ReportParser.double("38.4"), 38.4)
        XCTAssertNil(ReportParser.int("nan"))
        XCTAssertNil(ReportParser.int("1e300"))
        XCTAssertEqual(ReportParser.int("42"), 42)
        XCTAssertEqual(ReportParser.int("42.9"), 42)
    }
}
