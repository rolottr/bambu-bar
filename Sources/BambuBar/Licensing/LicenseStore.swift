#if !APP_STORE
import Foundation
import Observation
import Security

struct LicenseRecord: Codable {
    let key: String
    let activationID: String
    let installationID: String
    var validatedAt: Date?
    var expiresAt: Date?

    func permitsOfflineUse(at now: Date) -> Bool {
        guard let validatedAt, now >= validatedAt,
              now.timeIntervalSince(validatedAt) < 7 * 24 * 60 * 60 else { return false }
        return expiresAt.map { $0 > now } ?? true
    }
}

protocol LicensePersistence {
    func load() throws -> LicenseRecord?
    func save(_ record: LicenseRecord?) throws
}

/// A local, non-synchronizing Keychain item survives upgrades without consuming another activation.
struct LicenseKeychain: LicensePersistence {
    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: "dev.rolottr.BambuBar.polar",
         kSecAttrAccount as String: "license", kSecAttrSynchronizable as String: false]
    }
    func load() throws -> LicenseRecord? {
        var query = query
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else { throw LicenseError.storage(status) }
        return try JSONDecoder().decode(LicenseRecord.self, from: data)
    }
    func save(_ record: LicenseRecord?) throws {
        guard let record else {
            let status = SecItemDelete(query as CFDictionary)
            guard status == errSecSuccess || status == errSecItemNotFound else { throw LicenseError.storage(status) }
            return
        }
        let data = try JSONEncoder().encode(record)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            item[kSecValueData as String] = data
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            let added = SecItemAdd(item as CFDictionary, nil)
            guard added == errSecSuccess else { throw LicenseError.storage(added) }
        } else if status != errSecSuccess { throw LicenseError.storage(status) }
    }
}

@MainActor @Observable
final class LicenseStore {
    static let shared = LicenseStore()
    private(set) var hasAccess = false
    private(set) var isInitializing = true
    private(set) var isBusy = false
    private(set) var message: String?
    private(set) var record: LicenseRecord?
    var onAccessChange: ((Bool) -> Void)?
    private let api: any LicenseAPI
    private let persistence: any LicensePersistence
    private let now: () -> Date
    private var schedule: Task<Void, Never>?
    private var storageReady = false

    init(api: any LicenseAPI = PolarClient(), persistence: any LicensePersistence = LicenseKeychain(), now: @escaping () -> Date = Date.init) {
        self.api = api
        self.persistence = persistence
        self.now = now
    }

    func start() {
        guard schedule == nil else { return }
        schedule = Task { [weak self] in
            await self?.refresh()
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(15 * 60)) } catch { return }
                await self?.refresh()
            }
        }
    }

    func refresh() async {
        guard !isBusy else { return }
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
            guard var saved = record else { setAccess(false); return }
            do {
                let license = try await api.validate(saved)
                try license.verify(activationID: saved.activationID, now: now())
                saved.validatedAt = now()
                saved.expiresAt = license.expiresAt
                // Persist before enabling: a failed Keychain write is not an offline network failure.
                try persistence.save(saved)
                record = saved
                message = nil
                setAccess(true)
            } catch LicenseError.unavailable {
                setAccess(saved.permitsOfflineUse(at: now()))
                message = hasAccess
                    ? String(localized: "The licensing service is temporarily unreachable. Supporter benefits remain active for up to seven days after your last successful license check.")
                    : String(localized: "Connect to the internet to restore supporter benefits. Printer monitoring remains available.")
            } catch {
                // Retain the activation so a refusal never silently claims a new slot.
                saved.validatedAt = nil
                record = saved
                setAccess(false)
                try persistence.save(saved)
                throw error
            }
        } catch {
            setAccess(false)
            message = error.localizedDescription
        }
    }

    func activate(key: String) async {
        guard !isBusy else { return }
        let key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        isBusy = true
        defer {
            isBusy = false
            isInitializing = false
        }
        do {
            if !storageReady { record = try persistence.load(); storageReady = true }
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
                try activation.licenseKey.verify(now: now())
                try persistence.save(saved)
            } catch {
                // If local storage fails, release the newly claimed slot when possible.
                try? await api.deactivate(saved)
                throw error
            }
            record = saved
            message = nil
            setAccess(true)
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
        } catch { message = error.localizedDescription }
    }

    private func setAccess(_ value: Bool) {
        guard value != hasAccess || isInitializing else { return }
        hasAccess = value
        onAccessChange?(value)
    }
}
#endif
