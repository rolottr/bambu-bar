#if !APP_STORE
import Foundation

/// Public release metadata is used for version discovery; only Sparkle installs signed archives.
struct GitHubRelease: Decodable {
    struct Asset: Decodable { let name: String }
    let tagName: String
    let draft: Bool
    let prerelease: Bool
    let assets: [Asset]

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name", draft, prerelease, assets
    }

    var hasUpdateFeed: Bool { assets.contains { $0.name == "appcast.xml" } }

    func newerVersion(than current: String) throws -> String? {
        guard !draft, !prerelease else { return nil }
        let version = tagName.hasPrefix("v") ? String(tagName.dropFirst()) : tagName
        let latestParts = try Self.components(version)
        let currentParts = try Self.components(current)
        return currentParts.lexicographicallyPrecedes(latestParts) ? version : nil
    }

    private static func components(_ version: String) throws -> [Int] {
        let parts = version.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy({ $0.isASCII && $0.isNumber }) }) else {
            throw GitHubReleaseError.invalidVersion
        }
        let values = parts.compactMap { Int($0) }
        guard values.count == 3 else { throw GitHubReleaseError.invalidVersion }
        return values
    }
}

enum GitHubReleaseError: LocalizedError {
    case invalidVersion, unavailable, missingUpdateFeed
    var errorDescription: String? {
        switch self {
        case .invalidVersion: return String(localized: "GitHub returned an unrecognized release version.")
        case .unavailable: return String(localized: "Couldn’t reach GitHub. Check your connection and try again.")
        case .missingUpdateFeed: return String(localized: "A newer version is available, but its signed update feed is not ready yet. Please try again later.")
        }
    }
}
#endif
