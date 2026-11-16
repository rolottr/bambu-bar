import Foundation

/// Bambu Lab's texts for HMS codes and print errors in the app's language, in each printer model's own wording;
/// English for languages Bambu's HMS service doesn't offer. Generated from Bambu's catalogues by
/// shared/scripts/make-hms.py before a release; nothing is downloaded at run time. A code Bambu lists with no text
/// maps to "": its apps don't show those, so `ReportParser` skips them.
final class HMSCatalog {
    static let shared = HMSCatalog()

    private struct Table {
        var hms: [String: String] = [:]
        var errors: [String: String] = [:]
    }
    /// The text most models share, and per serial prefix the texts where a model differs.
    private struct Tables {
        var common = Table()
        var models: [String: Table] = [:]
    }
    private let bundle: Bundle
    private lazy var language = Self.language(in: bundle)
    private lazy var localized: Tables = Self.load(language, from: bundle)
    private lazy var english: Tables = language == "en" ? localized : Self.load("en", from: bundle)

    /// `bundle` holds the hms_<lang>.json.deflate files; the tests pass theirs.
    init(bundle: Bundle = .main) { self.bundle = bundle }

    /// `model` is the serial's three-character prefix; nil gives the text most models share.
    func hmsText(_ key: String, model: String? = nil) -> String? { text(key.uppercased(), model, \.hms) }

    func errorText(_ key: String, model: String? = nil) -> String? { text(key.uppercased(), model, \.errors) }

    /// The text for an issue's code as `PrinterIssue.id` shows it: an HMS code (`HMS_…`) or a print error.
    func issueText(_ id: String, model: String? = nil) -> String? {
        let hex = id.uppercased().replacingOccurrences(of: "HMS_", with: "").replacingOccurrences(of: "_", with: "")
        return id.uppercased().hasPrefix("HMS_") ? hmsText(hex, model: model) : errorText(hex, model: model)
    }

    private func text(_ key: String, _ model: String?, _ table: KeyPath<Table, [String: String]>) -> String? {
        let model = model?.uppercased()
        for tables in [localized, english] {
            if let model, let text = tables.models[model]?[keyPath: table][key] { return text }
            if let text = tables.common[keyPath: table][key] { return text }
        }
        return nil
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

    /// The app's language if there's a catalogue for it, else English.
    private static func language(in bundle: Bundle) -> String {
        let lang = AppLanguage.current
        return bundle.url(forResource: "hms_\(lang)", withExtension: "json.deflate") == nil ? "en" : lang
    }

    private static func load(_ language: String, from bundle: Bundle) -> Tables {
        guard let url = bundle.url(forResource: "hms_\(language)", withExtension: "json.deflate"),
              let data = try? (Data(contentsOf: url) as NSData).decompressed(using: .zlib) as Data,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return Tables() }
        func table(_ object: Any?) -> Table {
            let object = object as? [String: Any]
            return Table(hms: object?["hms"] as? [String: String] ?? [:], errors: object?["error"] as? [String: String] ?? [:])
        }
        let models = (json["models"] as? [String: Any] ?? [:]).mapValues(table)
        return Tables(common: table(json), models: models)
    }
}
