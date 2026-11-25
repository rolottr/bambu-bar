import Foundation

/// Links to bambubar.app, tagged so the site's analytics can tell visits from the app apart: an app opening the
/// browser sends no Referer, so without the tags they count as direct traffic. The medium names the channel (`app`
/// for direct builds, `app_store` for the Mac App Store), the campaign the feature and the content the button.
enum Website {
    static let domain = "bambubar.app"
    static let host = "https://bambubar.app"
    static let source = "bambu_bar"

    #if APP_STORE
    static let medium = "app_store"
    #else
    static let medium = "app"
    #endif

    /// Validates a product website host and rejects `clevermechanisms.dev` and its subdomains so
    /// app links always point at the product's own domain.
    static func validatedHost(_ value: String?) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(),
              !value.isEmpty,
              value.range(
                  of: #"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$"#,
                  options: .regularExpression
              ) != nil,
              value != "clevermechanisms.dev",
              !value.hasSuffix(".clevermechanisms.dev")
        else { return nil }
        return value
    }

    static func url(_ path: String = "", host: String?, campaign: String, content: String) -> URL? {
        guard let validHost = validatedHost(host) else { return nil }
        var components = URLComponents()
        components.scheme = "https"
        components.host = validHost
        let trimmed = path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        components.path = trimmed.isEmpty ? "/" : "/\(trimmed)"
        components.queryItems = [
            URLQueryItem(name: "utm_source", value: source),
            URLQueryItem(name: "utm_medium", value: medium),
            URLQueryItem(name: "utm_campaign", value: campaign),
            URLQueryItem(name: "utm_content", value: content),
        ]
        return components.url
    }

    static func url(_ path: String = "", campaign: String, content: String) -> URL {
        url(path, host: domain, campaign: campaign, content: content)!
    }

    /// The About page's links.
    static let home = url(campaign: "about", content: "website")
    static let changelog = url("changelog", campaign: "about", content: "release_notes")
    static let support = url("support", campaign: "about", content: "support")
    static let download = url("download", campaign: "update", content: "manual_download")
}
