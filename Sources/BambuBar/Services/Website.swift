import Foundation

/// Links to bambubar.app, tagged so the site's analytics can tell visits from the app apart: an app opening the
/// browser sends no Referer, so without the tags they count as direct traffic. The medium names the channel (`app`
/// for the GitHub build, `app_store` for the Mac App Store), the campaign the feature and the content the button.
enum Website {
    static let host = "https://bambubar.app"

    #if APP_STORE
    static let medium = "app_store"
    #else
    static let medium = "app"
    #endif

    static func url(_ path: String = "", campaign: String, content: String) -> URL {
        var url = URLComponents(string: "\(host)/\(path)")!
        url.queryItems = [
            URLQueryItem(name: "utm_source", value: "bambu_bar"),
            URLQueryItem(name: "utm_medium", value: medium),
            URLQueryItem(name: "utm_campaign", value: campaign),
            URLQueryItem(name: "utm_content", value: content),
        ]
        return url.url!
    }

    /// The About page's links.
    static let home = url(campaign: "about", content: "website")
    static let changelog = url("changelog", campaign: "about", content: "release_notes")
}
