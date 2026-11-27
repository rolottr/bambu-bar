import XCTest

/// Widget reloads for a printer's connection (WidgetReloadPolicy). A printer that couldn't be reached used to flip
/// its widget link on every reconnect attempt and ask for a reload every two minutes, spending the widgets' daily
/// allowance until they froze (feedback survey, 26 Nov 2026; issues #10, #12, #15).
final class WidgetReloadPolicyTests: XCTestCase {
    typealias Connection = WidgetReloadPolicy.Connection

    /// Plays phases through the policy the way PrinterSession and PrinterStore.writeSnapshot do for one printer,
    /// and records the reload requests.
    private struct Printer {
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        var hasStatus = false
        var lastHeard: Date?
        var failedSinceOnline = false
        var lastKey = ""
        var requests = WidgetReloadPolicy.Requests()
        /// When a widget last built a timeline (the receipts).
        var newestBuild: Date?
        /// Whether a widget answers each request at once. When false, macOS drops them until `widgetBuilt`.
        var widgetsAnswer = true
        /// Seconds from `start` at which a reload was asked for.
        var reloads: [TimeInterval] = []
        /// The link the widgets were last asked to show.
        var shown: SharedSnapshot.Link?
        /// The mapping before WidgetReloadPolicy, to show what it cost.
        var legacy = false

        mutating func phase(_ connection: Connection, at t: TimeInterval) {
            switch connection {
            case .online: failedSinceOnline = false
            case .offline: failedSinceOnline = true
            case .idle, .connecting, .unauthorized: break
            }
            let now = start.addingTimeInterval(t)
            let link: SharedSnapshot.Link
            if legacy {
                switch connection {
                case .online: link = .online
                case .connecting: link = hasStatus ? .online : .connecting
                case .unauthorized: link = .unauthorized
                case .idle, .offline: link = .offline
                }
            } else {
                link = WidgetReloadPolicy.link(connection, hasStatus: hasStatus, lastHeard: lastHeard,
                                               failedSinceOnline: failedSinceOnline, now: now)
            }
            let key = "\(link)"
            let force = WidgetReloadPolicy.forcesReload(connection)
            // As writeSnapshot: while requests go unanswered, it reads the receipts before judging a change.
            if requests.unanswered > 0 { requests.receiptsRead(newestBuild: newestBuild) }
            let reload = WidgetReloadPolicy.shouldReload(
                keyChanged: key != lastKey, force: force, opened: false,
                sinceLastReload: now.timeIntervalSince(requests.last),
                unanswered: requests.unanswered, correctionAllowed: false, correctionDue: { false })
            guard reload else { return }
            lastKey = key
            requests.asked(at: now, newestBuild: newestBuild)
            if widgetsAnswer { newestBuild = now }
            reloads.append(t)
            shown = link
        }

        /// A report: the printer is talking.
        mutating func heard(at t: TimeInterval) {
            hasStatus = true
            lastHeard = start.addingTimeInterval(t)
        }

        /// A widget builds a timeline at `t`: macOS answered the request before it.
        mutating func widgetBuilt(at t: TimeInterval) {
            newestBuild = start.addingTimeInterval(t)
        }

        /// Connects and reports at `t`.
        mutating func comesOnline(at t: TimeInterval) {
            phase(.connecting, at: t)
            heard(at: t + 1)
            phase(.online, at: t + 1)
        }

        /// Reconnect attempts that fail at once, as PrinterConnection makes them (2, 4, 8, 15, then every 30 s),
        /// from `t` until `end`. Returns when the last attempt failed.
        @discardableResult
        mutating func retries(from t: TimeInterval, until end: TimeInterval) -> TimeInterval {
            var now = t
            var attempt = 0
            let delays: [TimeInterval] = [2, 4, 8, 15, 30]
            var last = t
            while now < end {
                phase(.connecting, at: now)
                phase(.offline, at: now)
                last = now
                now += delays[min(attempt, delays.count - 1)]
                attempt += 1
            }
            return last
        }
    }

    // MARK: - Links

    func testFirstLaunchShowsConnectingUntilTheFirstReport() {
        let now = Date()
        XCTAssertEqual(WidgetReloadPolicy.link(.connecting, hasStatus: false, lastHeard: nil, failedSinceOnline: false, now: now),
                       .connecting)
        // The first attempt failed: the printer is off, not "Connecting…" for good.
        XCTAssertEqual(WidgetReloadPolicy.link(.connecting, hasStatus: false, lastHeard: nil, failedSinceOnline: true, now: now),
                       .offline)
        XCTAssertEqual(WidgetReloadPolicy.link(.offline, hasStatus: false, lastHeard: nil, failedSinceOnline: true, now: now),
                       .offline)
    }

    func testAReconnectingPrinterCountsAsOnlineOnlyWhileRecentlyHeard() {
        let now = Date()
        let recent = now.addingTimeInterval(-30)
        let old = now.addingTimeInterval(-(WidgetReloadPolicy.recentlyHeard + 1))
        for connection in [Connection.connecting, .offline] {
            XCTAssertEqual(WidgetReloadPolicy.link(connection, hasStatus: true, lastHeard: recent, failedSinceOnline: true, now: now),
                           .online, "\(connection)")
            XCTAssertEqual(WidgetReloadPolicy.link(connection, hasStatus: true, lastHeard: old, failedSinceOnline: true, now: now),
                           .offline, "\(connection)")
        }
    }

    func testTheFirstAttemptAfterAWakeKeepsTheLastReport() {
        // Asleep for hours: nothing failed yet, so the widget keeps the printer as it was until the attempt says.
        let now = Date()
        let hoursAgo = now.addingTimeInterval(-8 * 3600)
        XCTAssertEqual(WidgetReloadPolicy.link(.connecting, hasStatus: true, lastHeard: hoursAgo, failedSinceOnline: false, now: now),
                       .online)
    }

    func testSettledPhasesMapDirectly() {
        let now = Date()
        XCTAssertEqual(WidgetReloadPolicy.link(.online, hasStatus: false, lastHeard: nil, failedSinceOnline: true, now: now), .online)
        XCTAssertEqual(WidgetReloadPolicy.link(.unauthorized, hasStatus: true, lastHeard: now, failedSinceOnline: false, now: now),
                       .unauthorized)
        XCTAssertEqual(WidgetReloadPolicy.link(.idle, hasStatus: true, lastHeard: now, failedSinceOnline: false, now: now), .offline)
    }

    // MARK: - Scenarios

    func testAPrinterThatStaysUnreachableReloadsOnce() {
        var printer = Printer()
        printer.comesOnline(at: 0)
        // Reports every second for five minutes, then the printer is switched off for six hours.
        printer.heard(at: 300)
        printer.phase(.offline, at: 395) // the watchdog gives up after 95 s of silence
        printer.retries(from: 395, until: 395 + 6 * 3600)
        // "Connecting…" and online at launch, then offline once, after the printer had been silent for
        // `recentlyHeard`; the hundreds of attempts after that cost nothing.
        XCTAssertEqual(printer.reloads.count, 3, "\(printer.reloads)")
        XCTAssertEqual(printer.shown, .offline)
        let offlineAt = printer.reloads.last ?? 0
        XCTAssertGreaterThan(offlineAt, 300 + WidgetReloadPolicy.recentlyHeard)
        XCTAssertLessThan(offlineAt, 300 + WidgetReloadPolicy.recentlyHeard + 60)
    }

    func testTheOldMappingReloadedOnEveryOtherAttempt() {
        // What the app did before: the same six hours cost a request every two to three minutes.
        var printer = Printer()
        printer.legacy = true
        printer.comesOnline(at: 0)
        printer.heard(at: 300)
        printer.phase(.offline, at: 395)
        printer.retries(from: 395, until: 395 + 6 * 3600)
        XCTAssertGreaterThan(printer.reloads.count, 6 * 20, "\(printer.reloads.count) requests")
    }

    func testAShortDropCostsNoReload() {
        var printer = Printer()
        printer.comesOnline(at: 0)
        printer.heard(at: 600)
        let before = printer.reloads.count
        // The printer closes the connection; the next attempt, two seconds later, gets through.
        printer.phase(.offline, at: 601)
        printer.phase(.connecting, at: 603)
        printer.heard(at: 604)
        printer.phase(.online, at: 604)
        XCTAssertEqual(printer.reloads.count, before)
        XCTAssertEqual(printer.shown, .online)
    }

    func testARealOfflineTransitionAndTheReturnEachReloadOnce() {
        var printer = Printer()
        printer.comesOnline(at: 0)
        printer.heard(at: 1000)
        let online = printer.reloads.count
        let lastFailure = printer.retries(from: 1001, until: 1001 + 3600)
        XCTAssertEqual(printer.reloads.count, online + 1)
        XCTAssertEqual(printer.shown, .offline)
        // Switched back on: the next attempt gets a report and the widgets hear about it at once.
        printer.comesOnline(at: lastFailure + 30)
        XCTAssertEqual(printer.reloads.count, online + 2)
        XCTAssertEqual(printer.reloads.last, lastFailure + 31)
        XCTAssertEqual(printer.shown, .online)
    }

    func testFirstLaunchWithTheSwitchedOffPrinter() {
        var printer = Printer()
        printer.retries(from: 0, until: 6 * 3600)
        // "Connecting…" at launch, then "Offline" once the spacing allows, and nothing more.
        XCTAssertEqual(printer.reloads.count, 2, "\(printer.reloads)")
        XCTAssertEqual(printer.reloads.first, 0)
        XCTAssertEqual(printer.shown, .offline)
    }

    func testFirstLaunchWithAReachablePrinter() {
        var printer = Printer()
        printer.phase(.connecting, at: 0)
        XCTAssertEqual(printer.shown, .connecting)
        printer.heard(at: 2)
        printer.phase(.online, at: 2)
        // Online is forced, so it doesn't wait two minutes behind "Connecting…".
        XCTAssertEqual(printer.reloads, [0, 2])
        XCTAssertEqual(printer.shown, .online)
    }

    func testACertificateFailureReachesTheWidgetRightAfterAReload() {
        // Not retried, so nothing later would correct the widget: it goes through inside the two-minute spacing.
        var printer = Printer()
        printer.comesOnline(at: 0)
        printer.heard(at: 30)
        printer.phase(Connection(PrinterConnection.Phase.certificate("The serial doesn't match.")), at: 31)
        XCTAssertEqual(printer.reloads.last, 31)
        XCTAssertEqual(printer.shown, .offline)
    }

    func testPhasesThatNothingCorrectsForceAReload() {
        // The widget's link and the reload both come from WidgetReloadPolicy.Connection(phase), so they can't disagree.
        let expected: [(PrinterConnection.Phase, Connection, Bool)] = [
            (.idle, .idle, true),
            (.connecting, .connecting, false),
            (.online, .online, true),
            (.offline("No route."), .offline, false),
            (.unauthorized, .unauthorized, true),
            (.certificate("The serial doesn't match."), .idle, true),
            (.localNetworkDenied, .offline, false),
        ]
        for (phase, connection, forces) in expected {
            XCTAssertEqual(Connection(phase), connection, "\(phase)")
            XCTAssertEqual(WidgetReloadPolicy.forcesReload(Connection(phase)), forces, "\(phase)")
        }
    }

    // MARK: - Answered requests

    /// Five minutes online with no widget answering (macOS dropping requests), two of them judged.
    private func printerWithTwoUnansweredRequests() -> Printer {
        var printer = Printer()
        printer.widgetsAnswer = false
        printer.comesOnline(at: 0)          // 0 and 1: too close to judge
        printer.phase(.idle, at: 100)       // stopped: judges the request at 1
        printer.comesOnline(at: 200)        // judges the one at 100
        XCTAssertEqual(printer.requests.unanswered, 2)
        return printer
    }

    func testAnAnsweredRequestEndsTheBackOffBeforeTheNextChange() {
        var printer = printerWithTwoUnansweredRequests()
        // macOS answers the request at 201; the app reads the receipt without asking for anything.
        printer.widgetBuilt(at: 210)
        printer.heard(at: 400)
        let before = printer.reloads.count
        // Silent for longer than `recentlyHeard`: an unforced change, two minutes after the last request. It used to
        // wait out 40 minutes of back-off behind requests that had been answered.
        printer.phase(.offline, at: 530)
        XCTAssertEqual(printer.requests.unanswered, 0)
        XCTAssertEqual(printer.reloads.count, before + 1)
        XCTAssertEqual(printer.reloads.last, 530)
        XCTAssertEqual(printer.shown, .offline)
    }

    func testTheBackOffHoldsWhileRequestsGoUnanswered() {
        var printer = printerWithTwoUnansweredRequests()
        printer.heard(at: 400)
        let before = printer.reloads.count
        printer.phase(.offline, at: 530)
        XCTAssertEqual(printer.reloads.count, before)
        XCTAssertEqual(printer.requests.unanswered, 2)
        // Past the spacing for two unanswered requests (40 minutes), the change goes through.
        printer.phase(.connecting, at: 201 + WidgetReloadPolicy.retrySpacing(unanswered: 2) + 1)
        XCTAssertEqual(printer.reloads.count, before + 1)
        XCTAssertEqual(printer.shown, .offline)
    }

    func testRequests() {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        var requests = WidgetReloadPolicy.Requests()
        requests.receiptsRead(newestBuild: t0) // nothing asked yet
        XCTAssertEqual(requests.answered, .distantPast)
        XCTAssertFalse(requests.asked(at: t0, newestBuild: nil))
        // Seconds later (a connect forces two): not judged.
        XCTAssertFalse(requests.asked(at: t0 + 5, newestBuild: nil))
        XCTAssertEqual(requests.unanswered, 0)
        // A build from before the request doesn't answer it.
        XCTAssertTrue(requests.asked(at: t0 + 300, newestBuild: t0 + 1))
        XCTAssertTrue(requests.asked(at: t0 + 600, newestBuild: t0 + 1))
        XCTAssertEqual(requests.unanswered, 2)
        requests.receiptsRead(newestBuild: t0 + 299)
        XCTAssertEqual(requests.unanswered, 2)
        // A build since the last request answers it, read before any reload.
        requests.receiptsRead(newestBuild: t0 + 601)
        XCTAssertEqual(requests.unanswered, 0)
        XCTAssertEqual(requests.answered, t0 + 600)
        XCTAssertFalse(requests.asked(at: t0 + 900, newestBuild: t0 + 601))
        XCTAssertEqual(requests.answered, t0 + 600)
        XCTAssertTrue(requests.asked(at: t0 + 1200, newestBuild: t0 + 601))
        XCTAssertEqual(requests.unanswered, 1)
    }

    // MARK: - Gate

    func testForcedChangesAlwaysGoThrough() {
        for unanswered in [0, 1, 5] {
            XCTAssertTrue(WidgetReloadPolicy.shouldReload(keyChanged: true, force: true, opened: false, sinceLastReload: 1,
                                                          unanswered: unanswered, correctionAllowed: false, correctionDue: { false }))
        }
        // Nothing changed: forcing alone asks for nothing.
        XCTAssertFalse(WidgetReloadPolicy.shouldReload(keyChanged: false, force: true, opened: false, sinceLastReload: 9999,
                                                       unanswered: 0, correctionAllowed: false, correctionDue: { false }))
    }

    func testUnforcedChangesKeepTwoMinutesApart() {
        XCTAssertFalse(WidgetReloadPolicy.shouldReload(keyChanged: true, force: false, opened: false, sinceLastReload: 119,
                                                       unanswered: 0, correctionAllowed: true, correctionDue: { false }))
        XCTAssertTrue(WidgetReloadPolicy.shouldReload(keyChanged: true, force: false, opened: false, sinceLastReload: 121,
                                                      unanswered: 0, correctionAllowed: true, correctionDue: { false }))
    }

    func testUnforcedChangesBackOffWhileMacOSDropsRequests() {
        // The spacing corrections already used: twenty minutes after one unanswered request, an hour after three.
        XCTAssertFalse(WidgetReloadPolicy.shouldReload(keyChanged: true, force: false, opened: false, sinceLastReload: 1100,
                                                       unanswered: 1, correctionAllowed: true, correctionDue: { false }))
        XCTAssertTrue(WidgetReloadPolicy.shouldReload(keyChanged: true, force: false, opened: false, sinceLastReload: 1201,
                                                      unanswered: 1, correctionAllowed: true, correctionDue: { false }))
        XCTAssertFalse(WidgetReloadPolicy.shouldReload(keyChanged: true, force: false, opened: false, sinceLastReload: 3000,
                                                       unanswered: 4, correctionAllowed: true, correctionDue: { false }))
        XCTAssertEqual(WidgetReloadPolicy.retrySpacing(unanswered: 0), 600)
        XCTAssertEqual(WidgetReloadPolicy.retrySpacing(unanswered: 2), 2400)
        XCTAssertEqual(WidgetReloadPolicy.retrySpacing(unanswered: 9), 3600)
    }

    func testCorrections() {
        XCTAssertTrue(WidgetReloadPolicy.shouldReload(keyChanged: false, force: false, opened: false, sinceLastReload: 601,
                                                      unanswered: 0, correctionAllowed: true, correctionDue: { true }))
        XCTAssertFalse(WidgetReloadPolicy.shouldReload(keyChanged: false, force: false, opened: false, sinceLastReload: 599,
                                                       unanswered: 0, correctionAllowed: true, correctionDue: { true }))
        // Past the daily cap the receipts aren't even read.
        XCTAssertFalse(WidgetReloadPolicy.shouldReload(keyChanged: false, force: false, opened: false, sinceLastReload: 9999,
                                                       unanswered: 0, correctionAllowed: false,
                                                       correctionDue: { XCTFail("read past the cap"); return true }))
    }

    func testOpeningThePanelReloadsWhatIsBehind() {
        XCTAssertTrue(WidgetReloadPolicy.shouldReload(keyChanged: true, force: false, opened: true, sinceLastReload: 121,
                                                      unanswered: 3, correctionAllowed: false, correctionDue: { false }))
        XCTAssertTrue(WidgetReloadPolicy.shouldReload(keyChanged: false, force: false, opened: true, sinceLastReload: 121,
                                                      unanswered: 0, correctionAllowed: true, correctionDue: { true }))
        XCTAssertFalse(WidgetReloadPolicy.shouldReload(keyChanged: true, force: false, opened: true, sinceLastReload: 60,
                                                       unanswered: 0, correctionAllowed: true, correctionDue: { true }))
    }
}
