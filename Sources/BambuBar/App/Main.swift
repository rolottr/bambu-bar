import AppKit

/// Explicit entry point so language and locale alignment run strictly before
/// `NSApplication.shared` or any Foundation localization cache is initialized.
@main
enum Main {
    @MainActor
    static func main() {
        AppLanguage.configureForLaunch()
        BambuBarApp.main()
    }
}
