#if !APP_STORE
import Foundation
#if canImport(BambuBarLicensing)
import BambuBarLicensing
#endif

/// The license headers sent ONLY to `https://bambubar.app/api/updates/download` (or, in Debug builds
/// only, the same path on a validated `UpdateFeedURL` override) so the website can verify the
/// supporter license with Polar before redirecting to the signed archive. Never attached to feed
/// checks (`SPUUpdater.httpHeaders` stays unused) and never sent to any override origin in Release.
public enum UpdateDownload {
    public static let licenseKeyHeader = "X-BambuBar-License-Key"
    public static let activationHeader = "X-BambuBar-Activation-Id"
    public static let installationHeader = "X-BambuBar-Installation-Id"
    public static let endpoint = URL(string: "https://bambubar.app/api/updates/download")!

    #if DEBUG
    public static let defaultAllowsAnyFeedOverrideHost = true
    #else
    public static let defaultAllowsAnyFeedOverrideHost = false
    #endif

    /// True only for strict loopback hosts (`127.0.0.1`, `::1`, or `localhost`).
    public static func isLoopbackHost(_ host: String) -> Bool {
        let lowered = host.lowercased()
        return lowered == "localhost" || lowered == "127.0.0.1" || lowered == "::1" || lowered == "[::1]"
    }

    /// Validates an `UpdateFeedURL` UserDefaults override for anonymous appcast feed discovery.
    /// In Release policy (`allowAnyHost == false`), only strict loopback hosts are permitted for
    /// anonymous feed checks; `accepts(_:feed:allowAnyFeedOverrideHost:)` independently refuses
    /// every override origin (including loopback) as a license-header destination in Release.
    public static func validatedFeedOverride(_ raw: String?, allowAnyHost: Bool = defaultAllowsAnyFeedOverrideHost) -> URL? {
        guard let raw = raw?.trimmingCharacters(in: .whitespacesAndNewlines),
              !raw.isEmpty,
              let url = URL(string: raw),
              let scheme = url.scheme?.lowercased(),
              scheme == "https" || scheme == "http",
              url.user == nil,
              url.password == nil,
              let host = url.host?.lowercased(), !host.isEmpty else { return nil }
        if !allowAnyHost {
            guard isLoopbackHost(host) else { return nil }
        }
        return url
    }

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

    /// True only for `https://bambubar.app/api/updates/download` — and, ONLY when
    /// `allowAnyFeedOverrideHost` is `true` (`#if DEBUG`), `/api/updates/download` on the exact
    /// scheme/host/port of a validated `UpdateFeedURL` override — with no URL userinfo and no
    /// trailing slash or alternate path encoding.
    ///
    /// In Release (`allowAnyFeedOverrideHost == false`), NO override origin (not even loopback
    /// `127.0.0.1` / `localhost` / `::1`) is ever accepted as a header destination, preventing any
    /// same-user local process from intercepting `X-BambuBar-*` license headers over loopback HTTP.
    public static func accepts(
        _ url: URL,
        feed override: URL? = nil,
        allowAnyFeedOverrideHost: Bool = defaultAllowsAnyFeedOverrideHost
    ) -> Bool {
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
        if target == origin(endpoint) { return true }
        guard allowAnyFeedOverrideHost,
              let validOverride = validatedFeedOverride(override?.absoluteString, allowAnyHost: true) else {
            return false
        }
        return origin(validOverride) == target
    }
}
#endif
