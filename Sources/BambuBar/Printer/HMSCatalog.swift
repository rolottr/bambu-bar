import Foundation

/// Bambu Lab's texts for HMS codes and print errors in the app's language, P2S wording where it differs; English
/// for languages Bambu's HMS service doesn't offer and for codes a translation lacks.
/// Generated from ha-bambulab's catalogues (MIT) by scripts/make-hms.py.
final class HMSCatalog {
    static let shared = HMSCatalog()

    private typealias Tables = (hms: [String: String], errors: [String: String])
    private lazy var localized: Tables = Self.load(Self.language)
    private lazy var english: Tables = Self.language == "en" ? localized : Self.load("en")

    func hmsText(_ key: String) -> String? {
        let key = key.uppercased()
        return localized.hms[key] ?? english.hms[key]
    }

    func errorText(_ key: String) -> String? {
        let key = key.uppercased()
        return localized.errors[key] ?? english.errors[key]
    }

    /// Bambu Lab's wiki page for a code on this printer model, through the redirect Bambu Studio uses: it picks the
    /// model's page (the wiki's paths don't follow the code, and slot-specific codes share one page) and falls back
    /// to the HMS index when there isn't one. Only the serial's three-character model prefix goes along, never the
    /// serial itself. `code` is the HMS code's 16 hex digits; print errors (8) have no pages of their own.
    static func wikiURL(code: String, serial: String?) -> URL? {
        let chinese = AppLanguage.current.hasPrefix("zh")
        let home = URL(string: "https://wiki.bambulab.com/\(chinese ? "zh" : "en")/hms/home")
        guard code.count == 16, var url = URLComponents(string: "https://e.bambulab.com/index.php") else { return home }
        url.queryItems = [URLQueryItem(name: "e", value: code.uppercased())]
            + (serial.map { $0.count >= 3 ? [URLQueryItem(name: "d", value: String($0.prefix(3)))] : [] } ?? [])
            + [URLQueryItem(name: "s", value: "device_hms"), URLQueryItem(name: "lang", value: chinese ? "zh-cn" : "en")]
        return url.url ?? home
    }

    /// The app's language if there's a catalogue for it. Traditional Chinese readers get the Simplified one,
    /// which Bambu Lab does publish, rather than English.
    private static var language: String {
        let lang = AppLanguage.current == "zh-Hant" ? "zh-Hans" : AppLanguage.current
        return Bundle.main.url(forResource: "hms_\(lang)", withExtension: "json.deflate") == nil ? "en" : lang
    }

    private static func load(_ language: String) -> Tables {
        guard let url = Bundle.main.url(forResource: "hms_\(language)", withExtension: "json.deflate"),
              let data = try? (Data(contentsOf: url) as NSData).decompressed(using: .zlib) as Data,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: [String: String]] else { return ([:], [:]) }
        return (json["hms"] ?? [:], json["error"] ?? [:])
    }
}
