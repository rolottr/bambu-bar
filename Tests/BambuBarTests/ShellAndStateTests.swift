import AppKit
import Sparkle
import XCTest

final class ShellAndStateTests: XCTestCase {

    // MARK: - WindowFrameClamp

    func testClampPullsOffScreenRightAndLeftBackInsideVisibleFrame() {
        let visible = NSRect(x: 0, y: 64, width: 1440, height: 811)
        let minSize = NSSize(width: 960, height: 600)

        let offRight = NSRect(x: 1600, y: 200, width: 980, height: 700)
        let clampedRight = WindowFrameClamp.clamped(offRight, minSize: minSize, visibleFrame: visible)
        XCTAssertEqual(clampedRight, NSRect(x: 460, y: 175, width: 980, height: 700))

        let offLeft = NSRect(x: -500, y: 120, width: 980, height: 700)
        let clampedLeft = WindowFrameClamp.clamped(offLeft, minSize: minSize, visibleFrame: visible)
        XCTAssertEqual(clampedLeft, NSRect(x: 0, y: 120, width: 980, height: 700))
    }

    func testClampLiftsWindowOutFromUnderDockAndMenuBar() {
        let visible = NSRect(x: 0, y: 72, width: 1440, height: 800)
        let minSize = NSSize(width: 960, height: 600)

        let underDock = NSRect(x: 100, y: 10, width: 980, height: 650)
        let lifted = WindowFrameClamp.clamped(underDock, minSize: minSize, visibleFrame: visible)
        XCTAssertEqual(lifted.minY, 72)
        XCTAssertTrue(visible.contains(lifted))

        let aboveMenuBar = NSRect(x: 100, y: 400, width: 980, height: 650)
        let lowered = WindowFrameClamp.clamped(aboveMenuBar, minSize: minSize, visibleFrame: visible)
        XCTAssertEqual(lowered.maxY, visible.maxY)
        XCTAssertTrue(visible.contains(lowered))
    }

    func testClampHandlesTinyScreenAndOversizedWindow() {
        let tinyVisible = NSRect(x: 0, y: 40, width: 800, height: 500)
        let minSize = NSSize(width: 960, height: 600)
        let normal = NSRect(x: 100, y: 100, width: 980, height: 700)

        let tinyClamped = WindowFrameClamp.clamped(normal, minSize: minSize, visibleFrame: tinyVisible)
        XCTAssertEqual(tinyClamped, tinyVisible)

        let standardVisible = NSRect(x: 0, y: 40, width: 1440, height: 820)
        let oversized = NSRect(x: -50, y: 0, width: 2000, height: 1200)
        let fit = WindowFrameClamp.clamped(oversized, minSize: minSize, visibleFrame: standardVisible)
        XCTAssertEqual(fit, standardVisible)
    }

    func testClampEnforcesMinimumSizeWhenScreenAllows() {
        let visible = NSRect(x: 0, y: 40, width: 1440, height: 820)
        let minSize = NSSize(width: 960, height: 600)
        let tooSmall = NSRect(x: 200, y: 200, width: 500, height: 300)

        let grown = WindowFrameClamp.clamped(tooSmall, minSize: minSize, visibleFrame: visible)
        XCTAssertEqual(grown.size, minSize)
        XCTAssertTrue(visible.contains(grown))
    }

    func testClampPicksLargestIntersectionScreenIncludingNegativeOriginSecondary() {
        let primary = NSRect(x: 0, y: 64, width: 1440, height: 811)
        let leftSecondary = NSRect(x: -1920, y: 0, width: 1920, height: 1055)
        let minSize = NSSize(width: 960, height: 600)

        // Window 85% on left secondary display and 15% overlapping primary (which is listed first).
        let straddling = NSRect(x: -850, y: 200, width: 1000, height: 650)
        let clamped = WindowFrameClamp.clamped(
            straddling,
            minSize: minSize,
            screens: [primary, leftSecondary],
            preferred: primary
        )
        // Must clamp onto leftSecondary, NOT jump onto primary!
        XCTAssertTrue(leftSecondary.contains(clamped))
        XCTAssertEqual(clamped.maxX, leftSecondary.maxX)

        // Window completely off-screen falls back to preferred screen.
        let wayOffScreen = NSRect(x: -99999, y: -99999, width: 980, height: 700)
        let recovered = WindowFrameClamp.clamped(
            wayOffScreen,
            minSize: minSize,
            screens: [primary, leftSecondary],
            preferred: primary
        )
        XCTAssertTrue(primary.contains(recovered))
    }

    func testClampPreservesAspectRatioWhenShrinkingCameraWindow() {
        let smallScreen = NSRect(x: 0, y: 40, width: 640, height: 480)
        let minSize = NSSize(width: 320, height: 180)
        let wideCamera = NSRect(x: 100, y: 100, width: 1280, height: 720) // 16:9

        let fitted = WindowFrameClamp.clamped(
            wideCamera,
            minSize: minSize,
            visibleFrame: smallScreen,
            aspectRatio: 16.0 / 9.0
        )
        XCTAssertTrue(smallScreen.contains(fitted))
        XCTAssertEqual(fitted.width, 640, accuracy: 1)
        XCTAssertEqual(fitted.height, 360, accuracy: 1)
    }

    // MARK: - SingleInstance Tie-Breaker

    func testSingleInstanceEarlierLaunchDateWins() {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        let older = SingleInstance.Instance(processIdentifier: 200, launchDate: t0)
        let newer = SingleInstance.Instance(processIdentifier: 100, launchDate: t0.addingTimeInterval(2))

        XCTAssertEqual(SingleInstance.handOverTarget(newer, others: [older]), older)
        XCTAssertNil(SingleInstance.handOverTarget(older, others: [newer]))
    }

    func testSingleInstanceSimultaneousLaunchBreaksTieByPID() {
        let sameTime = Date(timeIntervalSince1970: 1_800_000_000)
        let lowPID = SingleInstance.Instance(processIdentifier: 101, launchDate: sameTime)
        let highPID = SingleInstance.Instance(processIdentifier: 102, launchDate: sameTime)

        // Exactly one instance hands over so two simultaneous launches never both exit.
        XCTAssertNil(SingleInstance.handOverTarget(lowPID, others: [highPID]))
        XCTAssertEqual(SingleInstance.handOverTarget(highPID, others: [lowPID]), lowPID)
    }

    // MARK: - RelaunchTicketStore

    private final class TestClock: @unchecked Sendable {
        var now: Date
        init(_ now: Date) { self.now = now }
    }

    func testRelaunchTicketMintQueueAndConsume() throws {
        let suiteName = "dev.rolottr.BambuBar.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let clock = TestClock(Date(timeIntervalSince1970: 1_800_000_000))
        let predecessor = RelaunchTicketStore(defaults: defaults, processIdentifier: 4001, now: { clock.now })
        let ticket = try XCTUnwrap(predecessor.begin())
        XCTAssertTrue(predecessor.isPending(ticket))

        let printerURL = URL(string: "bambubar://printer/22E00A000000000")!
        let rejectedURL = URL(string: "https://example.com/ignored")!
        XCTAssertTrue(predecessor.queue([printerURL, rejectedURL], for: ticket))

        // Replacement process with a different PID validates and consumes the ticket once.
        let replacement = RelaunchTicketStore(defaults: defaults, processIdentifier: 4002, now: { clock.now })
        let args = ["BambuBar", RelaunchTicketStore.argument, ticket.token.uuidString]
        let pending = try XCTUnwrap(replacement.pending(arguments: args))
        XCTAssertEqual(pending.token, ticket.token)
        XCTAssertEqual(pending.predecessor, 4001)

        // Same PID cannot claim its own ticket.
        XCTAssertNil(predecessor.pending(arguments: args))

        let urls = try replacement.consume(pending)
        XCTAssertEqual(urls, [printerURL])
        // Consumed ticket cannot be reused.
        XCTAssertNil(replacement.pending(arguments: args))
        XCTAssertThrowsError(try replacement.consume(pending))
    }

    func testRelaunchTicketExpiresAndCancels() throws {
        let suiteName = "dev.rolottr.BambuBar.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let clock = TestClock(Date(timeIntervalSince1970: 1_800_000_000))
        let predecessor = RelaunchTicketStore(defaults: defaults, processIdentifier: 5001, now: { clock.now })
        let ticket = try XCTUnwrap(predecessor.begin())

        // Past the 30 s validity window the ticket is rejected.
        clock.now = clock.now.addingTimeInterval(35)
        let replacement = RelaunchTicketStore(defaults: defaults, processIdentifier: 5002, now: { clock.now })
        let args = ["BambuBar", RelaunchTicketStore.argument, ticket.token.uuidString]
        XCTAssertFalse(predecessor.isPending(ticket))
        XCTAssertNil(replacement.pending(arguments: args))

        // Fresh ticket that gets cancelled is also rejected.
        clock.now = Date(timeIntervalSince1970: 1_800_000_100)
        let second = try XCTUnwrap(predecessor.begin())
        predecessor.cancel(second)
        XCTAssertFalse(predecessor.isPending(second))
        XCTAssertNil(replacement.pending(arguments: ["BambuBar", RelaunchTicketStore.argument, second.token.uuidString]))
    }

    func testSingleInstanceQueuesURLsOntoActiveRelaunchTicket() throws {
        let suiteName = "dev.rolottr.BambuBar.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let clock = TestClock(Date(timeIntervalSince1970: 1_800_000_000))
        let predecessor = RelaunchTicketStore(defaults: defaults, processIdentifier: 6001, now: { clock.now })
        let ticket = try XCTUnwrap(predecessor.begin())

        let incoming = URL(string: "bambubar://open/settings")!
        let queued = SingleInstance.queueURLsIfRelaunching([incoming], forPredecessor: 6001, ticketStore: predecessor)
        XCTAssertTrue(queued)
        XCTAssertFalse(SingleInstance.queueURLsIfRelaunching([incoming], forPredecessor: 9999, ticketStore: predecessor))

        let replacement = RelaunchTicketStore(defaults: defaults, processIdentifier: 6002, now: { clock.now })
        let pending = try XCTUnwrap(replacement.pending(arguments: ["BambuBar", RelaunchTicketStore.argument, ticket.token.uuidString]))
        let consumed = try replacement.consume(pending)
        XCTAssertEqual(consumed, [incoming])
    }

    // MARK: - AppLanguage Locale Region Preservation

    func testAlignLocalePreservesRegionAcrossLanguageOverride() {
        let ruOnUS = AppLanguage.alignedLocaleIdentifier(global: "en_US", interface: "ru")
        let ruLocale = Locale(identifier: ruOnUS)
        XCTAssertEqual(ruLocale.language.languageCode?.identifier, "ru")
        XCTAssertEqual(ruLocale.region?.identifier, "US")

        let zhOnDE = AppLanguage.alignedLocaleIdentifier(global: "en_DE", interface: "zh-Hans")
        let zhLocale = Locale(identifier: zhOnDE)
        XCTAssertEqual(zhLocale.language.languageCode?.identifier, "zh")
        XCTAssertEqual(zhLocale.language.script?.identifier, "Hans")
        XCTAssertEqual(zhLocale.region?.identifier, "DE")

        let ptBROnGB = AppLanguage.alignedLocaleIdentifier(global: "en_GB", interface: "pt-BR")
        let ptLocale = Locale(identifier: ptBROnGB)
        XCTAssertEqual(ptLocale.language.languageCode?.identifier, "pt")
        XCTAssertEqual(ptLocale.region?.identifier, "GB")

        // Already matching language preserves the original regional identifier unchanged.
        XCTAssertEqual(AppLanguage.alignedLocaleIdentifier(global: "de_AT", interface: "de"), "de_AT")
    }

    // MARK: - ActivityLog Ring Buffer & Redaction

    @MainActor
    func testActivityLogRingBufferCapAndVisibilityGating() {
        let log = ActivityLog(capacity: 5)
        for i in 1...8 {
            log.record(category: .job, printerName: "P2S", summary: "Event \(i)")
        }
        // Hidden: buffer retains the newest 5, but visibleEntries stays empty (zero UI work).
        XCTAssertEqual(log.count, 5)
        XCTAssertEqual(log.allEntries.map(\.summary), ["Event 4", "Event 5", "Event 6", "Event 7", "Event 8"])
        XCTAssertTrue(log.visibleEntries.isEmpty)

        // Appearing catches up immediately and streams live updates.
        log.startMonitoring()
        XCTAssertEqual(log.visibleEntries.count, 5)
        log.record(category: .action, printerName: "P2S", summary: "Pause print", outcome: .applied)
        XCTAssertEqual(log.visibleEntries.count, 5)
        XCTAssertEqual(log.visibleEntries.last?.summary, "Pause print")
        XCTAssertEqual(log.visibleEntries.last?.outcome, .applied)

        // Disappearing stops live mirroring and drops UI state.
        log.stopMonitoring()
        XCTAssertTrue(log.visibleEntries.isEmpty)
        XCTAssertEqual(log.count, 5)
    }

    @MainActor
    func testActivityLogRedactsSecretsSerialsAndIPsOnExport() throws {
        let log = ActivityLog(capacity: 20)
        log.record(
            category: .connection,
            printerName: "Workshop 22E00A123456789",
            summary: "Failed connecting to 192.168.1.42:8883 with code 98765432 and serial 22E00A123456789 (printer.lan)",
            outcome: .failed
        )
        let cap = ActivityLog.CapabilitySummary(
            printerName: "P2S Workshop",
            modelName: "P2S",
            serialPrefix: "22E00A123456789",
            isOnline: true,
            amsCount: 1,
            hasChamberSensor: true,
            hasChamberHeater: false,
            hasChamberLight: true,
            hasDualLight: false,
            cameraEnabled: true,
            nozzleCount: 1,
            hasAuxFan: true,
            hasChamberFan: true
        )

        let data = try log.exportJSON(
            capabilities: [cap],
            appVersion: "3.0.1",
            serials: ["22E00A123456789"],
            hosts: ["printer.lan"],
            accessCodes: ["98765432"]
        )
        let json = try XCTUnwrap(String(data: data, encoding: .utf8))

        XCTAssertFalse(json.contains("192.168.1.42"))
        XCTAssertFalse(json.contains("98765432"))
        XCTAssertFalse(json.contains("22E00A123456789"))
        XCTAssertFalse(json.contains("printer.lan"))
        // Safe 3-character model prefix is kept in capabilities.
        XCTAssertTrue(json.contains("\"serialPrefix\" : \"22E\""))
    }

    @MainActor
    func testActivityLogCoalescesOfflineReconnectFlappingAndLogsRecovery() {
        let log = ActivityLog(capacity: 500)
        for _ in 0..<100 {
            log.recordConnectionTransition(printerID: "SN1", printerName: "P2S", state: .connecting, summary: "Connecting…")
            log.recordConnectionTransition(printerID: "SN1", printerName: "P2S", state: .offline, summary: "Offline: Timed out")
        }
        // Only the initial connecting and offline entries should be recorded, not 200 reconnect cycles.
        XCTAssertLessThanOrEqual(log.count, 2)
        XCTAssertEqual(log.allEntries.last?.summary, "Offline: Timed out")

        // Eventual recovery to .online is cleanly logged.
        log.recordConnectionTransition(printerID: "SN1", printerName: "P2S", state: .online, summary: "Connected")
        XCTAssertEqual(log.allEntries.last?.summary, "Connected")
        XCTAssertEqual(log.count, 3)
    }

    @MainActor
    func testActivityLogCoalescesIssueReconnectFlappingAndLogsRecovery() {
        let log = ActivityLog(capacity: 500)
        for _ in 0..<50 {
            log.recordConnectionTransition(printerID: "SN1", printerName: "P2S", state: .connecting, summary: "Connecting…")
            log.recordConnectionTransition(
                printerID: "SN1",
                printerName: "P2S",
                state: .issue("localNetworkDenied"),
                summary: "Local network access denied"
            )
        }
        // Only the initial connecting and issue entries should be recorded, not 100 reconnect cycles.
        XCTAssertLessThanOrEqual(log.count, 2)
        XCTAssertEqual(log.allEntries.last?.summary, "Local network access denied")

        log.recordConnectionTransition(printerID: "SN1", printerName: "P2S", state: .online, summary: "Connected")
        XCTAssertEqual(log.allEntries.last?.summary, "Connected")
        XCTAssertEqual(log.count, 3)
    }

    func testActivityLogRedactsIPv6WhilePreservingTimestamps() {
        let sample = "Error at 12:34:56 connecting to fe80::1, 2001:db8::1, [2001:db8::1]:8883, fe80::1%en0, ::1, and 2001:0db8:85a3:0000:0000:8a2e:0370:7334 (logged 2026-11-25 23:59:59)"
        let redacted = ActivityLog.redact(sample)

        XCTAssertTrue(redacted.contains("12:34:56"), "Clock timestamp 12:34:56 must survive redaction: \(redacted)")
        XCTAssertTrue(redacted.contains("23:59:59"), "Clock timestamp 23:59:59 must survive redaction: \(redacted)")
        XCTAssertFalse(redacted.contains("fe80::1"))
        XCTAssertFalse(redacted.contains("2001:db8::1"))
        XCTAssertFalse(redacted.contains("::1"))
        XCTAssertFalse(redacted.contains("2001:0db8:85a3:0000:0000:8a2e:0370:7334"))
        XCTAssertTrue(redacted.contains("<ip>"))
    }

    // MARK: - OnboardingDraft Apply vs Skip & Capability Gating

    func testOnboardingDraftAppliesOnlyOnFinishAndPreservesUntouchedNotificationFlags() {
        var persisted = OnboardingSettings(
            showProgressInMenuBar: true,
            notifyFinished: true,
            notifyFailed: true,
            notifyPaused: false,
            cameraEnabled: false,
            showPrinterActions: true,
            panelShowsAllPrinters: false
        )

        func applyOnboarding(_ draft: OnboardingDraft, finished: Bool) {
            guard finished else { return }
            persisted = draft.applied(to: persisted)
        }

        // Toggle several features, then Skip: persisted settings must remain completely unchanged.
        var skippedDraft = OnboardingDraft(from: persisted)
        skippedDraft.toggle(.menuBar)
        skippedDraft.toggle(.notifications)
        skippedDraft.toggle(.camera)
        skippedDraft.toggle(.controls)
        skippedDraft.toggle(.fleet)
        applyOnboarding(skippedDraft, finished: false)
        XCTAssertEqual(persisted.showProgressInMenuBar, true)
        XCTAssertEqual(persisted.notifyFinished, true)
        XCTAssertEqual(persisted.notifyFailed, true)
        XCTAssertEqual(persisted.notifyPaused, false)
        XCTAssertEqual(persisted.cameraEnabled, false)

        // Click Finish WITHOUT touching Alerts: mixed [true, true, false] notification flags stay intact!
        var untouchedAlertsDraft = OnboardingDraft(from: persisted)
        untouchedAlertsDraft.toggle(.camera)
        applyOnboarding(untouchedAlertsDraft, finished: true)
        XCTAssertEqual(persisted.cameraEnabled, true)
        XCTAssertEqual(persisted.notifyFinished, true)
        XCTAssertEqual(persisted.notifyFailed, true)
        XCTAssertEqual(persisted.notifyPaused, false)

        // Toggle Alerts off and back on before Finish: net value matches initialNotificationsEnabled,
        // so mixed [true, true, false] notification flags must STILL stay intact (Paused stays false).
        var roundTripAlertsDraft = OnboardingDraft(from: persisted)
        roundTripAlertsDraft.toggle(.notifications)
        XCTAssertEqual(roundTripAlertsDraft.notificationsEnabled, false)
        roundTripAlertsDraft.toggle(.notifications)
        XCTAssertEqual(roundTripAlertsDraft.notificationsEnabled, true)
        applyOnboarding(roundTripAlertsDraft, finished: true)
        XCTAssertEqual(persisted.notifyFinished, true)
        XCTAssertEqual(persisted.notifyFailed, true)
        XCTAssertEqual(persisted.notifyPaused, false)

        // Click Finish AFTER changing Alerts from true -> false: all three flags update together.
        var changedAlertsDraft = OnboardingDraft(from: persisted)
        changedAlertsDraft.toggle(.notifications)
        applyOnboarding(changedAlertsDraft, finished: true)
        XCTAssertEqual(persisted.notifyFinished, false)
        XCTAssertEqual(persisted.notifyFailed, false)
        XCTAssertEqual(persisted.notifyPaused, false)
    }

    // MARK: - UpdateChecker Hero Supporter Gate & License Loss

    @MainActor
    func testUnlicensedUpdateCheckerBlocksChecksAutoToggleAndStaysIdle() throws {
        let item = try XCTUnwrap(SUAppcastItem(dictionary: [
            "enclosure": [
                "url": "https://bambubar.app/api/updates/download?version=3.1.1",
                "sparkle:version": "202612310000",
                "sparkle:shortVersionString": "3.1.1",
                "length": "1024",
            ],
        ]))

        // 1. Unlicensed copy cannot start Sparkle, cannot enable automatic checks/downloads,
        //    and ignores check()/showUpdate().
        let unlicensed = UpdateChecker(downloadHeadersProvider: { nil })
        XCTAssertFalse(unlicensed.supportsAutomaticUpdates)
        XCTAssertFalse(unlicensed.checksAutomatically)
        XCTAssertFalse(unlicensed.downloadsAutomatically)
        XCTAssertFalse(unlicensed.canCheckForUpdates)

        unlicensed.checksAutomatically = true
        unlicensed.downloadsAutomatically = true
        XCTAssertFalse(unlicensed.checksAutomatically)
        XCTAssertFalse(unlicensed.downloadsAutomatically)

        unlicensed.start()
        XCTAssertFalse(unlicensed.isRunning)
        XCTAssertFalse(unlicensed.canCheckForUpdates)

        unlicensed.check()
        unlicensed.showUpdate()
        XCTAssertEqual(unlicensed.state, .idle)
        XCTAssertNil(unlicensed.available)
        XCTAssertNil(unlicensed.offered)

        // 2. Fail-closed delegate: shouldProceedWithUpdate throws and leaves NO stale unlicensed notice.
        unlicensed.beginCheckingForTests()
        var thrownError: Error?
        XCTAssertThrowsError(
            try unlicensed.updater(unlicensed.updater, shouldProceedWithUpdate: item, updateCheck: .updateInformation)
        ) { error in
            thrownError = error
        }
        let gateError = try XCTUnwrap(thrownError as NSError?)
        XCTAssertEqual(gateError.domain, UpdateChecker.unlicensedGateErrorDomain)
        XCTAssertEqual(gateError.code, UpdateChecker.unlicensedGateErrorCode)
        XCTAssertEqual(unlicensed.state, .idle)
        XCTAssertNil(unlicensed.available)
        XCTAssertNil(unlicensed.offered)
        XCTAssertNil(unlicensed.pendingVersion)

        unlicensed.updater(unlicensed.updater, didFindValidUpdate: item)
        unlicensed.updaterDidNotFindUpdate(unlicensed.updater)
        unlicensed.updater(unlicensed.updater, didFinishUpdateCycleFor: .updateInformation, error: gateError)
        XCTAssertEqual(unlicensed.state, .idle)
        XCTAssertNil(unlicensed.available)
        XCTAssertNil(unlicensed.offered)
    }

    @MainActor
    func testHeroSupporterUpdateFlowAndLicenseLossClearsStateAndDisablesAutomation() throws {
        let item = try XCTUnwrap(SUAppcastItem(dictionary: [
            "enclosure": [
                "url": "https://bambubar.app/api/updates/download?version=3.1.1",
                "sparkle:version": "202612310000",
                "sparkle:shortVersionString": "3.1.1",
                "length": "1024",
            ],
        ]))

        var active = true
        let headers = [
            UpdateDownload.licenseKeyHeader: "BB-TEST",
            UpdateDownload.activationHeader: "act-1",
            UpdateDownload.installationHeader: "inst-1",
        ]
        let checker = UpdateChecker(downloadHeadersProvider: { active ? headers : nil })

        // Active Hero Supporter can enable automatic checks/downloads and proceed with updates.
        XCTAssertTrue(checker.supportsAutomaticUpdates)
        XCTAssertTrue(checker.checksAutomatically)
        checker.downloadsAutomatically = true
        XCTAssertTrue(checker.downloadsAutomatically)
        XCTAssertNoThrow(
            try checker.updater(checker.updater, shouldProceedWithUpdate: item, updateCheck: .updates)
        )

        checker.updater(checker.updater, didFindValidUpdate: item)
        XCTAssertEqual(checker.available, UpdateChecker.Release(version: "3.1.1"))
        XCTAssertEqual(checker.offered, UpdateChecker.Release(version: "3.1.1"))
        XCTAssertEqual(checker.state, .available(UpdateChecker.Release(version: "3.1.1")))

        // Download headers attach ONLY to the verified production download endpoint, never to feed URLs.
        let prodDownloadReq = NSMutableURLRequest(url: URL(string: "https://bambubar.app/api/updates/download?version=3.1.1")!)
        checker.updater(checker.updater, willDownloadUpdate: item, with: prodDownloadReq)
        XCTAssertEqual(prodDownloadReq.value(forHTTPHeaderField: UpdateDownload.licenseKeyHeader), "BB-TEST")
        XCTAssertEqual(prodDownloadReq.value(forHTTPHeaderField: UpdateDownload.activationHeader), "act-1")
        XCTAssertEqual(prodDownloadReq.value(forHTTPHeaderField: UpdateDownload.installationHeader), "inst-1")

        let appcastReq = NSMutableURLRequest(url: URL(string: "https://bambubar.app/appcast.xml")!)
        checker.updater(checker.updater, willDownloadUpdate: item, with: appcastReq)
        XCTAssertNil(appcastReq.value(forHTTPHeaderField: UpdateDownload.licenseKeyHeader))

        // Losing the Hero Supporter license immediately disables automatic checks/downloads and clears offered updates.
        active = false
        checker.supporterStatusChanged()
        XCTAssertFalse(checker.supportsAutomaticUpdates)
        XCTAssertFalse(checker.checksAutomatically)
        XCTAssertFalse(checker.downloadsAutomatically)
        XCTAssertFalse(checker.canCheckForUpdates)
        XCTAssertNil(checker.available)
        XCTAssertNil(checker.offered)
        XCTAssertNil(checker.pendingVersion)
        XCTAssertEqual(checker.state, .idle)

        // Download headers are never attached after license loss.
        let postLossReq = NSMutableURLRequest(url: URL(string: "https://bambubar.app/api/updates/download?version=3.1.1")!)
        checker.updater(checker.updater, willDownloadUpdate: item, with: postLossReq)
        XCTAssertNil(postLossReq.value(forHTTPHeaderField: UpdateDownload.licenseKeyHeader))
        XCTAssertThrowsError(
            try checker.updater(checker.updater, shouldProceedWithUpdate: item, updateCheck: .updates)
        )
    }

    @MainActor
    func testStartLicenseGateIsDecidingGuardAndPreventsDoubleStart() {
        // Pure precondition table: unlicensed official non-developer run with EdDSA key must return false.
        XCTAssertFalse(UpdateChecker.mayStart(licensed: false, running: false, official: true, developerRun: false, hasEdKey: true))
        XCTAssertTrue(UpdateChecker.mayStart(licensed: true, running: false, official: true, developerRun: false, hasEdKey: true))
        XCTAssertFalse(UpdateChecker.mayStart(licensed: true, running: true, official: true, developerRun: false, hasEdKey: true))
        XCTAssertFalse(UpdateChecker.mayStart(licensed: true, running: false, official: false, developerRun: false, hasEdKey: true))
        XCTAssertFalse(UpdateChecker.mayStart(licensed: true, running: false, official: true, developerRun: true, hasEdKey: true))
        XCTAssertFalse(UpdateChecker.mayStart(licensed: true, running: false, official: true, developerRun: false, hasEdKey: false))

        // Direct start() test with official=true, developerRun=false, hasEdKey=true so supportsAutomaticUpdates
        // is the SOLE deciding guard. Removing the license check from start()/mayStart must fail this test.
        var active = false
        var startCount = 0
        let headers = [
            UpdateDownload.licenseKeyHeader: "BB-TEST",
            UpdateDownload.activationHeader: "act-1",
            UpdateDownload.installationHeader: "inst-1",
        ]
        let checker = UpdateChecker(
            downloadHeadersProvider: { active ? headers : nil },
            isOfficialBuildProvider: { true },
            isDeveloperRunProvider: { false },
            hasEdKeyProvider: { true },
            startUpdater: { _ in startCount += 1 }
        )

        checker.start()
        XCTAssertFalse(checker.isRunning)
        XCTAssertEqual(startCount, 0)

        // Activating the license starts Sparkle exactly once.
        active = true
        checker.supporterStatusChanged()
        XCTAssertTrue(checker.isRunning)
        XCTAssertEqual(startCount, 1)

        // Calling start() again or deactivating and re-activating never double-starts Sparkle.
        checker.start()
        XCTAssertEqual(startCount, 1)

        active = false
        checker.supporterStatusChanged()
        XCTAssertTrue(checker.isRunning)
        XCTAssertFalse(checker.updater.automaticallyChecksForUpdates)
        XCTAssertFalse(checker.updater.automaticallyDownloadsUpdates)

        active = true
        checker.supporterStatusChanged()
        XCTAssertEqual(startCount, 1)
    }

    @MainActor
    func testMayPerformUpdateCheckFailsClosedWhenUnlicensed() throws {
        var active = false
        let headers = [
            UpdateDownload.licenseKeyHeader: "BB-TEST",
            UpdateDownload.activationHeader: "act-1",
            UpdateDownload.installationHeader: "inst-1",
        ]
        let checker = UpdateChecker(downloadHeadersProvider: { active ? headers : nil })

        // Verify Sparkle's ObjC delegate selector is wired up.
        XCTAssertTrue(checker.responds(to: NSSelectorFromString("updater:mayPerformUpdateCheck:error:")))

        // Unlicensed: every check kind (scheduled background, manual, probing) fails closed.
        for kind in [SPUUpdateCheck.updatesInBackground, .updates, .updateInformation] {
            checker.beginCheckingForTests()
            var thrown: Error?
            XCTAssertThrowsError(
                try checker.updater(checker.updater, mayPerform: kind)
            ) { thrown = $0 }
            let gateError = try XCTUnwrap(thrown as NSError?)
            XCTAssertEqual(gateError.domain, UpdateChecker.unlicensedGateErrorDomain)
            XCTAssertEqual(gateError.code, UpdateChecker.unlicensedGateErrorCode)
            XCTAssertEqual(checker.state, .idle)
            XCTAssertNil(checker.pendingVersion)
        }

        // Licensed: checks are permitted.
        active = true
        XCTAssertNoThrow(try checker.updater(checker.updater, mayPerform: .updatesInBackground))
        XCTAssertNoThrow(try checker.updater(checker.updater, mayPerform: .updates))

        // Losing the license closes the gate again for any scheduled check racing deactivation.
        active = false
        XCTAssertThrowsError(try checker.updater(checker.updater, mayPerform: .updatesInBackground))
    }

    @MainActor
    func testFeedURLIgnoresUserDefaultsSUFeedURLAndStartClearsIt() {
        let defaults = UserDefaults.standard
        let previousSUFeedURL = defaults.object(forKey: "SUFeedURL")
        let previousOverride = defaults.object(forKey: UpdateChecker.feedOverrideKey)
        defer {
            if let previousSUFeedURL {
                defaults.set(previousSUFeedURL, forKey: "SUFeedURL")
            } else {
                defaults.removeObject(forKey: "SUFeedURL")
            }
            if let previousOverride {
                defaults.set(previousOverride, forKey: UpdateChecker.feedOverrideKey)
            } else {
                defaults.removeObject(forKey: UpdateChecker.feedOverrideKey)
            }
        }

        defaults.removeObject(forKey: UpdateChecker.feedOverrideKey)
        defaults.set("https://evil.example/appcast.xml", forKey: "SUFeedURL")

        let headers = [
            UpdateDownload.licenseKeyHeader: "BB-TEST",
            UpdateDownload.activationHeader: "act-1",
            UpdateDownload.installationHeader: "inst-1",
        ]
        var startCount = 0
        let checker = UpdateChecker(
            downloadHeadersProvider: { headers },
            isOfficialBuildProvider: { true },
            isDeveloperRunProvider: { false },
            hasEdKeyProvider: { true },
            startUpdater: { _ in startCount += 1 }
        )

        // feedURLString(for:) must NEVER return nil and must NEVER yield the injected UserDefaults SUFeedURL.
        let resolvedFeed = checker.feedURLString(for: checker.updater)
        XCTAssertNotNil(resolvedFeed)
        XCTAssertEqual(resolvedFeed, UpdateChecker.defaultFeedURL)
        XCTAssertNotEqual(resolvedFeed, "https://evil.example/appcast.xml")
        XCTAssertEqual(checker.updater.feedURL?.absoluteString, UpdateChecker.defaultFeedURL)

        // Starting the updater clears any persisted SUFeedURL from UserDefaults before Sparkle starts.
        checker.start()
        XCTAssertEqual(startCount, 1)
        XCTAssertNil(defaults.object(forKey: "SUFeedURL"))
    }

    func testOnboardingFeatureGatesCardsByCapabilities() {
        let singleNoCamera = OnboardingCapabilities(supportsCamera: false, hasMultiplePrinters: false)
        let gated = OnboardingFeature.cards(for: singleNoCamera)
        XCTAssertFalse(gated.contains(.camera))
        XCTAssertFalse(gated.contains(.fleet))
        XCTAssertEqual(gated.first, .menuBar)
        XCTAssertEqual(gated.last, .done)

        let multiWithCamera = OnboardingCapabilities(supportsCamera: true, hasMultiplePrinters: true)
        let full = OnboardingFeature.cards(for: multiWithCamera)
        XCTAssertTrue(full.contains(.camera))
        XCTAssertTrue(full.contains(.fleet))
    }
}
