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

    /// The links to try for a `wikiURL`, best first (HMSWiki opens the first that isn't the index). The wiki files
    /// AMS codes once for every unit and slot (0700_2100 and 0701_2300 under 0700_2000), and Bambu's lookup doesn't
    /// always see that, nor a page filed under another model. So: the code for this model, then with the AMS unit
    /// and slot zeroed, then both for any model.
    static func wikiCandidates(for url: URL) -> [URL] {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              let code = components.queryItems?.first(where: { $0.name == "e" })?.value else { return [url] }
        let codes = [code, amsGeneric(code)]
        var out: [URL] = []
        for keepModel in [true, false] {
            for c in codes {
                var candidate = components
                candidate.queryItems = components.queryItems?.compactMap { item in
                    if item.name == "d" { return keepModel ? item : nil }
                    return item.name == "e" ? URLQueryItem(name: "e", value: c) : item
                }
                if let link = candidate.url, !out.contains(link) { out.append(link) }
            }
        }
        return out
    }

    /// An AMS code (modules 07, 12, 18) with the unit (digits 3–4) and the slot (digit 6) zeroed.
    static func amsGeneric(_ code: String) -> String {
        var c = Array(code.uppercased())
        guard c.count == 16, ["07", "12", "18"].contains(String(c[0..<2])) else { return code.uppercased() }
        c[2] = "0"; c[3] = "0"; c[5] = "0"
        return String(c)
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
