import AppKit
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

    // MARK: - OnboardingDraft Apply vs Skip & Capability Gating

    func testOnboardingDraftAppliesOnlyOnFinishAndNeverOnSkip() {
        let initial = OnboardingSettings(
            showProgressInMenuBar: true,
            notifyFinished: true,
            notifyFailed: true,
            notifyPaused: true,
            cameraEnabled: false,
            showPrinterActions: true,
            panelShowsAllPrinters: false
        )

        var draft = OnboardingDraft(from: initial)
        draft.toggle(.menuBar)
        draft.toggle(.notifications)
        draft.toggle(.camera)
        draft.toggle(.controls)
        draft.toggle(.fleet)

        // Simulating Skip: initial settings remain untouched.
        XCTAssertEqual(initial.showProgressInMenuBar, true)
        XCTAssertEqual(initial.cameraEnabled, false)

        // Simulating Finish: draft applies every toggled choice.
        let applied = draft.applied(to: initial)
        XCTAssertEqual(applied.showProgressInMenuBar, false)
        XCTAssertEqual(applied.notifyFinished, false)
        XCTAssertEqual(applied.notifyFailed, false)
        XCTAssertEqual(applied.notifyPaused, false)
        XCTAssertEqual(applied.cameraEnabled, true)
        XCTAssertEqual(applied.showPrinterActions, false)
        XCTAssertEqual(applied.panelShowsAllPrinters, true)
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
