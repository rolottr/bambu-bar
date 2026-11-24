import AppKit

/// The app follows the system language (or the one picked for it in System Settings → General → Language & Region
/// → Applications). Settings can override it; like that system setting, the change applies on the next launch.
enum AppLanguage {
    private static let languagesKey = "AppleLanguages"
    nonisolated(unsafe) private static var configuredCurrent: String?

    /// The language the running app shows, as a localization code ("de", "pt-BR", "zh-Hans").
    static var current: String {
        configuredCurrent ?? Bundle.main.preferredLocalizations.first ?? "en"
    }

    /// The language the app shows without an override: the first of the Mac's languages it is translated into.
    static var system: String {
        let preferred = CFPreferencesCopyValue(languagesKey as CFString, kCFPreferencesAnyApplication,
                                               kCFPreferencesCurrentUser, kCFPreferencesAnyHost) as? [String] ?? []
        return Bundle.preferredLocalizations(from: available, forPreferences: preferred).first ?? "en"
    }

    /// Every language the app is translated into, by their own names in the order the system sorts them.
    static var available: [String] {
        Bundle.main.localizations.filter { $0 != "Base" }
            .sorted { name(of: $0).localizedStandardCompare(name(of: $1)) == .orderedAscending }
    }

    /// The override picked in Settings; nil follows the system.
    static var override: String? {
        // The app's own domain only: the global AppleLanguages is the system's list.
        get { (UserDefaults.standard.persistentDomain(forName: Bundle.main.bundleIdentifier ?? "")?[languagesKey] as? [String])?.first }
        set {
            if let newValue {
                UserDefaults.standard.set([newValue], forKey: languagesKey)
            } else {
                UserDefaults.standard.removeObject(forKey: languagesKey)
            }
        }
    }

    /// Must run before `NSApplication.shared` or any Foundation localization read so
    /// `Locale.current` and plural rules match the active interface language.
    @MainActor
    static func configureForLaunch() {
        configuredCurrent = Bundle.main.preferredLocalizations.first ?? "en"
        alignLocaleWithInterface()
    }

    /// Makes `Locale.current` speak the UI language while keeping the user's region.
    /// With a per-app language override, the locale otherwise inherits the Mac's system language,
    /// causing mismatched plural rules and formatting.
    static func alignLocaleWithInterface() {
        let interface = current
        let global = CFPreferencesCopyValue("AppleLocale" as CFString, kCFPreferencesAnyApplication,
                                            kCFPreferencesCurrentUser, kCFPreferencesAnyHost) as? String
            ?? Locale.current.identifier
        let aligned = alignedLocaleIdentifier(global: global, interface: interface)
        guard aligned != global else { return }
        var arguments = UserDefaults.standard.volatileDomain(forName: UserDefaults.argumentDomain)
        arguments["AppleLocale"] = aligned
        UserDefaults.standard.setVolatileDomain(arguments, forName: UserDefaults.argumentDomain)
    }

    /// Returns `global` with its language replaced by `interface`, preserving the user's effective region.
    static func alignedLocaleIdentifier(global: String, interface: String) -> String {
        var components = Locale.Components(identifier: global)
        let wanted = Locale.Language(identifier: interface)
        let wantedComponents = Locale.Language.Components(identifier: interface)
        if components.languageComponents.languageCode == wantedComponents.languageCode
            && (wantedComponents.script == nil || components.languageComponents.script == wantedComponents.script) {
            return global
        }
        // Ordinary identifiers (e.g. en_US, es_ES) carry their region inside the language,
        // whereas Components.region is only an explicit regional override. Preserve the effective region
        // on both so Locale(identifier:) reports the user's region cleanly.
        let effectiveRegion = Locale(identifier: global).region
        var languageComponents = Locale.Language.Components(language: wanted)
        if let effectiveRegion {
            languageComponents.region = effectiveRegion
        }
        components.languageComponents = languageComponents
        components.region = effectiveRegion
        return Locale(components: components).identifier
    }

    /// "Deutsch", "Português (Brasil)", "简体中文"…
    static func name(of code: String) -> String {
        let locale = Locale(identifier: code)
        let name = locale.localizedString(forIdentifier: code) ?? code
        return name.prefix(1).uppercased(with: locale) + name.dropFirst()
    }

    /// Starts a fresh copy of the app with a one-time hand-over ticket and quits this copy so a new language
    /// takes effect without racing `SingleInstance`.
    @MainActor
    static func relaunch(ticketStore: RelaunchTicketStore = .shared) {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.createsNewApplicationInstance = true
        let ticket = ticketStore.begin()
        if let ticket {
            configuration.arguments = [RelaunchTicketStore.argument, ticket.token.uuidString]
        }
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: configuration) { _, error in
            DispatchQueue.main.async {
                if error != nil {
                    if let ticket { ticketStore.cancel(ticket) }
                    return
                }
                NSApp.terminate(nil)
            }
        }
    }
}
