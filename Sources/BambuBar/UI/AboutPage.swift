import AppKit
import Darwin
import SwiftUI

/// About the app: header, copy details for bug reports, UTM-tagged product links, and license management.
struct AboutPage: View {
    @State private var copied = false
    private var updates: UpdateChecker { .shared }

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            HStack(spacing: 20) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 96, height: 96)
                    .shadow(color: .black.opacity(0.25), radius: 10, y: 5)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Bambu Bar").font(.system(size: 30, weight: .bold))
                    Text("© 2026 Clever Mechanisms").font(.subheadline).foregroundStyle(.secondary)
                    Text("Version \(AppInfo.version) (\(AppInfo.build))").foregroundStyle(.secondary)
                    Text("Your Bambu Lab printer, one click away in the menu bar.")
                        .font(.system(size: 13)).foregroundStyle(.tertiary)
                    if let release = updates.available {
                        Button { updates.showUpdate() } label: {
                            Label("Get Bambu Bar \(release.version)", systemImage: "arrow.down.circle.fill")
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(Palette.tintDeep)
                        .help("Shows what’s new in \(release.version) and installs it")
                        .padding(.top, 6)
                    }
                }
            }
            .padding(.bottom, 4)

            HStack(alignment: .top, spacing: 20) {
                section("This copy") {
                    DetailRow(title: "Version", value: "\(AppInfo.version) (\(AppInfo.build))")
                    DetailRow(title: "System", value: "\(AppInfo.system) · \(AppInfo.architecture)")
                    DetailRow(title: "Location", value: AppInfo.location)
                    DetailRow(title: "License", value: AppInfo.licenseStatus)
                    HStack {
                        Text("For bug reports").font(.system(size: 12)).foregroundStyle(.secondary)
                        Spacer()
                        Button(copied ? "Copied" : "Copy Version Info", action: copy)
                            .controlSize(.small)
                    }
                }

                section("Links") {
                    link("bambubar.app", symbol: "globe", Website.home)
                    if UpdateChecker.isAvailable {
                        link("Release Notes", symbol: "doc.text", Website.changelog)
                    }
                    #if !APP_STORE
                    link("Manage Purchase", symbol: "person.crop.circle", PolarConfiguration.portalURL)
                    #endif
                    link("Support", symbol: "questionmark.bubble", Website.support)
                }
            }

            #if !APP_STORE
            section("Your license") { LicenseView() }
            #endif
        }
    }

    private func section<Content: View>(_ title: LocalizedStringKey, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionLabel(title)
            VStack(alignment: .leading, spacing: 10) { content() }
                .panel(radius: 16, padding: 16)
        }
    }

    private func link(_ title: LocalizedStringKey, symbol: String, _ url: URL) -> some View {
        Button { NSWorkspace.shared.open(url) } label: {
            HStack(spacing: 8) {
                Image(systemName: symbol).frame(width: 18)
                Text(title)
            }
            .font(.system(size: 12.5))
        }
        .buttonStyle(.borderless)
        .foregroundStyle(Palette.tint)
        .help(url.absoluteString)
    }

    private func copy() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(AppInfo.summary, forType: .string)
        copied = true
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(2))
            copied = false
        }
    }
}

@MainActor
enum AppInfo {
    private static let info = Bundle.main.infoDictionary ?? [:]
    nonisolated static let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    nonisolated static let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "dev"
    nonisolated static let system: String = {
        let os = ProcessInfo.processInfo.operatingSystemVersion
        return "macOS \(os.majorVersion).\(os.minorVersion)" + (os.patchVersion > 0 ? ".\(os.patchVersion)" : "")
    }()
    nonisolated static let architecture: String = {
        #if arch(arm64)
            "Apple Silicon"
        #else
            "Intel"
        #endif
    }()

    /// The folder this copy runs from, abbreviating the real home directory to `~/…` and
    /// flagging App Translocation copies.
    nonisolated static let location = location(of: Bundle.main.bundleURL, home: realHome)

    static var licenseStatus: String {
        #if APP_STORE
        String(localized: "Mac App Store")
        #else
        LicenseStore.shared.hasAccess ? String(localized: "Activated") : String(localized: "Not activated")
        #endif
    }

    /// One line to paste into a bug report (never includes the license key).
    static var summary: String {
        "Bambu Bar \(version) (\(build)) · \(system) · \(architecture) · \(location) · \(licenseStatus)"
    }

    nonisolated static func location(of bundle: URL, home: String) -> String {
        let path = bundle.standardizedFileURL.path
        if path.contains("/AppTranslocation/") {
            return String(localized: "Temporary copy (App Translocation)")
        }
        let folder = bundle.standardizedFileURL.deletingLastPathComponent().path
        if !home.isEmpty, folder == home || folder.hasPrefix(home + "/") {
            return "~" + folder.dropFirst(home.count)
        }
        return folder
    }

    /// The user's real home directory (`NSHomeDirectory()` returns the sandbox container in App Store builds).
    nonisolated private static var realHome: String {
        guard let entry = getpwuid(getuid()), let dir = entry.pointee.pw_dir else { return "" }
        return String(cString: dir)
    }
}
