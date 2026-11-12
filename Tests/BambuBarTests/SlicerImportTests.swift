import XCTest

/// One printer per serial, whatever case each slicer section spells it in, with every access code kept to try
/// (review finding P2: an uppercase stale code and a lowercase working one made two entries; the app kept the
/// stale one and the printer stayed offline).
final class SlicerImportTests: XCTestCase {
    private var files: [URL] = []

    override func tearDown() {
        files.forEach { try? FileManager.default.removeItem(at: $0) }
        files = []
        super.tearDown()
    }

    private func read(_ json: String, source: String = "Test") throws -> [SlicerPrinter] {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".conf")
        try Data(json.utf8).write(to: url)
        files.append(url)
        return SlicerImport.read(url, source: source)
    }

    func testSerialSpelledDifferentlyInEachSectionIsOnePrinter() throws {
        let rows = try read("""
        {"access_code": {"22EF00000000001": "STALE"},
         "user_access_code": {"22ef00000000001": "VALID"},
         "ip_address": {"22ef00000000001": "192.168.1.20"}}
        """)
        XCTAssertEqual(rows.count, 1)
        XCTAssertEqual(rows.first?.serial, "22EF00000000001")
        XCTAssertEqual(rows.first?.accessCodes, ["STALE", "VALID"], "both codes, the cloud's first, to try in turn")
        XCTAssertEqual(rows.first?.host, "192.168.1.20")
    }

    func testLowercaseKeysKeepTheirCodeAndAddress() throws {
        let rows = try read(#"{"user_access_code": {"22ef00000000001": "VALID"}, "ip_address": {"22ef00000000001": "10.0.0.7"}}"#)
        XCTAssertEqual(rows, [SlicerPrinter(serial: "22EF00000000001", accessCodes: ["VALID"], host: "10.0.0.7",
                                            name: nil, source: "Test")])
    }

    func testBothSpellingsInOneSectionKeepBothCodes() throws {
        let rows = try read(#"{"user_access_code": {"22ef00000000001": "LOWER", "22EF00000000001": "UPPER"}}"#)
        XCTAssertEqual(rows.count, 1)
        XCTAssertEqual(rows.first?.accessCodes, ["UPPER", "LOWER"], "the uppercase spelling first")
    }

    func testTheSameCodeTwiceIsTriedOnce() throws {
        let rows = try read(#"{"access_code": {"22EF00000000001": "SAME"}, "user_access_code": {"22ef00000000001": "SAME"}}"#)
        XCTAssertEqual(rows.first?.accessCodes, ["SAME"])
    }

    func testAddressOnlyUnderTheOtherSpellingIsFound() throws {
        // The scrambled user_access_dev_ip, as the mock fleet and Bambu Studio 2 write it, under a lowercase key.
        let rows = try read("""
        {"app": {"slicer_uuid": "daf7e1d0-0c1f-4bbc-b88f-309df40dab23"},
         "access_code": {"22EF00000000001": "STALE"},
         "user_access_code": {"22ef00000000001": "VALID"},
         "user_access_dev_ip": {"22ef00000000001": "\(SlicerImportTests.scrambled("127.0.0.1:18801", key: "daf7e1d0-0c1f-4bbc-b88f-309df40dab23"))"}}
        """)
        XCTAssertEqual(rows.first?.host, "127.0.0.1:18801")
        XCTAssertEqual(rows.first?.accessCodes, ["STALE", "VALID"])
    }

    func testPrintersWithoutACodeAreLeftOut() throws {
        XCTAssertEqual(try read(#"{"ip_address": {"22EF00000000001": "10.0.0.7"}, "user_access_code": {"22EF00000000002": " "}}"#), [])
    }

    func testChecksumLineAfterTheJSONIsIgnored() throws {
        let rows = try read("{\"user_access_code\": {\"22EF00000000001\": \"VALID\"}}\n# MD5 checksum 0123456789abcdef\n")
        XCTAssertEqual(rows.count, 1)
    }

    // MARK: - Several slicers

    func testTheSamePrinterInTwoSlicersKeepsTheCodesOfBoth() {
        let studio = SlicerPrinter(serial: "22EF00000000001", accessCodes: ["STALE"], host: nil, name: nil, source: "Bambu Studio")
        let orca = SlicerPrinter(serial: "22ef00000000001", accessCodes: ["VALID", "STALE"], host: "10.0.0.7", name: nil, source: "OrcaSlicer")
        let other = SlicerPrinter(serial: "01P00A000000002", accessCodes: ["X"], host: "10.0.0.8", name: nil, source: "OrcaSlicer")
        XCTAssertEqual(SlicerImport.merge([studio, orca, other]), [
            SlicerPrinter(serial: "22EF00000000001", accessCodes: ["STALE", "VALID"], host: "10.0.0.7", name: nil, source: "Bambu Studio"),
            other,
        ])
    }

    func testMergeUppercasesALoneLowercaseSerial() {
        let printer = SlicerPrinter(serial: "22ef00000000001", accessCodes: ["A"], host: nil, name: nil, source: "S")
        XCTAssertEqual(SlicerImport.merge([printer]).map(\.serial), ["22EF00000000001"])
    }

    // MARK: - Helpers

    /// `BBLCrossTalk::Encode_DevIp`, as shared/scripts/fleet/fleet.py writes it: the inverse of
    /// `SlicerImport.decodeIP`.
    static func scrambled(_ ip: String, key: String) -> String {
        var seed: UInt32 = 2_166_136_261
        for byte in key.utf8 { seed = (seed ^ UInt32(byte)) &* 16_777_619 }
        var rng = MersenneTwister(seed: seed)
        return ip.utf8.map { String(format: "%02x", $0 ^ UInt8(truncatingIfNeeded: rng.next())) }.joined()
    }
}
