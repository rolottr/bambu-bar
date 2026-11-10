import XCTest

/// The "Wiki" link on an issue: Bambu Lab's redirect to the code's page for the printer model, carrying only the
/// serial's model prefix; print errors, which have no pages, go to the HMS index.
final class HMSWikiTests: XCTestCase {
    private func query(_ url: URL?) -> [String: String] {
        let items = url.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems } ?? []
        return Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
    }

    func testHMSCodeLinksToTheModelsPage() {
        let url = HMSCatalog.wikiURL(code: "0700210000020019", serial: "01P00A123456789")
        XCTAssertEqual(url?.host, "e.bambulab.com")
        let q = query(url)
        XCTAssertEqual(q["e"], "0700210000020019")
        XCTAssertEqual(q["d"], "01P", "only the model prefix, never the whole serial")
        XCTAssertEqual(q["s"], "device_hms")
        XCTAssertNotNil(q["lang"])
    }

    func testWithoutASerialTheModelIsLeftOut() {
        let q = query(HMSCatalog.wikiURL(code: "0501040000030002", serial: nil))
        XCTAssertEqual(q["e"], "0501040000030002")
        XCTAssertNil(q["d"])
    }

    func testPrintErrorsGoToTheIndex() {
        let url = HMSCatalog.wikiURL(code: "07008006", serial: "01P00A123456789")
        XCTAssertEqual(url?.host, "wiki.bambulab.com")
        XCTAssertTrue(url?.path.hasSuffix("/hms/home") == true)
    }

    func testParserLinksIssuesForThePrinter() throws {
        var doc = ReportDocument()
        // HMS_0700_2100_0002_0019 and print error 0700_8006.
        _ = doc.apply(["print": ["gcode_state": "PAUSE", "hms": [["attr": 0x0700_2100, "code": 0x0002_0019]],
                                 "print_error": 0x0700_8006] as [String: Any]])
        let issues = ReportParser.status(from: doc, hms: .shared, serial: "22E00A000000000").issues
        let hms = try XCTUnwrap(issues.first { $0.id == "HMS_0700_2100_0002_0019" })
        XCTAssertEqual(query(hms.wikiURL)["d"], "22E")
        let error = try XCTUnwrap(issues.first { $0.id == "0700_8006" })
        XCTAssertEqual(error.wikiURL?.host, "wiki.bambulab.com")
    }
}
