import AppKit
import Foundation
import Observation
#if !APP_STORE
    import Combine
    import Security
    import Sparkle
    import os
#endif

/// In-app updates with Sparkle 2 on `https://bambubar.app/appcast.xml`.
///
/// The daily scheduled check reads the anonymous public feed at `https://bambubar.app/appcast.xml`
/// (`SUScheduledCheckInterval` = 86400). Only the update download request to
/// `https://bambubar.app/api/updates/download` carries `X-BambuBar-*` supporter license headers,
/// attached per-request in `updater(_:willDownloadUpdate:with:)` after verifying the URL with
/// `UpdateDownload.accepts(_:feed:)`. Every update must carry the shared Clever Mechanisms EdDSA
/// signature (`SUPublicEDKey`) and Developer ID signature.
///
/// The Mac App Store edition compiles with zero Sparkle symbols; the App Store handles updates.
@MainActor
@Observable
final class UpdateChecker: NSObject {
    static let shared = UpdateChecker()

    struct Release: Equatable {
        let version: String
    }

    enum State: Equatable {
        case idle, checking, upToDate, available(Release), failed(String)
    }

    let currentVersion = AppInfo.version
    private(set) var state: State = .idle
    private(set) var lastChecked: Date?
    /// Hidden from the menu bar panel for this run ("Later", or Remind Me Later in Sparkle's window).
    private(set) var offerDismissed = false
    private(set) var skippedVersion: String?
    private(set) var isRunning = false
    private(set) var pendingVersion: String?

    // Preserve the user's choices while supporter validation is pending or unavailable.
    private var automaticChecksPreference = true
    private var automaticDownloadsPreference = false
    private var sparkleCanCheckForUpdates = false

    var supportsAutomaticUpdates: Bool {
        #if APP_STORE
        false
        #else
        LicenseStore.shared.hasAccess && LicenseStore.shared.record != nil
        #endif
    }

    var checksAutomatically: Bool {
        get { supportsAutomaticUpdates && automaticChecksPreference }
        set {
            automaticChecksPreference = newValue
            UserDefaults.standard.set(newValue, forKey: "supporterAutomaticChecks")
            applySettings()
        }
    }

    var downloadsAutomatically: Bool {
        get { checksAutomatically && automaticDownloadsPreference }
        set {
            automaticDownloadsPreference = newValue
            UserDefaults.standard.set(newValue, forKey: "supporterAutomaticDownloads")
            applySettings()
        }
    }

    var canCheckForUpdates: Bool { isRunning && sparkleCanCheckForUpdates && supportsAutomaticUpdates }

    var available: Release? {
        guard supportsAutomaticUpdates else { return nil }
        if case .available(let release) = state { return release }
        if let pendingVersion { return Release(version: pendingVersion) }
        return nil
    }

    /// The release to offer in the menu bar panel: not skipped and not put off for later.
    var offered: Release? {
        guard let release = available, !offerDismissed, release.version != skippedVersion else { return nil }
        return release
    }

    func dismissOffer() { offerDismissed = true }

    #if APP_STORE
        nonisolated static let isAvailable = false

        private override init() {}
        func start() {}
        func check() {}
        func showUpdate() {}
        private func applySettings() {}
    #else
        nonisolated static let isAvailable = true
        nonisolated static let teamIdentifier = "PCZ6F8Q9DZ"
        nonisolated static let feedOverrideKey = "UpdateFeedURL"
        private static let log = Logger(subsystem: "dev.rolottr.BambuBar", category: "updates")

        /// `-UpdateFeedURL <url>` on the command line or `defaults write dev.rolottr.BambuBar UpdateFeedURL <url>`
        /// points a signed build at a local test appcast. Updates must still carry a valid EdDSA signature.
        nonisolated static var feedOverride: URL? {
            guard let string = UserDefaults.standard.string(forKey: feedOverrideKey),
                  !string.isEmpty else { return nil }
            return URL(string: string)
        }

        /// True only when running inside an official `.app` signed by Clever Mechanisms' Team ID (`PCZ6F8Q9DZ`).
        nonisolated static var isOfficialBuild: Bool {
            guard Bundle.main.bundleURL.pathExtension == "app" else { return false }
            var code: SecCode?
            var staticCode: SecStaticCode?
            var info: CFDictionary?
            guard SecCodeCopySelf([], &code) == errSecSuccess, let code,
                  SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode,
                  SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
                  let info = info as? [String: Any]
            else { return false }
            return info[kSecCodeInfoTeamIdentifier as String] as? String == teamIdentifier
        }

        @ObservationIgnored private var controller: SPUStandardUpdaterController!
        @ObservationIgnored private var observations: Set<AnyCancellable> = []

        private override init() {
            super.init()
            controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)
        }

        private var updater: SPUUpdater { controller.updater }

        /// Starts Sparkle only in an official `.app` signed by Team ID `PCZ6F8Q9DZ` with `SUPublicEDKey`
        /// configured, and never during developer runs (`--demo`, `--showcase`, `--readme-frames`, etc.).
        func start() {
            guard !isRunning,
                  Self.isOfficialBuild,
                  !LaunchContext.isDeveloperRun,
                  Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") != nil else { return }

            migrateSettings()
            // Sync Sparkle automation with current supporter status before starting its scheduler.
            applySettings()

            updater.publisher(for: \.canCheckForUpdates)
                .sink { [weak self] value in
                    MainActor.assumeIsolated { self?.sparkleCanCheckForUpdates = value }
                }
                .store(in: &observations)

            updater.publisher(for: \.automaticallyChecksForUpdates)
                .sink { [weak self] value in
                    MainActor.assumeIsolated {
                        guard let self, self.supportsAutomaticUpdates, self.automaticChecksPreference != value else { return }
                        self.automaticChecksPreference = value
                        UserDefaults.standard.set(value, forKey: "supporterAutomaticChecks")
                    }
                }
                .store(in: &observations)

            updater.publisher(for: \.automaticallyDownloadsUpdates)
                .sink { [weak self] value in
                    MainActor.assumeIsolated {
                        guard let self, self.supportsAutomaticUpdates, self.automaticDownloadsPreference != value else { return }
                        self.automaticDownloadsPreference = value
                        UserDefaults.standard.set(value, forKey: "supporterAutomaticDownloads")
                    }
                }
                .store(in: &observations)

            controller.startUpdater()
            isRunning = true
            sparkleCanCheckForUpdates = updater.canCheckForUpdates
            syncLastChecked()
            applySettings()
        }

        func check() {
            guard canCheckForUpdates else { return }
            state = .checking
            if updater.sessionInProgress {
                NSApp.activate(ignoringOtherApps: true)
                controller.checkForUpdates(nil)
            } else {
                updater.checkForUpdateInformation()
            }
        }

        func showUpdate() {
            guard canCheckForUpdates else { return }
            NSApp.activate(ignoringOtherApps: true)
            if state != .checking && available == nil {
                state = .checking
            }
            controller.checkForUpdates(nil)
        }

        func supporterStatusChanged() {
            if supportsAutomaticUpdates && !isRunning {
                start()
            }
            if isRunning {
                applySettings()
            }
            if !supportsAutomaticUpdates {
                pendingVersion = nil
                state = .idle
                offerDismissed = true
            }
        }

        private func applySettings() {
            let autoCheck = checksAutomatically
            if updater.automaticallyChecksForUpdates != autoCheck {
                updater.automaticallyChecksForUpdates = autoCheck
            }
            let autoDownload = downloadsAutomatically
            if updater.automaticallyDownloadsUpdates != autoDownload {
                updater.automaticallyDownloadsUpdates = autoDownload
            }
        }

        private func syncLastChecked() {
            if let checked = updater.lastUpdateCheckDate, checked > (lastChecked ?? .distantPast) {
                lastChecked = checked
                UserDefaults.standard.set(checked, forKey: "supporterLastUpdateCheck")
            }
        }

        private func migrateSettings() {
            let defaults = UserDefaults.standard
            lastChecked = defaults.object(forKey: "supporterLastUpdateCheck") as? Date
            automaticChecksPreference = defaults.object(forKey: "supporterAutomaticChecks") as? Bool
                ?? defaults.object(forKey: "updateChecksAutomatically") as? Bool
                ?? defaults.object(forKey: "SUEnableAutomaticChecks") as? Bool ?? true
            automaticDownloadsPreference = defaults.object(forKey: "supporterAutomaticDownloads") as? Bool
                ?? defaults.object(forKey: "SUAutomaticallyUpdate") as? Bool ?? false
            defaults.set(automaticChecksPreference, forKey: "supporterAutomaticChecks")
            defaults.set(automaticDownloadsPreference, forKey: "supporterAutomaticDownloads")
            for key in ["updateChecksAutomatically", "updateLastChecked", "updateSkippedVersion"] {
                defaults.removeObject(forKey: key)
            }
        }

        private var downloadHeaders: [String: String]? {
            let license = LicenseStore.shared
            guard license.hasAccess, let record = license.record else { return nil }
            return UpdateDownload.headers(for: record)
        }

        private func found(_ item: SUAppcastItem) {
            guard supportsAutomaticUpdates else { return }
            let release = Release(version: item.displayVersionString)
            if available != release { offerDismissed = false }
            pendingVersion = release.version
            state = .available(release)
        }
    #endif
}

#if !APP_STORE
    extension UpdateChecker: SPUUpdaterDelegate {
        nonisolated func feedURLString(for updater: SPUUpdater) -> String? {
            Self.feedOverride?.absoluteString
        }

        nonisolated func updater(_ updater: SPUUpdater, shouldProceedWithUpdate item: SUAppcastItem,
                                 updateCheck: SPUUpdateCheck) throws {
            try MainActor.assumeIsolated {
                guard downloadHeaders != nil else {
                    throw NSError(
                        domain: "dev.rolottr.BambuBar.updates",
                        code: 1,
                        userInfo: [NSLocalizedDescriptionKey: String(localized: "Activate your license to download updates.")]
                    )
                }
            }
        }

        /// Attach `X-BambuBar-*` license headers ONLY on verified download requests to
        /// `bambubar.app/api/updates/download` (or the local feed override origin). Never on feed checks.
        nonisolated func updater(_ updater: SPUUpdater, willDownloadUpdate item: SUAppcastItem,
                                 with request: NSMutableURLRequest) {
            MainActor.assumeIsolated {
                guard let url = request.url, UpdateDownload.accepts(url, feed: Self.feedOverride) else {
                    Self.log.error("Update download from an unexpected address; license not sent")
                    return
                }
                guard let headers = downloadHeaders else { return }
                for (field, value) in headers {
                    request.setValue(value, forHTTPHeaderField: field)
                }
            }
        }

        nonisolated func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
            MainActor.assumeIsolated { found(item) }
        }

        nonisolated func updaterDidNotFindUpdate(_ updater: SPUUpdater) {
            MainActor.assumeIsolated {
                pendingVersion = nil
                state = .upToDate
            }
        }

        nonisolated func updater(_ updater: SPUUpdater, userDidMake choice: SPUUserUpdateChoice,
                                 forUpdate updateItem: SUAppcastItem, state: SPUUserUpdateState) {
            let version = updateItem.displayVersionString
            MainActor.assumeIsolated {
                switch choice {
                case .skip:
                    skippedVersion = version
                    pendingVersion = nil
                case .dismiss:
                    offerDismissed = true
                case .install:
                    break
                @unknown default:
                    break
                }
            }
        }

        nonisolated func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: (any Error)?) {
            let nsError = error as NSError?
            let sparkleCode = nsError.flatMap { $0.domain == SUSparkleErrorDomain ? $0.code : nil }
            let failed = error != nil
                && sparkleCode != Int(SUError.noUpdateError.rawValue)
                && sparkleCode != Int(SUError.installationCanceledError.rawValue)
            let message = error?.localizedDescription ?? String(localized: "Couldn’t check for updates. Check your connection and try again.")
            MainActor.assumeIsolated {
                syncLastChecked()
                if !supportsAutomaticUpdates {
                    pendingVersion = nil
                    state = .idle
                } else if state == .checking {
                    state = failed ? .failed(message) : .upToDate
                }
            }
        }
    }

    extension UpdateChecker: SPUStandardUserDriverDelegate {
        nonisolated var supportsGentleScheduledUpdateReminders: Bool { true }

        nonisolated func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem,
                                                                              andInImmediateFocus immediateFocus: Bool) -> Bool {
            immediateFocus
        }

        nonisolated func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem,
                                                                   state: SPUUserUpdateState) {
            MainActor.assumeIsolated {
                if !handleShowingUpdate {
                    found(update)
                }
            }
        }

        nonisolated func standardUserDriverDidReceiveUserAttention(forUpdate update: SUAppcastItem) {
            MainActor.assumeIsolated {
                pendingVersion = nil
            }
        }

        nonisolated func standardUserDriverWillFinishUpdateSession() {
            MainActor.assumeIsolated {
                pendingVersion = nil
            }
        }
    }
#endif
