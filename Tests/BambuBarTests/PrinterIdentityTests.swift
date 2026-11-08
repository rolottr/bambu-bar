import XCTest

/// One printer is one serial, in uppercase, from wherever it's learned: its network announcement, the saved
/// printers, a slicer. And a name Bambu Bar made up gives way to the one the printer announces.
final class PrinterIdentityTests: XCTestCase {
    private func announcement(serial: String, name: String? = "Workshop P2S") -> String {
        var lines = ["NOTIFY * HTTP/1.1", "HOST: 239.255.255.250:1990", "NT: urn:bambulab-com:device:3dprinter:1",
                     "USN: \(serial)", "Location: 192.168.1.20", "DevModel.bambu.com: N7"]
        if let name { lines.append("DevName.bambu.com: \(name)") }
        return lines.joined(separator: "\r\n") + "\r\n\r\n"
    }

    // MARK: - Discovery

    func testLowercaseAnnouncementIsTheSamePrinter() throws {
        let lower = try XCTUnwrap(PrinterDiscovery.parse(announcement(serial: "22ef00000000001"), sender: "192.168.1.20"))
        let upper = try XCTUnwrap(PrinterDiscovery.parse(announcement(serial: "22EF00000000001"), sender: "192.168.1.20"))
        XCTAssertEqual(lower.serial, "22EF00000000001")
        XCTAssertEqual(lower.id, upper.id)
        XCTAssertEqual(lower.name, "Workshop P2S")
        XCTAssertEqual(lower.host, "192.168.1.20")
    }

    func testUnnamedAnnouncementIsNamedAfterTheUppercaseSerial() throws {
        let printer = try XCTUnwrap(PrinterDiscovery.parse(announcement(serial: "22ef00000000001", name: nil), sender: "10.0.0.2"))
        XCTAssertEqual(printer.name, "22EF00000000001")
    }

    func testOtherDevicesAreIgnored() {
        XCTAssertNil(PrinterDiscovery.parse("NOTIFY * HTTP/1.1\r\nNT: upnp:rootdevice\r\nUSN: uuid:abc\r\n\r\n", sender: "10.0.0.3"))
        XCTAssertNil(PrinterDiscovery.parse("NOTIFY * HTTP/1.1\r\nNT: urn:bambulab-com:device:3dprinter:1\r\nUSN: \r\n\r\n", sender: "10.0.0.3"))
    }

    // MARK: - Saved printers

    private func config(_ serial: String, _ name: String = "Printer", host: String = "10.0.0.1") -> PrinterConfig {
        PrinterConfig(host: host, serial: serial, name: name, modelCode: "N7")
    }

    func testPrinterSavedTwiceIsLoadedOnce() {
        let saved = [config("22EF00000000001", "Workshop", host: "10.0.0.1"), config("01P00A000000002"),
                     config("22EF00000000001", "Workshop copy", host: "10.0.0.9"), config("22ef00000000001", "lower")]
        let loaded = PrinterConfig.deduplicated(saved)
        XCTAssertEqual(loaded.map(\.serial), ["22EF00000000001", "01P00A000000002"])
        XCTAssertEqual(loaded.first?.name, "Workshop", "the first copy wins")
        XCTAssertEqual(loaded.first?.host, "10.0.0.1")
    }

    func testSavedPrintersWithoutDuplicatesAreUnchanged() {
        let saved = [config("22EF00000000001"), config("01P00A000000002")]
        XCTAssertEqual(PrinterConfig.deduplicated(saved), saved)
    }

    func testLowercaseSavedSerialIsUppercased() {
        XCTAssertEqual(PrinterConfig.deduplicated([config("22ef00000000001")]).map(\.serial), ["22EF00000000001"])
    }

    // MARK: - Camera switch

    func testPrinterSavedByAnEarlierVersionHasItsCameraOff() throws {
        let json = #"[{"host":"10.0.0.1","serial":"22EF00000000001","name":"Workshop","modelCode":"N7"}]"#
        let configs = try JSONDecoder().decode([PrinterConfig].self, from: Data(json.utf8))
        XCTAssertNil(configs.first?.cameraEnabled)
        XCTAssertNotEqual(configs.first?.cameraEnabled, true, "off unless turned on")
    }

    func testCameraSwitchIsSaved() throws {
        var config = config("22EF00000000001")
        config.cameraEnabled = true
        let loaded = try JSONDecoder().decode(PrinterConfig.self, from: JSONEncoder().encode(config))
        XCTAssertEqual(loaded.cameraEnabled, true)
        XCTAssertEqual(PrinterConfig.deduplicated([loaded]).first?.cameraEnabled, true)
    }

    // MARK: - Models (PR #5)

    /// Setups before 1.2 saved "N7" (P2S) for a printer typed in by hand; the serial tells the truth.
    func testSerialPrefixWinsOverAStaleModelCode() {
        XCTAssertEqual(PrinterModel.find(code: "N7", serial: "039F00000000008")?.name, "A1")
        XCTAssertEqual(PrinterConfig(host: "", serial: "039F00000000008", name: "", modelCode: "N7").modelName, "A1")
        XCTAssertEqual(PrinterModel.find(code: "N7", serial: "01PF00000000002")?.name, "P1S")
    }

    func testModelCodeStillDecidesForUnknownSerials() {
        XCTAssertEqual(PrinterModel.find(code: "N2S", serial: "ZZZ00EMULATOR0")?.name, "A1")
        XCTAssertEqual(PrinterModel.find(code: "C13")?.name, "X1E")
        XCTAssertEqual(PrinterModel.find(code: "BL-P003")?.name, "X1E", "aliases still work")
        XCTAssertNil(PrinterModel.find(code: "", serial: "ZZZ00EMULATOR0"))
    }

    func testEveryModelIsFoundByItsOwnSerialAndCode() {
        for model in PrinterModel.all where !model.serialPrefix.isEmpty {
            XCTAssertEqual(PrinterModel.find(code: model.code, serial: model.serialPrefix + "000000000001"), model)
            XCTAssertEqual(PrinterModel.find(code: "", serial: model.serialPrefix + "000000000001"), model)
        }
    }

    /// The H3C has its own render (the AMS 3H hood, nine spools); the H2 family keeps the wide one.
    func testH3CHasItsOwnShape() {
        XCTAssertEqual(PrinterShape(model: "H3C"), .h3c)
        XCTAssertEqual(PrinterShape(model: "N10"), .h3c)
        XCTAssertEqual(PrinterShape(model: "N10-V2"), .h3c)
        XCTAssertEqual(PrinterShape(model: "H2C"), .wide)
        XCTAssertEqual(PrinterShape.h3c.assetName, "h3c")
        XCTAssertTrue(PrinterShape.h3c.isWide)
    }

    // MARK: - Names

    func testPlaceholderName() {
        XCTAssertEqual(PrinterConfig.placeholderName(serial: "22EF00000000495", modelCode: "N7"), "P2S 495")
        // No model code yet: the serial's prefix tells the model.
        XCTAssertEqual(PrinterConfig.placeholderName(serial: "22EF00000000495", modelCode: ""), "P2S 495")
    }

    func testMadeUpNamesGiveWayToTheAnnouncedOne() {
        XCTAssertTrue(config("22EF00000000495", "22EF00000000495").hasGeneratedName)
        XCTAssertTrue(config("22EF00000000495", "22ef00000000495").hasGeneratedName, "the serial in another case")
        XCTAssertTrue(config("22EF00000000495", "P2S 495").hasGeneratedName)
        XCTAssertTrue(PrinterConfig(host: "", serial: "22EF00000000495", name: "P2S 495", modelCode: "").hasGeneratedName)
    }

    func testNamesChosenByHandAreKept() {
        XCTAssertFalse(config("22EF00000000495", "Workshop").hasGeneratedName)
        XCTAssertFalse(config("22EF00000000495", "P2S 496").hasGeneratedName)
        XCTAssertFalse(config("22EF00000000495", "P2S").hasGeneratedName)
    }
}
