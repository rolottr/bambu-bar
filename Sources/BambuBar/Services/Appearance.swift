import AppKit
import SwiftUI

/// How the app looks: light or dark, Liquid Glass or the frosted modules of earlier versions, and the accent colour
/// (Settings → Appearance). Views read it through `Appearance.shared`, and Observation redraws what used a value when
/// it changes. The keys match the Windows app's settings.
@MainActor @Observable
final class Appearance {
    static let shared = Appearance()

    enum Mode: String, CaseIterable, Identifiable {
        case system, light, dark
        var id: String { rawValue }

        var title: LocalizedStringKey {
            switch self {
            case .system: return "Match System"
            case .light: return "Always Light"
            case .dark: return "Always Dark"
            }
        }
    }

    enum Style: String, CaseIterable, Identifiable {
        /// macOS Tahoe's Liquid Glass on the panel's modules, the window's cards and the round controls.
        case glass
        /// The frosted modules and hairlines of 2.2 and earlier.
        case classic
        var id: String { rawValue }

        var title: LocalizedStringKey {
            switch self {
            case .glass: return "Liquid Glass"
            case .classic: return "Classic"
            }
        }
    }

    /// The colour of buttons, switches, links and selections. Printer states keep their own colours
    /// (`Palette.color(for:)`): a printing printer is Bambu green whatever the accent.
    enum Accent: String, CaseIterable, Identifiable {
        case bambu, system, blue, purple, pink, orange, graphite
        var id: String { rawValue }

        var title: LocalizedStringKey {
            switch self {
            case .bambu: return "Bambu Green"
            case .system: return "System Accent"
            case .blue: return "Blue"
            case .purple: return "Purple"
            case .pink: return "Pink"
            case .orange: return "Orange"
            case .graphite: return "Graphite"
            }
        }

        /// Bright: text, dots and rings, made to read on dark glass. The same values as the Windows app.
        var color: Color {
            switch self {
            case .bambu: return Palette.accent
            case .system: return Color(nsColor: .controlAccentColor)
            case .blue: return Color(hex: 0x0A84FF)
            case .purple: return Color(hex: 0xBF5AF2)
            case .pink: return Color(hex: 0xFF375F)
            case .orange: return Color(hex: 0xFF9F0A)
            case .graphite: return Color(hex: 0x98989D)
            }
        }

        /// Deep: fills under white text (prominent buttons) and switches.
        var deep: Color {
            switch self {
            case .bambu: return Palette.accentDeep
            case .system: return Color(nsColor: NSColor.controlAccentColor.blended(withFraction: 0.15, of: .black) ?? .controlAccentColor)
            case .blue: return Color(hex: 0x0062CC)
            case .purple: return Color(hex: 0x8944AB)
            case .pink: return Color(hex: 0xD12B4C)
            case .orange: return Color(hex: 0xC76E00)
            case .graphite: return Color(hex: 0x5E5E63)
            }
        }
    }

    private enum Key {
        static let mode = "appearanceMode"
        static let style = "appearanceStyle"
        static let accent = "accentColor"
    }

    private let defaults = UserDefaults.standard

    var mode: Mode {
        didSet {
            guard mode != oldValue else { return }
            defaults.set(mode.rawValue, forKey: Key.mode)
            applyMode()
        }
    }

    var style: Style {
        didSet {
            guard style != oldValue else { return }
            defaults.set(style.rawValue, forKey: Key.style)
        }
    }

    var accent: Accent {
        didSet {
            guard accent != oldValue else { return }
            defaults.set(accent.rawValue, forKey: Key.accent)
        }
    }

    /// Bumped when the accent colour in System Settings changes, so views using `.system` redraw.
    private var systemColorsRevision = 0

    /// README and App Store frames draw views into bitmaps, where glass doesn't render; they keep the classic look.
    private let rendersToBitmaps = ReadmeFrames.directory != nil || StoreMedia.directory != nil

    private init() {
        _mode = Mode(rawValue: defaults.string(forKey: Key.mode) ?? "") ?? .system
        _style = Style(rawValue: defaults.string(forKey: Key.style) ?? "") ?? .glass
        _accent = Accent(rawValue: defaults.string(forKey: Key.accent) ?? "") ?? .bambu
        NotificationCenter.default.addObserver(forName: NSColor.systemColorsDidChangeNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { Appearance.shared.systemColorsRevision += 1 }
        }
    }

    /// Liquid Glass is on (the style, unless this run renders bitmaps).
    var usesGlass: Bool { style == .glass && !rendersToBitmaps }

    var accentColor: Color {
        _ = systemColorsRevision
        return accent.color
    }

    var accentDeepColor: Color {
        _ = systemColorsRevision
        return accent.deep
    }

    /// Light or dark for every window, menu and alert of the app; `.system` follows the Mac.
    func applyMode() {
        switch mode {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }
}

extension Palette {
    /// The accent for interactive things: links, selections, highlighted cards (Settings → Appearance).
    @MainActor static var tint: Color { Appearance.shared.accentColor }
    /// The deep accent: prominent buttons and switches.
    @MainActor static var tintDeep: Color { Appearance.shared.accentDeepColor }
}

private extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
}
