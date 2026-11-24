#if !APP_STORE
import Foundation

/// Public customer endpoints only. An organization access token must never ship in the app.
enum PolarConfiguration {
    static let organizationID = "2925c2bc-549b-4b87-96ca-83d2268817a6"
    static let benefitID = "3560f84c-991a-4d50-9b5d-1009584204dd"
    static let productID = "0fb62657-f715-42dc-90e4-c8e427a99271"
    static let checkoutURL = URL(string: "https://buy.polar.sh/polar_cl_vhx7SBcybHor1sLFyf4INLiI3ixoCza9zob3w42Z28x")!
    static let portalURL = URL(string: "https://polar.sh/nina-labs-ai-llc/portal")!
    static let apiBase = "https://api.polar.sh/v1/customer-portal/license-keys/"

    static func validBenefit(_ id: String) -> Bool {
        UUID(uuidString: id) != nil && id.lowercased() != productID.lowercased()
    }

    static var isConfigured: Bool { validBenefit(benefitID) }

    enum SupportPlacement: String {
        case mainWindow = "main_window"
        case appMenu = "app_menu"
        case menuPanel = "menu_panel"
        case license = "license"
    }

    static func supportURL(from placement: SupportPlacement) -> URL {
        if placement == .license { return checkoutURL }
        return Website.url("download", campaign: "support_project", content: placement.rawValue)
    }
}

enum LicenseError: LocalizedError, Equatable {
    case notConfigured, invalidKey, rejected, activationLimit, unavailable, invalidResponse, storage(OSStatus)

    /// Polar's explicit verdict on the key or activation. Only these lock a running app;
    /// everything else (outages, captive portals, malformed responses, Keychain write errors)
    /// falls back to bounded offline grace.
    var isRefusal: Bool {
        switch self {
        case .invalidKey, .rejected, .activationLimit: true
        case .notConfigured, .unavailable, .invalidResponse, .storage: false
        }
    }

    var errorDescription: String? {
        switch self {
        case .notConfigured:
            return String(localized: "License activation is not configured in this copy of Bambu Bar.")
        case .invalidKey:
            return String(localized: "This license key isn’t valid.")
        case .rejected:
            return String(localized: "This license or activation is no longer valid. Check your license in Manage Purchase, then deactivate this installation before activating again.")
        case .activationLimit:
            return String(localized: "This license is active on 2 Macs. You can deactivate one in Manage Purchase, then try again.")
        case .unavailable:
            return String(localized: "Cannot reach the licensing service right now. Check your internet connection and try again.")
        case .invalidResponse:
            return String(localized: "The licensing service returned an unexpected response. Please try again later.")
        case .storage:
            return String(localized: "The license could not be saved in your Mac’s Keychain. Check Keychain access and try again.")
        }
    }
}

struct PolarLicense: Decodable, Sendable {
    let organizationId: String
    let benefitId: String
    let status: String
    let expiresAt: Date?
    let activation: Activation?
    struct Activation: Decodable, Sendable { let id: String }

    func verify(activationID: String? = nil, now: Date = Date(),
                benefitID: String = PolarConfiguration.benefitID) throws {
        guard organizationId == PolarConfiguration.organizationID,
              PolarConfiguration.validBenefit(benefitID),
              benefitId == benefitID, status == "granted",
              expiresAt.map({ $0 > now }) ?? true,
              activationID == nil || activation?.id == activationID else { throw LicenseError.rejected }
    }
}

struct PolarActivation: Decodable, Sendable {
    let id: String
    let licenseKey: PolarLicense
}

protocol LicenseAPI: Sendable {
    func activate(key: String, installationID: String) async throws -> PolarActivation
    func validate(_ record: LicenseRecord) async throws -> PolarLicense
    func deactivate(_ record: LicenseRecord) async throws
}

struct PolarClient: LicenseAPI {
    static let maxResponseBytes = 128 * 1024

    let session: URLSession
    let benefitID: String

    init(session: URLSession? = nil, benefitID: String = PolarConfiguration.benefitID) {
        self.session = session ?? Self.makeSession()
        self.benefitID = benefitID
    }

    static func makeSession(protocolClasses: [AnyClass]? = nil) -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 25
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.httpShouldSetCookies = false
        if let protocolClasses {
            configuration.protocolClasses = protocolClasses
        }
        return URLSession(configuration: configuration, delegate: LicenseRedirectPolicy(), delegateQueue: nil)
    }

    func activate(key: String, installationID: String) async throws -> PolarActivation {
        let data = try await request("activate", fields: [
            "key": key, "label": "Mac · \(installationID.prefix(8))",
            "conditions": ["installation_id": installationID],
        ])
        return try Self.decode(PolarActivation.self, from: data)
    }

    func validate(_ record: LicenseRecord) async throws -> PolarLicense {
        let data = try await request("validate", fields: [
            "key": record.key, "activation_id": record.activationID,
            "benefit_id": benefitID,
            "conditions": ["installation_id": record.installationID],
        ])
        return try Self.decode(PolarLicense.self, from: data)
    }

    func deactivate(_ record: LicenseRecord) async throws {
        _ = try await request("deactivate", fields: ["key": record.key, "activation_id": record.activationID])
    }

    private func request(_ operation: String, fields: [String: Any]) async throws -> Data {
        guard PolarConfiguration.validBenefit(benefitID) else { throw LicenseError.notConfigured }
        var fields = fields
        fields["organization_id"] = PolarConfiguration.organizationID
        var request = URLRequest(url: URL(string: PolarConfiguration.apiBase + operation)!)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: fields)

        let bytes: URLSession.AsyncBytes
        let response: URLResponse
        do {
            (bytes, response) = try await session.bytes(for: request)
        } catch {
            throw LicenseError.unavailable
        }
        defer { bytes.task.cancel() }

        // A captive portal or proxy that redirected us off api.polar.sh is never Polar answering.
        guard let http = response as? HTTPURLResponse,
              http.url == request.url,
              http.url?.host?.lowercased() == "api.polar.sh"
        else {
            throw LicenseError.unavailable
        }

        var data = Data()
        do {
            for try await byte in bytes {
                guard data.count < Self.maxResponseBytes else { throw LicenseError.invalidResponse }
                data.append(byte)
            }
        } catch let error as LicenseError {
            throw error
        } catch {
            throw LicenseError.unavailable
        }

        return try Self.interpret(status: http.statusCode, operation: operation, body: data)
    }

    /// Maps Polar's HTTP status to a result; pure so it is unit-tested. Polar answers refusals
    /// with a JSON error object, so a 4xx without a JSON object (captive portal or proxy HTML)
    /// and any redirect/rate-limit/5xx count as `.unavailable`, never as a refusal.
    static func interpret(status: Int, operation: String, body: Data) throws -> Data {
        switch status {
        case 200..<300: return body
        case 400..<500 where status != 429 && !isJSONObject(body): throw LicenseError.unavailable
        case 404 where operation == "deactivate": return Data() // Already removed in Polar.
        case 403 where operation == "activate": throw LicenseError.activationLimit
        case 404 where operation == "activate": throw LicenseError.invalidKey
        case 422 where operation == "activate": throw LicenseError.invalidKey
        case 400, 401, 403, 404, 422: throw LicenseError.rejected
        default: throw LicenseError.unavailable // 3xx redirects, 429, 5xx, proxy statuses…
        }
    }

    /// Decodes a 2xx body. Non-JSON 200 (e.g. captive-portal HTML) is `.unavailable`;
    /// a JSON object with missing/wrong fields is `.invalidResponse`. Neither is a refusal.
    static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        guard isJSONObject(data) else { throw LicenseError.unavailable }
        do { return try decoder().decode(type, from: data) }
        catch { throw LicenseError.invalidResponse }
    }

    static func isJSONObject(_ data: Data) -> Bool {
        (try? JSONSerialization.jsonObject(with: data)) is [String: Any]
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

/// License POST bodies must never be replayed at a redirect destination.
private final class LicenseRedirectPolicy: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
#endif
