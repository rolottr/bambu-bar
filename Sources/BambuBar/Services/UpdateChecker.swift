import AppKit
import Foundation
import Observation
#if !APP_STORE
    import Sparkle
#endif

/// Supporter update discovery reads GitHub's latest release metadata once a day (no account or identifiers).
/// Sparkle installs newer releases through their signed appcast. A newer version opens Sparkle's window with its release notes and Install Update, Skip This
/// Version or Remind Me Later; installing downloads it, checks its EdDSA signature and Developer ID, swaps the app
/// and relaunches it.
///
/// The App Store build has none of this: the App Store keeps its copies up to date and App Review doesn't allow apps
/// to update themselves, so its target doesn't even link Sparkle and the update controls hide.
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

    nonisolated static let repository = "rolottr/bambu-bar"

    let currentVersion = AppInfo.version
    private(set) var state: State = .idle
    private(set) var lastChecked: Date?
    /// Hidden from the menu bar panel for this run ("Later", or Remind Me Later in Sparkle's window).
    private(set) var offerDismissed = false
    private(set) var skippedVersion: String?
    private(set) var isRunning = false

    // Preserve the user's choices while supporter validation is pending or unavailable.
    private var automaticChecksPreference = true
    private var automaticDownloadsPreference = false

    var supportsAutomaticUpdates: Bool {
        #if APP_STORE
        false
        #else
        LicenseStore.shared.hasAccess
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

    var canCheckForUpdates: Bool { isRunning && supportsAutomaticUpdates }

    var available: Release? {
        guard supportsAutomaticUpdates else { return nil }
        if case .available(let release) = state { return release }
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

        /// `-UpdateFeedURL <url>` on the command line points a build at another appcast, to try an update end to
        /// end. The update still has to carry this app's EdDSA signature.
        nonisolated private static var feedOverride: String? {
            UserDefaults.standard.volatileDomain(forName: UserDefaults.argumentDomain)["UpdateFeedURL"] as? String
        }

        @ObservationIgnored private var controller: SPUStandardUpdaterController!
        @ObservationIgnored private var metadataTask: Task<Void, Never>?
        @ObservationIgnored private var metadataTimer: Timer?

        private override init() {
            super.init()
            controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)
        }

        private var updater: SPUUpdater { controller.updater }

        /// Starts Sparkle's daily schedule. Demo, showcase and probe runs leave it off, and so do builds Xcode runs
        /// directly: only build.sh adds Sparkle's settings (Resources/Sparkle-Info.plist).
        func start() {
            guard supportsAutomaticUpdates, !isRunning, Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") != nil,
                  !LaunchContext.isDeveloperRun || Self.feedOverride != nil else { return }
            migrateSettings()
            // Disable persisted Sparkle automation before starting its scheduler.
            applySettings()
            controller.startUpdater()
            isRunning = true
            syncSettings()
            applySettings()
        }

        /// Check GitHub metadata first: older releases need no appcast and never trigger a downgrade.
        func check() { checkGitHub(interactive: false, scheduled: false) }
        func showUpdate() { checkGitHub(interactive: true, scheduled: false) }

        private func checkGitHub(interactive: Bool, scheduled: Bool) {
            guard canCheckForUpdates, metadataTask == nil else { return }
            if updater.sessionInProgress {
                if interactive { controller.checkForUpdates(nil) }
                return
            }
            // A developer-provided feed is still signature-checked by Sparkle.
            if Self.feedOverride != nil {
                if interactive { controller.checkForUpdates(nil) }
                else if scheduled { updater.checkForUpdatesInBackground() }
                else { updater.checkForUpdateInformation() }
                return
            }
            state = .checking
            metadataTask = Task { [weak self] in
                guard let self else { return }
                defer { metadataTask = nil }
                do {
                    var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repository)/releases/latest")!)
                    request.timeoutInterval = 30
                    request.cachePolicy = .reloadIgnoringLocalCacheData
                    request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
                    request.setValue("BambuBar/\(currentVersion)", forHTTPHeaderField: "User-Agent")
                    let (data, response) = try await URLSession.shared.data(for: request)
                    try Task.checkCancellation()
                    guard canCheckForUpdates else { return }
                    guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw GitHubReleaseError.unavailable }
                    let latest = try JSONDecoder().decode(GitHubRelease.self, from: data)
                    let version = try latest.newerVersion(than: currentVersion)
                    lastChecked = Date()
                    UserDefaults.standard.set(lastChecked, forKey: "supporterLastUpdateCheck")
                    guard let version else {
                        state = .upToDate
                        if interactive { MainWindow.show(.settings) }
                        return
                    }
                    guard latest.hasUpdateFeed else { throw GitHubReleaseError.missingUpdateFeed }
                    state = .available(Release(version: version))
                    offerDismissed = false
                    if interactive { controller.checkForUpdates(nil) }
                    else if scheduled && checksAutomatically { updater.checkForUpdatesInBackground() }
                } catch {
                    guard !Task.isCancelled, canCheckForUpdates else { return }
                    state = .failed(error.localizedDescription)
                    if interactive { MainWindow.show(.settings) }
                }
            }
        }

        func supporterStatusChanged() {
            if supportsAutomaticUpdates { start() }
            if isRunning { applySettings() }
            if !supportsAutomaticUpdates {
                metadataTask?.cancel()
                state = .idle
                offerDismissed = true
            }
        }

        private func applySettings() {
            // Our metadata timer schedules checks; Sparkle only runs after a newer signed release is found.
            updater.automaticallyChecksForUpdates = false
            updater.automaticallyDownloadsUpdates = downloadsAutomatically
            if isRunning && checksAutomatically && metadataTimer == nil {
                let interval: TimeInterval = 24 * 60 * 60
                let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
                    MainActor.assumeIsolated { self?.checkGitHub(interactive: false, scheduled: true) }
                }
                timer.fireDate = Date().addingTimeInterval(max(1, interval - Date().timeIntervalSince(lastChecked ?? .distantPast)))
                RunLoop.main.add(timer, forMode: .common)
                metadataTimer = timer
            } else if !checksAutomatically {
                metadataTimer?.invalidate()
                metadataTimer = nil
            }
        }

        /// The checkbox in Sparkle's window can change automatic downloads behind the toggles' back.
        private func syncSettings() {
            if supportsAutomaticUpdates {
                downloadsAutomatically = updater.automaticallyDownloadsUpdates
            } else {
                applySettings()
            }
            if let checked = updater.lastUpdateCheckDate, checked > (lastChecked ?? .distantPast) {
                lastChecked = checked
                UserDefaults.standard.set(checked, forKey: "supporterLastUpdateCheck")
            }
        }

        /// Up to 1.3 the app checked GitHub itself; keep a switched-off automatic check switched off.
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

        private func found(_ item: SUAppcastItem) {
            guard supportsAutomaticUpdates else { return }
            let release = Release(version: item.displayVersionString)
            if available != release { offerDismissed = false }
            state = .available(release)
        }
    #endif
}

#if !APP_STORE
    // Sparkle calls its delegates on the main thread.
    extension UpdateChecker: SPUUpdaterDelegate {
        nonisolated func feedURLString(for updater: SPUUpdater) -> String? { Self.feedOverride }

        // Guard every check, including manual checks and revocation while fetching the feed.
        nonisolated func updater(_ updater: SPUUpdater, mayPerform updateCheck: SPUUpdateCheck) throws {
            try MainActor.assumeIsolated { try requireSupporter() }
        }

        nonisolated func updater(_ updater: SPUUpdater, shouldProceedWithUpdate item: SUAppcastItem,
                                 updateCheck: SPUUpdateCheck) throws {
            try MainActor.assumeIsolated { try requireSupporter() }
        }

        private func requireSupporter() throws {
            if !supportsAutomaticUpdates {
                throw NSError(domain: "BambuBar.Updates", code: 1,
                              userInfo: [NSLocalizedDescriptionKey: String(localized: "In-app update checks require an active website supporter license.")])
            }
        }


        nonisolated func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
            MainActor.assumeIsolated { found(item) }
        }

        nonisolated func updaterDidNotFindUpdate(_ updater: SPUUpdater) {
            MainActor.assumeIsolated { state = .upToDate }
        }

        nonisolated func updater(_ updater: SPUUpdater, userDidMake choice: SPUUserUpdateChoice,
                                 forUpdate updateItem: SUAppcastItem, state: SPUUserUpdateState) {
            let version = updateItem.displayVersionString
            MainActor.assumeIsolated {
                switch choice {
                case .skip: skippedVersion = version
                case .dismiss: offerDismissed = true
                case .install: break
                @unknown default: break
                }
            }
        }

        nonisolated func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: (any Error)?) {
            let code = (error as NSError?).flatMap { $0.domain == SUSparkleErrorDomain ? $0.code : nil }
            let failed = error != nil && code != Int(SUError.noUpdateError.rawValue)
                && code != Int(SUError.installationCanceledError.rawValue)
            MainActor.assumeIsolated {
                if !supportsAutomaticUpdates { state = .idle }
                else if state == .checking {
                    state = failed ? .failed(String(localized: "Couldn’t reach GitHub. Check your connection and try again.")) : .upToDate
                }
                syncSettings()
            }
        }
    }

    extension UpdateChecker: SPUStandardUserDriverDelegate {
        /// A menu bar app has no window to wait for: Sparkle shows its window right away (in front shortly after
        /// launch or while the Mac sat idle, otherwise behind other apps) and the menu bar panel offers the update too.
        nonisolated var supportsGentleScheduledUpdateReminders: Bool { true }

        nonisolated func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem,
                                                                              andInImmediateFocus immediateFocus: Bool) -> Bool {
            true
        }

        nonisolated func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem,
                                                                   state: SPUUserUpdateState) {
            MainActor.assumeIsolated { found(update) }
        }
    }
#endif
