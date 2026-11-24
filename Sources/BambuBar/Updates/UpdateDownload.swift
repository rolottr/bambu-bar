#if !APP_STORE
import Foundation
#if canImport(BambuBarLicensing)
import BambuBarLicensing
#endif

/// The license headers sent ONLY to `bambubar.app/api/updates/download` (or the same path on a local
/// `-UpdateFeedURL` test host) so the website can verify the supporter license with Polar before
/// redirecting to the signed archive. Never attached to feed checks (`SPUUpdater.httpHeaders` stays unused).
public enum UpdateDownload {
    public static let licenseKeyHeader = "X-BambuBar-License-Key"
    public static let activationHeader = "X-BambuBar-Activation-Id"
    public static let installationHeader = "X-BambuBar-Installation-Id"
    public static let endpoint = URL(string: "https://bambubar.app/api/updates/download")!

    /// Returns nil when no complete activation record exists.
    public static func headers(for record: LicenseRecord?) -> [String: String]? {
        guard let record,
              !record.key.isEmpty,
              !record.activationID.isEmpty,
              !record.installationID.isEmpty else { return nil }
        return [
            licenseKeyHeader: record.key,
            activationHeader: record.activationID,
            installationHeader: record.installationID,
        ]
    }

    /// True only for `https://bambubar.app/api/updates/download` (or `/api/updates/download` on the
    /// exact scheme/host/port of a local test `UpdateFeedURL` override), with no URL userinfo and no
    /// trailing slash or alternate path encoding.
    public static func accepts(_ url: URL, feed override: URL? = nil) -> Bool {
        // `path(percentEncoded: true)` preserves trailing slashes (`URL.path` drops them), so
        // `…/api/updates/download/` is rejected.
        guard url.path(percentEncoded: true) == endpoint.path(percentEncoded: true),
              url.user == nil,
              url.password == nil else { return false }

        func origin(_ candidate: URL) -> String? {
            guard let scheme = candidate.scheme?.lowercased(),
                  scheme == "https" || scheme == "http",
                  let host = candidate.host?.lowercased(), !host.isEmpty else { return nil }
            let port = candidate.port ?? (scheme == "https" ? 443 : 80)
            return "\(scheme)://\(host):\(port)"
        }

        guard let target = origin(url) else { return false }
        return target == origin(endpoint) || override.flatMap(origin) == target
    }
}
#endif
