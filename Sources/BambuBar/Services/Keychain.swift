import Foundation
import Security

/// Single owner of all `SecItem*` Keychain calls in Bambu Bar (both printer LAN access codes and,
/// in direct builds, the Polar license record).
///
/// ## Verified macOS 26 Data-Protection Keychain behavior
/// - `kSecUseDataProtectionKeychain: true` is attempted unconditionally (not gated behind `#if APP_STORE`)
///   so App Store builds, Xcode Development builds, and Developer ID builds with an embedded Developer ID
///   provisioning profile all store secrets in the data-protection keychain with zero ACL prompts.
/// - On macOS 26, adding `keychain-access-groups` to `BambuBar.entitlements` *without* a matching embedded
///   Developer ID provisioning profile causes AMFI to terminate the process on launch (`SIGKILL`, exit 137).
///   Without that profile, `SecItemAdd`/`SecItemUpdate`/`SecItemDelete` with `kSecUseDataProtectionKeychain`
///   returns `errSecMissingEntitlement` (`-34018`). In that case this store transparently falls back to the
///   login keychain without deleting any existing item; once a build carries the data-protection entitlement,
///   the first read silently migrates legacy login-keychain items into the data-protection keychain and
///   removes the legacy copy.
/// - Every secret is cached in memory (read-once per process) and written only when its bytes change.
/// - Developer launches (`--demo`, `--emulator`, `--snapshot`, `--showcase`, `--readme-frames`,
///   `--store-media`, `--connect`, `--dump-status`) stay strictly in memory and never touch the real Keychain.
enum Keychain {
    static let accessCodeService = "dev.rolottr.BambuBar.access-code"

    /// Low-level Keychain backend, injectable for unit tests.
    protocol Backend: Sendable {
        func read(service: String, account: String, dataProtection: Bool) -> (OSStatus, Data?)
        func write(_ data: Data, service: String, account: String, label: String?, dataProtection: Bool) -> OSStatus
        func delete(service: String, account: String, dataProtection: Bool) -> OSStatus
    }

    private struct ItemKey: Hashable {
        let service: String
        let account: String
    }

    private final class State: @unchecked Sendable {
        let lock = NSLock()
        var backend: any Backend = SystemBackend()
        var forceMemoryOnly: Bool?
        var memoryItems: [ItemKey: Data] = [:]
        /// Read-once per-process cache. A key mapped to `nil` means confirmed absent.
        var cache: [ItemKey: Data?] = [:]
    }

    private static let state = State()

    private static let developerRunFlags: Set<String> = [
        "--demo",
        "--emulator",
        "--snapshot",
        "--showcase",
        "--readme-frames",
        "--store-media",
        "--connect",
        "--dump-status",
    ]

    /// True when this process was started with a developer/demo/snapshot flag that must never touch
    /// the user's real Keychain (D-09). `--probe` is excluded because `--probe ... keychain` explicitly
    /// asks to read a saved printer's access code.
    static var isDeveloperRun: Bool {
        CommandLine.arguments.contains { developerRunFlags.contains($0) }
    }

    private static var usesMemoryStore: Bool {
        state.lock.lock()
        let override = state.forceMemoryOnly
        state.lock.unlock()
        return override ?? isDeveloperRun
    }

    // MARK: - Printer LAN access codes

    static func accessCode(for serial: String) -> String? {
        guard let data = try? readData(service: accessCodeService, account: serial) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func setAccessCode(_ code: String, for serial: String) {
        try? writeData(Data(code.utf8), service: accessCodeService, account: serial, label: "Bambu Bar printer access code")
    }

    static func removeAccessCode(for serial: String) {
        try? deleteData(service: accessCodeService, account: serial)
    }

    // MARK: - Generic secret storage (shared with LicenseKeychain)

    static func readData(service: String, account: String) throws -> Data? {
        let key = ItemKey(service: service, account: account)
        state.lock.lock()
        defer { state.lock.unlock() }

        if let cached = state.cache[key] {
            return cached
        }
        if state.forceMemoryOnly ?? isDeveloperRun {
            let value = state.memoryItems[key]
            state.cache[key] = .some(value)
            return value
        }

        let backend = state.backend
        let (dpStatus, dpData) = backend.read(service: service, account: account, dataProtection: true)
        if dpStatus == errSecSuccess {
            state.cache[key] = .some(dpData)
            return dpData
        }
        if dpStatus != errSecItemNotFound && dpStatus != errSecMissingEntitlement {
            throw KeychainStorageError(status: dpStatus)
        }

        // Not found in data-protection keychain (or unentitled build): check legacy login keychain.
        let (legacyStatus, legacyData) = backend.read(service: service, account: account, dataProtection: false)
        if legacyStatus == errSecItemNotFound {
            state.cache[key] = .some(nil)
            return nil
        }
        guard legacyStatus == errSecSuccess else {
            throw KeychainStorageError(status: legacyStatus)
        }

        // One-time silent migration: copy legacy item into data-protection keychain and delete the legacy copy
        // only when the data-protection write succeeds.
        if let legacyData, dpStatus != errSecMissingEntitlement {
            let migrateStatus = backend.write(legacyData, service: service, account: account, label: nil, dataProtection: true)
            if migrateStatus == errSecSuccess {
                _ = backend.delete(service: service, account: account, dataProtection: false)
            }
        }

        state.cache[key] = .some(legacyData)
        return legacyData
    }

    static func writeData(_ data: Data, service: String, account: String, label: String? = nil) throws {
        let key = ItemKey(service: service, account: account)
        state.lock.lock()
        defer { state.lock.unlock() }

        // Write only when values change.
        if let cached = state.cache[key], cached == data {
            return
        }
        if state.forceMemoryOnly ?? isDeveloperRun {
            state.memoryItems[key] = data
            state.cache[key] = .some(data)
            return
        }

        let backend = state.backend
        let dpStatus = backend.write(data, service: service, account: account, label: label, dataProtection: true)
        if dpStatus == errSecSuccess {
            state.cache[key] = .some(data)
            // Remove any stale legacy login-keychain copy now that data-protection holds the truth.
            _ = backend.delete(service: service, account: account, dataProtection: false)
            return
        }
        if dpStatus == errSecMissingEntitlement {
            let legacyStatus = backend.write(data, service: service, account: account, label: label, dataProtection: false)
            guard legacyStatus == errSecSuccess else { throw KeychainStorageError(status: legacyStatus) }
            state.cache[key] = .some(data)
            return
        }
        throw KeychainStorageError(status: dpStatus)
    }

    static func deleteData(service: String, account: String) throws {
        let key = ItemKey(service: service, account: account)
        state.lock.lock()
        defer { state.lock.unlock() }

        if state.forceMemoryOnly ?? isDeveloperRun {
            state.memoryItems.removeValue(forKey: key)
            state.cache[key] = .some(nil)
            return
        }

        let backend = state.backend
        let dpStatus = backend.delete(service: service, account: account, dataProtection: true)
        if dpStatus != errSecSuccess && dpStatus != errSecItemNotFound && dpStatus != errSecMissingEntitlement {
            throw KeychainStorageError(status: dpStatus)
        }
        let legacyStatus = backend.delete(service: service, account: account, dataProtection: false)
        if legacyStatus != errSecSuccess && legacyStatus != errSecItemNotFound {
            throw KeychainStorageError(status: legacyStatus)
        }
        state.cache[key] = .some(nil)
    }

    // MARK: - Testing hooks

    static func resetForTesting(backend: (any Backend)? = nil, memoryOnly: Bool? = nil) {
        state.lock.lock()
        defer { state.lock.unlock() }
        state.backend = backend ?? SystemBackend()
        state.forceMemoryOnly = memoryOnly
        state.memoryItems.removeAll()
        state.cache.removeAll()
    }

    // MARK: - System Security.framework backend (sole SecItem* owner)

    private struct SystemBackend: Backend {
        private func baseQuery(service: String, account: String, dataProtection: Bool) -> [String: Any] {
            var query: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: service,
                kSecAttrAccount as String: account,
                kSecAttrSynchronizable as String: false,
            ]
            if dataProtection {
                query[kSecUseDataProtectionKeychain as String] = true
            }
            return query
        }

        func read(service: String, account: String, dataProtection: Bool) -> (OSStatus, Data?) {
            var query = baseQuery(service: service, account: account, dataProtection: dataProtection)
            query[kSecReturnData as String] = true
            query[kSecMatchLimit as String] = kSecMatchLimitOne
            var result: CFTypeRef?
            let status = SecItemCopyMatching(query as CFDictionary, &result)
            guard status == errSecSuccess else { return (status, nil) }
            return (status, result as? Data)
        }

        func write(_ data: Data, service: String, account: String, label: String?, dataProtection: Bool) -> OSStatus {
            let query = baseQuery(service: service, account: account, dataProtection: dataProtection)
            let updateStatus = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
            if updateStatus != errSecItemNotFound {
                return updateStatus
            }
            var item = query
            item[kSecValueData as String] = data
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            if let label {
                item[kSecAttrLabel as String] = label
            }
            return SecItemAdd(item as CFDictionary, nil)
        }

        func delete(service: String, account: String, dataProtection: Bool) -> OSStatus {
            SecItemDelete(baseQuery(service: service, account: account, dataProtection: dataProtection) as CFDictionary)
        }
    }
}

struct KeychainStorageError: Error, Equatable {
    let status: OSStatus
}
