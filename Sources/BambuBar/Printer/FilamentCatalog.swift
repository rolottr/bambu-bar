import Foundation

/// Bambu Studio's filament presets and Bambu Lab colour names, so a slot reads "SUNLU PLA Matte" or "Grass Green"
/// rather than a preset id or a hex code. Generated from Bambu Studio's profiles by shared/scripts/make-filaments.py.
enum FilamentCatalog {
    private struct Tables: Decodable {
        var presets: [String: [String]] = [:]
        var colors: [String: String] = [:]
    }

    private static let tables: Tables = {
        guard let url = Bundle.main.url(forResource: "filaments", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let tables = try? JSONDecoder().decode(Tables.self, from: data) else { return Tables() }
        return tables
    }()

    /// The maker and product of a preset id (`tray_info_idx`): ("SUNLU", "PLA Matte"). Nil for user presets.
    static func preset(_ id: String) -> (vendor: String, product: String)? {
        guard let entry = tables.presets[id.uppercased()], entry.count == 2 else { return nil }
        return (entry[0], entry[1])
    }

    /// Bambu Lab's name for a spool's colour: from the tag's colour code ("A01-G1" → "Grass Green"), else from
    /// the preset and the exact colour a Bambu colour has (picked from the Bambu list in Bambu Studio).
    static func colorName(tagCode: String, preset: String?, hex: String) -> String? {
        let parts = tagCode.uppercased().split(separator: "-")
        if parts.count == 2 {
            let letters = parts[1].prefix { $0.isLetter }
            let number = Int(parts[1].dropFirst(letters.count)).map(String.init) ?? ""
            if let name = tables.colors["GF\(parts[0])-\(letters)\(number)"] { return name }
        }
        guard let preset, hex.count >= 6 else { return nil }
        return tables.colors["\(preset.uppercased())-\(hex.prefix(6).uppercased())"]
    }
}
