import Foundation
import Security

/// The secrets a printer needs, in the keychain per serial number: its LAN access code, and the password of an
/// external camera entered for it. Two services, so neither write can disturb the other and printers saved by
/// earlier versions keep their codes where they are.
enum Keychain {
    private static let accessCodeService = "dev.rolottr.BambuBar.access-code"
    private static let cameraPasswordService = "dev.rolottr.BambuBar.camera-password"

    /// The item for a printer. The App Store build keeps its secrets in the data protection keychain, under its App
    /// Store identity: the login keychain would ask for permission (and block launch until answered) whenever it
    /// met one saved by the differently signed GitHub build. Its sandbox starts with settings of its own anyway.
    private static func item(_ serial: String, service: String) -> [String: Any] {
        var item: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: serial,
        ]
        #if APP_STORE
            item[kSecUseDataProtectionKeychain as String] = true
        #endif
        return item
    }

    // MARK: - Access codes

    static func accessCode(for serial: String) -> String? {
        value(serial, service: accessCodeService)
    }

    static func setAccessCode(_ code: String, for serial: String) {
        store(code, serial, service: accessCodeService, label: "Bambu Bar printer access code")
    }

    static func removeAccessCode(for serial: String) {
        SecItemDelete(item(serial, service: accessCodeService) as CFDictionary)
    }

    // MARK: - External camera passwords

    static func cameraPassword(for serial: String) -> String? {
        value(serial, service: cameraPasswordService)
    }

    static func setCameraPassword(_ password: String, for serial: String) {
        store(password, serial, service: cameraPasswordService, label: "Bambu Bar camera password")
    }

    static func removeCameraPassword(for serial: String) {
        SecItemDelete(item(serial, service: cameraPasswordService) as CFDictionary)
    }

    // MARK: - Items

    private static func value(_ serial: String, service: String) -> String? {
        var query = item(serial, service: service)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private static func store(_ secret: String, _ serial: String, service: String, label: String) {
        let data = Data(secret.utf8)
        let query = item(serial, service: service)
        if SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary) == errSecItemNotFound {
            var add = query
            add[kSecValueData as String] = data
            add[kSecAttrLabel as String] = label
            SecItemAdd(add as CFDictionary, nil)
        }
    }
}
