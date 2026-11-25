#if !APP_STORE
import Foundation
import Observation

public struct LicenseRecord: Codable, Equatable, Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    public let key: String
    public let activationID: String
    public let installationID: String
    public var validatedAt: Date?
    public var expiresAt: Date?

    public static let offlineGrace: TimeInterval = 7 * 24 * 60 * 60
    /// Tolerance for minor NTP backwards clock steps without revoking access.
    /// Rolling the clock backwards never extends `accessDeadline` (which is anchored to `validatedAt`).
    public static let clockSkewTolerance: TimeInterval = 60

    public init(key: String, activationID: String, installationID: String,
                validatedAt: Date? = nil, expiresAt: Date? = nil) {
        self.key = key
        self.activationID = activationID
        self.installationID = installationID
        self.validatedAt = validatedAt
        self.expiresAt = expiresAt
    }

    public func permitsOfflineUse(at now: Date) -> Bool {
        guard let validatedAt else { return false }
        let elapsed = now.timeIntervalSince(validatedAt)
        guard elapsed >= -Self.clockSkewTolerance,
              elapsed < Self.offlineGrace else { return false }
        return expiresAt.map { $0 > now } ?? true
    }

    /// Local entitlement deadline, independent of the next network revalidation.
    public var accessDeadline: Date? {
        guard let validatedAt, validatedAt.timeIntervalSince1970.isFinite else { return nil }
        let graceEnd = validatedAt.addingTimeInterval(Self.offlineGrace)
        guard let expiresAt else { return graceEnd }
        guard expiresAt.timeIntervalSince1970.isFinite else { return nil }
        return min(expiresAt, graceEnd)
    }

    public var installationLabel: String { "Mac · \(installationID.prefix(8))" }

    public var description: String { "LicenseRecord(redacted)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: [], displayStyle: .struct) }
}

protocol LicensePersistence {
    func load() throws -> LicenseRecord?
    func save(_ record: LicenseRecord?) throws
}

/// Delegates all Keychain I/O to `Keychain` (the single Keychain owner).
struct LicenseKeychain: LicensePersistence {
    static let service = "dev.rolottr.BambuBar.polar"
    static let account = "license"

    func load() throws -> LicenseRecord? {
        do {
            guard let data = try Keychain.readData(service: Self.service, account: Self.account) else { return nil }
            do {
                return try JSONDecoder().decode(LicenseRecord.self, from: data)
            } catch {
                throw LicenseError.storage(errSecDecode)
            }
        } catch let error as KeychainStorageError {
            throw LicenseError.storage(error.status)
        }
    }

    func save(_ record: LicenseRecord?) throws {
        do {
            guard let record else {
                try Keychain.deleteData(service: Self.service, account: Self.account)
                return
            }
            let data = try JSONEncoder().encode(record)
            try Keychain.writeData(data, service: Self.service, account: Self.account, label: "Bambu Bar license")
        } catch let error as KeychainStorageError {
            throw LicenseError.storage(error.status)
        }
    }
}

/// In-memory license persistence for developer runs (`D-09`) and unit tests.
final class MemoryLicensePersistence: LicensePersistence {
    var record: LicenseRecord?
    init(_ record: LicenseRecord? = nil) { self.record = record }
    func load() throws -> LicenseRecord? { record }
    func save(_ record: LicenseRecord?) throws { self.record = record }
}

@MainActor @Observable
final class LicenseStore {
    static let shared = LicenseStore(persistence: LaunchContext.isDeveloperRun ? MemoryLicensePersistence() : LicenseKeychain())

    static let revalidationInterval: TimeInterval = 15 * 60
    private static let didBecomeActiveNotification = Notification.Name("NSApplicationDidBecomeActiveNotification")

    private(set) var hasAccess = false
    private(set) var isInitializing = true
    private(set) var isBusy = false
    private(set) var message: String?
    private(set) var record: LicenseRecord?
    @ObservationIgnored var onAccessChange: ((Bool) -> Void)?

    @ObservationIgnored private let api: any LicenseAPI
    @ObservationIgnored private let persistence: any LicensePersistence
    @ObservationIgnored private let benefitID: String
    @ObservationIgnored private let now: () -> Date
    @ObservationIgnored private let notificationCenter: NotificationCenter
    @ObservationIgnored private var schedule: Task<Void, Never>?
    @ObservationIgnored private var accessDeadlineTask: Task<Void, Never>?
    @ObservationIgnored private var activeObserver: NSObjectProtocol?
    @ObservationIgnored private var storageReady = false
    @ObservationIgnored private(set) var lastRefreshAttempt: Date?

    init(api: any LicenseAPI = PolarClient(),
         persistence: any LicensePersistence = LicenseKeychain(),
         benefitID: String = PolarConfiguration.benefitID,
         now: @escaping () -> Date = Date.init,
         notificationCenter: NotificationCenter = .default) {
        self.api = api
        self.persistence = persistence
        self.benefitID = benefitID
        self.now = now
        self.notificationCenter = notificationCenter
    }

    var isConfigured: Bool { PolarConfiguration.validBenefit(benefitID) }

    func start() {
        // Developer runs never touch the real Keychain or Polar.
        if LaunchContext.isDeveloperRun {
            isInitializing = false
            return
        }
        guard schedule == nil else { return }
        activeObserver = notificationCenter.addObserver(
            forName: Self.didBecomeActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.handleDidBecomeActive()
            }
        }
        schedule = Task { [weak self] in
            await self?.refresh()
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(Self.revalidationInterval)) } catch { return }
                await self?.refresh()
            }
        }
    }

    /// Runs synchronous deadline enforcement on every activation, and only triggers a network
    /// revalidation when no refresh has been attempted within `revalidationInterval`.
    func handleDidBecomeActive() {
        enforceAccessDeadline()
        guard shouldRefreshOnActivation() else { return }
        Task { await self.refresh() }
    }

    private func shouldRefreshOnActivation() -> Bool {
        guard let lastRefreshAttempt else { return true }
        let elapsed = now().timeIntervalSince(lastRefreshAttempt)
        return elapsed >= Self.revalidationInterval || elapsed < -LicenseRecord.clockSkewTolerance
    }

    func stop() {
        schedule?.cancel()
        schedule = nil
        accessDeadlineTask?.cancel()
        accessDeadlineTask = nil
        if let activeObserver {
            notificationCenter.removeObserver(activeObserver)
            self.activeObserver = nil
        }
    }

    /// Synchronous local offline-grace check on `didBecomeActive` and when the deadline timer fires.
    /// Catches expired grace or a rolled-back clock immediately without waiting for network.
    func enforceAccessDeadline() {
        guard hasAccess else { return }
        guard let record, record.permitsOfflineUse(at: now()) else {
            setAccess(false)
            message = String(localized: "Connect to the internet to restore supporter benefits. Printer monitoring remains available.")
            return
        }
        scheduleAccessDeadline()
    }

    private func scheduleAccessDeadline() {
        accessDeadlineTask?.cancel()
        accessDeadlineTask = nil
        guard let deadline = record?.accessDeadline else { return }
        let delay = max(0, deadline.timeIntervalSince(now()))
        accessDeadlineTask = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(delay)) } catch { return }
            guard !Task.isCancelled else { return }
            self?.enforceAccessDeadline()
        }
    }

    func refresh() async {
        guard !isBusy else { return }
        guard isConfigured else {
            message = LicenseError.notConfigured.localizedDescription
            setAccess(false)
            isInitializing = false
            return
        }
        isBusy = true
        lastRefreshAttempt = now()
        defer {
            isBusy = false
            isInitializing = false
        }
        do {
            if !storageReady {
                record = try persistence.load()
                storageReady = true
            }
            guard var saved = record else {
                setAccess(false)
                return
            }
            do {
                let license = try await api.validate(saved)
                try license.verify(activationID: saved.activationID, now: now(), benefitID: benefitID)
                saved.validatedAt = now()
                saved.expiresAt = license.expiresAt
            } catch let error as LicenseError where error.isRefusal {
                // Retain the activation so a refusal never silently claims a new slot.
                saved.validatedAt = nil
                record = saved
                setAccess(false)
                try? persistence.save(saved)
                message = error.localizedDescription
                return
            } catch {
                // Outage, captive portal, or unexpected/malformed 200 JSON: preserve bounded offline grace.
                setAccess(saved.permitsOfflineUse(at: now()))
                message = hasAccess
                    ? String(localized: "The licensing service is temporarily unreachable. Supporter benefits remain active for up to seven days after your last successful license check.")
                    : String(localized: "Connect to the internet to restore supporter benefits. Printer monitoring remains available.")
                return
            }

            // Persist before extending grace. A transient Keychain write failure is not a refusal:
            // preserve the previous record's bounded offline grace and let the next check retry.
            do {
                try persistence.save(saved)
            } catch {
                setAccess(record?.permitsOfflineUse(at: now()) ?? false)
                message = error.localizedDescription
                return
            }
            record = saved
            lastRefreshAttempt = now()
            message = nil
            setAccess(true)
        } catch {
            setAccess(false)
            message = error.localizedDescription
        }
    }

    func activate(key: String) async {
        guard !isBusy else { return }
        guard isConfigured else {
            message = LicenseError.notConfigured.localizedDescription
            return
        }
        let key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        guard key.utf8.count <= 4096,
              !key.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            message = LicenseError.invalidKey.localizedDescription
            return
        }
        isBusy = true
        defer {
            isBusy = false
            isInitializing = false
        }
        do {
            if !storageReady {
                record = try persistence.load()
                storageReady = true
            }
            guard record == nil else {
                message = String(localized: "Deactivate this installation before entering a different license. Use Check License to retry your saved activation.")
                return
            }
            let installationID = UUID().uuidString.lowercased()
            let activation = try await api.activate(key: key, installationID: installationID)
            let saved = LicenseRecord(key: key, activationID: activation.id, installationID: installationID,
                                      validatedAt: now(), expiresAt: activation.licenseKey.expiresAt)
            do {
                guard UUID(uuidString: activation.id) != nil else { throw LicenseError.invalidResponse }
                try activation.licenseKey.verify(now: now(), benefitID: benefitID)
                try persistence.save(saved)
            } catch {
                // If verification or local storage fails, release the newly claimed slot when possible.
                try? await api.deactivate(saved)
                throw error
            }
            record = saved
            lastRefreshAttempt = now()
            message = nil
            setAccess(true)
        } catch LicenseError.invalidKey {
            message = LicenseError.invalidKey.localizedDescription
        } catch LicenseError.activationLimit {
            message = LicenseError.activationLimit.localizedDescription
        } catch {
            message = String(localized: "\(error.localizedDescription) If an activation appeared in your account, remove it there before retrying.", comment: "A licensing error message, then advice")
        }
    }

    func deactivate() async {
        guard !isBusy, var saved = record else { return }
        isBusy = true
        defer { isBusy = false }
        do {
            try await api.deactivate(saved)
            // Never keep access after server-side deactivation, even if local deletion fails.
            saved.validatedAt = nil
            record = saved
            setAccess(false)
            try persistence.save(nil)
            record = nil
            message = nil
        } catch {
            message = error.localizedDescription
        }
    }

    private func setAccess(_ requested: Bool) {
        let value = requested && (record?.permitsOfflineUse(at: now()) == true)
        if value {
            scheduleAccessDeadline()
        } else {
            accessDeadlineTask?.cancel()
            accessDeadlineTask = nil
        }
        guard value != hasAccess || isInitializing else { return }
        hasAccess = value
        onAccessChange?(value)
    }
}
#endif
