#if !APP_STORE
import Foundation
import Security

/// Public customer endpoints only. An organization access token must never ship in the app.
enum PolarConfiguration {
    static let organizationID = "2925c2bc-549b-4b87-96ca-83d2268817a6"
    static let benefitID = "3560f84c-991a-4d50-9b5d-1009584204dd"
    static let portalURL = URL(string: "https://polar.sh/nina-labs-ai-llc/portal")!

    enum SupportPlacement: String {
        case mainWindow = "main_window"
        case appMenu = "app_menu"
        case menuPanel = "menu_panel"
        case license = "license"
    }

    static func supportURL(from placement: SupportPlacement) -> URL {
        Website.url("download", campaign: "support_project", content: placement.rawValue)
    }
}

enum LicenseError: LocalizedError {
    case rejected, activationLimit, unavailable, invalidResponse, storage(OSStatus)
    var errorDescription: String? {
        switch self {
        case .rejected: return String(localized: "This license or activation is no longer valid. Check your license in your account, then deactivate this installation before activating again.")
        case .activationLimit: return String(localized: "This license could not be activated. If all installations are in use, deactivate one in your account, then try again.")
        case .unavailable: return String(localized: "Cannot reach the licensing service right now. Check your internet connection and try again.")
        case .invalidResponse: return String(localized: "The licensing service returned an unexpected response. Please try again later.")
        case .storage: return String(localized: "The license could not be saved in your Mac’s Keychain. Check Keychain access and try again.")
        }
    }
}

struct PolarLicense: Decodable {
    let organizationId: String
    let benefitId: String
    let status: String
    let expiresAt: Date?
    let activation: Activation?
    struct Activation: Decodable { let id: String }

    func verify(activationID: String? = nil, now: Date = Date()) throws {
        guard organizationId == PolarConfiguration.organizationID,
              benefitId == PolarConfiguration.benefitID, status == "granted",
              expiresAt.map({ $0 > now }) ?? true,
              activationID == nil || activation?.id == activationID else { throw LicenseError.rejected }
    }
}

struct PolarActivation: Decodable {
    let id: String
    let licenseKey: PolarLicense
}

protocol LicenseAPI {
    func activate(key: String, installationID: String) async throws -> PolarActivation
    func validate(_ record: LicenseRecord) async throws -> PolarLicense
    func deactivate(_ record: LicenseRecord) async throws
}

struct PolarClient: LicenseAPI {
    var session: URLSession = .shared

    func activate(key: String, installationID: String) async throws -> PolarActivation {
        let data = try await request("activate", fields: [
            "key": key, "label": "Mac · \(installationID.prefix(8))",
            "conditions": ["installation_id": installationID],
        ])
        return try Self.decoder().decode(PolarActivation.self, from: data)
    }

    func validate(_ record: LicenseRecord) async throws -> PolarLicense {
        let data = try await request("validate", fields: [
            "key": record.key, "activation_id": record.activationID,
            "benefit_id": PolarConfiguration.benefitID,
            "conditions": ["installation_id": record.installationID],
        ])
        return try Self.decoder().decode(PolarLicense.self, from: data)
    }

    func deactivate(_ record: LicenseRecord) async throws {
        _ = try await request("deactivate", fields: ["key": record.key, "activation_id": record.activationID])
    }

    private func request(_ operation: String, fields: [String: Any]) async throws -> Data {
        var fields = fields
        fields["organization_id"] = PolarConfiguration.organizationID
        var request = URLRequest(url: URL(string: "https://api.polar.sh/v1/customer-portal/license-keys/\(operation)")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: fields)
        let (data, response): (Data, URLResponse)
        do { (data, response) = try await session.data(for: request) }
        catch { throw LicenseError.unavailable }
        guard let http = response as? HTTPURLResponse else { throw LicenseError.invalidResponse }
        switch http.statusCode {
        case 200..<300: return data
        case 404 where operation == "deactivate": return Data() // Already removed in Polar.
        case 404: throw LicenseError.rejected
        case 403 where operation == "activate": throw LicenseError.activationLimit
        case 400, 401, 403, 422: throw LicenseError.rejected
        case 429, 500...599: throw LicenseError.unavailable
        default: throw LicenseError.invalidResponse
        }
    }

    static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        decoder.dateDecodingStrategy = .custom { decoder in
            let value = try decoder.singleValueContainer().decode(String.self)
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            if let date = formatter.date(from: value) { return date }
            formatter.formatOptions = [.withInternetDateTime]
            guard let date = formatter.date(from: value) else { throw LicenseError.invalidResponse }
            return date
        }
        return decoder
    }
}
#endif
