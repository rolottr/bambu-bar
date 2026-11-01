import XCTest

/// Printers whose serial turns up in another case than the uppercase one on their certificate (PR #3): the mock
/// fleet runs with SERIAL_CASE=mixed. Its Bambu Studio settings keep a stale code under each uppercase serial and
/// the working one under the lowercase serial, and its printers announce themselves in both spellings, in turn.
/// Each must still be one printer that comes online with its working code.
final class FleetIntegrationTests: XCTestCase {
    override func setUpWithError() throws {
        try Simulators.require()
    }

    private func slicerPrinters() throws -> [SlicerPrinter] {
        let printers = SlicerImport.read(Simulators.fleetSettings, source: "Bambu Studio")
        guard !printers.isEmpty else {
            throw XCTSkip("No fleet settings at \(Simulators.fleetSettings.path)")
        }
        return printers
    }

    // MARK: - Import

    /// Review finding P2: the PR made two entries per printer here (one per spelling), each with one code.
    func testSettingsGiveOnePrinterPerSerialWithBothCodes() throws {
        let printers = try slicerPrinters()
        XCTAssertEqual(printers.count, Simulators.fleetSize)
        XCTAssertEqual(Set(printers.map(\.serial)).count, Simulators.fleetSize, "one entry per printer")
        for printer in printers {
            XCTAssertEqual(printer.serial, printer.serial.uppercased())
            let number = printer.serial.suffix(5)
            XCTAssertEqual(printer.accessCodes, ["OLD\(number)", "FLT\(number)"], printer.serial)
            XCTAssertEqual(printer.host?.hasPrefix("127.0.0.1:188"), true, "the address under the other spelling")
        }
    }

    /// Importing the same settings again (Add All twice, or another slicer that knows the same printers) adds nothing.
    func testImportingTwiceAddsNoPrinter() throws {
        let printers = try slicerPrinters()
        XCTAssertEqual(SlicerImport.merge(printers + printers), printers)
    }

    // MARK: - Connecting

    private enum Outcome: Equatable {
        case online(code: String, refused: [String])
        case notOnline(String)
    }

    /// Tries the codes in turn the way PrinterSession does: the next one after the printer refuses one.
    private func connect(_ printer: SlicerPrinter, insecure: Bool = true) async -> Outcome {
        var refused: [String] = []
        for code in printer.accessCodes {
            let config = PrinterConfig(host: printer.host ?? "", serial: printer.serial, name: "", modelCode: "")
            let endpoint = config.endpoint
            switch await Self.connect(host: endpoint.host, port: endpoint.port, serial: printer.serial, code: code,
                                      insecure: insecure) {
            case nil:
                return .online(code: code, refused: refused)
            case let failure? where failure.isAuthentication:
                refused.append(code)
            case let failure?:
                return .notOnline("\(failure)")
            }
        }
        return .notOnline("every code refused: \(refused)")
    }

    /// One MQTT connection: nil once connected, else why not.
    private static func connect(host: String, port: UInt16, serial: String, code: String, insecure: Bool) async -> MQTTClient.Failure? {
        let client = MQTTClient(.init(host: host, port: port, password: code, serial: serial, connectTimeout: 8, insecure: insecure))
        let result: MQTTClient.Failure? = await withCheckedContinuation { continuation in
            var resumed = false
            client.onEvent = { event in
                guard !resumed else { return }
                switch event {
                case .connected:
                    resumed = true
                    continuation.resume(returning: nil)
                case .disconnected(let failure):
                    resumed = true
                    continuation.resume(returning: failure)
                case .message:
                    break
                }
            }
            client.connect()
        }
        client.onEvent = nil
        client.disconnect()
        return result
    }

    /// Review finding P2: with the PR the stale code was the only one left, and the printer stayed offline.
    func testEveryPrinterComesOnlineWithItsWorkingCode() async throws {
        let printers = try slicerPrinters()
        var outcomes: [String: Outcome] = [:]
        await withTaskGroup(of: (String, Outcome).self) { group in
            for printer in printers {
                group.addTask { (printer.serial, await self.connect(printer)) }
            }
            for await (serial, outcome) in group { outcomes[serial] = outcome }
        }
        for printer in printers where printer.serial != Simulators.switchedOffPrinter {
            let number = printer.serial.suffix(5)
            XCTAssertEqual(outcomes[printer.serial], .online(code: "FLT\(number)", refused: ["OLD\(number)"]), printer.serial)
        }
        guard case .notOnline = outcomes[Simulators.switchedOffPrinter] else {
            return XCTFail("the switched-off printer: \(String(describing: outcomes[Simulators.switchedOffPrinter]))")
        }
    }

    /// The fleet's certificates are self-signed: without `insecure` they must be refused, before any code is sent,
    /// even right after a connection to the same printer (a resumed TLS session has no certificate to check).
    func testFleetCertificatesAreRefusedUnlessInsecure() async throws {
        let printer = try XCTUnwrap(try slicerPrinters().first { $0.serial != Simulators.switchedOffPrinter })
        let endpoint = PrinterConfig(host: printer.host ?? "", serial: printer.serial, name: "", modelCode: "").endpoint
        let code = printer.accessCodes.last!
        let insecure = await Self.connect(host: endpoint.host, port: endpoint.port, serial: printer.serial, code: code, insecure: true)
        XCTAssertNil(insecure)
        let failure = await Self.connect(host: endpoint.host, port: endpoint.port, serial: printer.serial, code: code, insecure: false)
        guard case .certificate = failure else { return XCTFail("expected a certificate failure, got \(String(describing: failure))") }
    }

    // MARK: - Discovery

    /// The announcements alternate between the two spellings of each serial: still one printer each, with the
    /// serial the settings file gives it, so the announcement finds the printer that was added from the file.
    func testAnnouncementsInEitherCaseAreOnePrinterEach() async throws {
        let imported = Set(try slicerPrinters().map(\.serial))
        let found = MainQueueBox<[DiscoveredPrinter]>([])
        let discovery = PrinterDiscovery(extraPorts: [Simulators.announcementPort])
        discovery.onFound = { printer in found.update { $0.append(printer) } }
        discovery.start()
        // The fleet announces every printer every two seconds, the serial's case changing each time.
        try await Task.sleep(for: .seconds(7))
        discovery.stop()
        let fleet = await found.read().filter { $0.host.hasPrefix("127.0.0.1:188") }
        let online = Simulators.fleetSize - 1
        XCTAssertGreaterThanOrEqual(fleet.count, 2 * online, "each printer heard in both spellings")
        let serials = Set(fleet.map(\.serial))
        XCTAssertEqual(serials.count, online, "one printer each")
        XCTAssertTrue(serials.allSatisfy { $0 == $0.uppercased() })
        XCTAssertTrue(serials.isSubset(of: imported), "matches the printers imported from the settings")
        XCTAssertTrue(fleet.allSatisfy { $0.name != $0.serial }, "announced with their names")
    }
}
