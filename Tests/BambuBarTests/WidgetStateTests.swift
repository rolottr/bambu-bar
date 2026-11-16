import XCTest

/// The widget projects a print only for a while after the app's snapshot, then holds its figures and says how old
/// they are (issue #13: a widget left on an old timeline ran ahead of the printer).
final class WidgetStateTests: XCTestCase {
    private let saved = Date(timeIntervalSince1970: 1_800_000_000)

    private func snapshot(_ state: JobState = .printing, progress: Int = 40, remaining: Int = 120) -> SharedSnapshot {
        var s = PrinterStatus.sample
        s.state = state
        s.progress = progress
        s.remainingMinutes = remaining
        s.updatedAt = saved
        return SharedSnapshot(serial: "S1", printerName: "P1S", model: "P1S", link: .online, status: s, lastJob: nil,
                              savedAt: saved)
    }

    func testProjectsInsideTheWindow() {
        let w = WidgetState(snapshot: snapshot(), date: saved.addingTimeInterval(30 * 60))
        XCTAssertFalse(w.isStale)
        XCTAssertNil(w.staleText)
        XCTAssertEqual(w.minutesLeft, 90)
        XCTAssertEqual(w.progress, 0.4 + 0.6 * 0.25, accuracy: 0.0001)
        // The layer moves on the same line (sample: layer 170 of 305), so "55 %" doesn't sit beside an old layer.
        XCTAssertEqual(w.layerText, "203/305")
        XCTAssertNotNil(w.finishText)
    }

    func testHoldsFiguresPastTheWindow() {
        let window = WidgetState.projectionWindow
        let atEdge = WidgetState(snapshot: snapshot(), date: saved.addingTimeInterval(window))
        let late = WidgetState(snapshot: snapshot(), date: saved.addingTimeInterval(2 * 3600 + 17 * 60))
        XCTAssertTrue(late.isStale)
        XCTAssertEqual(late.progress, atEdge.progress, accuracy: 0.0001)
        XCTAssertEqual(late.minutesLeft, atEdge.minutesLeft)
        XCTAssertEqual(late.layerText, atEdge.layerText)
        // The age takes the finish time's place.
        XCTAssertNil(late.finishText)
        XCTAssertNotNil(late.staleText)
    }

    func testOnlyARunningPrintGoesStale() {
        let later = saved.addingTimeInterval(5 * 3600)
        for state in [JobState.paused, .preparing, .finished, .failed, .idle] {
            XCTAssertFalse(WidgetState(snapshot: snapshot(state), date: later).isStale, "\(state)")
        }
    }

    func testThePlaceholderIsNeverStale() {
        let w = WidgetState(snapshot: .placeholder, date: Date())
        XCTAssertFalse(w.isStale)
    }

    func testReceiptsRoundTrip() throws {
        let receipt = WidgetReceipt(builtAt: saved.addingTimeInterval(5), savedAt: saved, link: .online,
                                    status: snapshot().status)
        let data = try SharedStore.encoder.encode(["S1": receipt])
        let decoded = try SharedStore.decoder.decode([String: WidgetReceipt].self, from: data)
        XCTAssertEqual(decoded["S1"], receipt)
    }
}
