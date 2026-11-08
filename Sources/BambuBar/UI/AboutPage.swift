import AppKit
import SwiftUI

/// About the app: version details for bug reports, how it works, privacy and credits.
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
                    Text("NINA LABS, LLC").font(.subheadline).foregroundStyle(.secondary)
                    Text("Version \(AppInfo.version) (\(AppInfo.build))").foregroundStyle(.secondary)
                    Text("Your Bambu Lab printer, one click away in the menu bar.")
                        .font(.system(size: 13)).foregroundStyle(.tertiary)
                    if let release = updates.available {
                        Button { updates.showUpdate() } label: {
                            Label("Get Bambu Bar \(release.version)", systemImage: "arrow.down.circle.fill")
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(Palette.accentDeep)
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
                    #if APP_STORE
                    DetailRow(title: "License", value: String(localized: "Mac App Store"))
                    #else
                    if LicenseStore.shared.hasAccess {
                        Label("Hero Supporter · Thanks for supporting Bambu Bar", systemImage: "star.circle.fill")
                            .font(.callout.weight(.semibold)).foregroundStyle(Palette.accent)
                    } else {
                        DetailRow(title: "License", value: String(localized: "Free · Community edition"))
                    }
                    #endif
                    HStack {
                        Text("For bug reports").font(.system(size: 12)).foregroundStyle(.secondary)
                        Spacer()
                        Button(copied ? "Copied" : "Copy Version Info", action: copy)
                            .controlSize(.small)
                    }
                }

                section("Links") {
                    link("bambubar.app", symbol: "globe", Website.home)
                    // The App Store shows what's new itself, and the release page offers the app outside the store.
                    if UpdateChecker.isAvailable {
                        link("Release Notes", symbol: "doc.text", Website.changelog)
                    }
                    #if !APP_STORE
                    link("Manage Subscription", symbol: "person.crop.circle", PolarConfiguration.portalURL)
                    #endif
                    link("Report an Issue", symbol: "exclamationmark.bubble", AppInfo.project.appendingPathComponent("issues/new"))
                }
            }

            #if !APP_STORE
            section("Your license") { LicenseView() }
            #endif

            section("How it works") {
                paragraph("Bambu Bar connects straight to your printer on the local network, the same way Bambu Studio does in LAN mode: an encrypted MQTT connection on port 8883, signed in with the printer's access code. The printer's certificate is checked against Bambu Lab's own certificate authority.")
                paragraph("The printer streams its status about once a second. Bambu Bar shows it in the menu bar, keeps a short history of your prints, and shares a snapshot with its widget through a private container on this Mac.")
            }

            section("Privacy") {
                paragraph("Printer data stays on your local network. No app analytics. Printer access codes are stored in your keychain.")
                if UpdateChecker.isAvailable {
                    paragraph("Supporter licenses are validated online, with the key and installation ID stored in Keychain. In-app update checks use the latest GitHub release feed and require an active website supporter license. Update checks send no printer data, and updates must carry the developer's signature. Manage automatic updates in Settings → Updates.")
                } else {
                    paragraph("It makes no other network requests. Updates come from the App Store.")
                }
            }

            section("Credits") {
                paragraph("Protocol details and the HMS error texts come from the open-source ha-bambulab project (MIT), with thanks to Bambuddy and bambu-cli for documenting the newer printers. What each model can do (chamber sensor, fans, heated chamber, nozzles, build volume) follows the printer profiles Bambu Lab publishes with Bambu Studio.")
                paragraph("The printer models were built for this project in Blender. They are visual approximations, not manufacturer CAD.")
                paragraph("Bambu Lab, Bambu Studio and P2S are trademarks of Bambu Lab. Bambu Bar is an independent project, not affiliated with or endorsed by Bambu Lab.")
            }
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
        .foregroundStyle(Palette.accent)
        .help(url.absoluteString)
    }

    private func paragraph(_ text: LocalizedStringKey) -> some View {
        Text(text).font(.system(size: 12.5)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
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

enum AppInfo {
    private static let info = Bundle.main.infoDictionary ?? [:]
    static let version = info["CFBundleShortVersionString"] as? String ?? "dev"
    static let build = info["CFBundleVersion"] as? String ?? "dev"
    static let system: String = {
        let os = ProcessInfo.processInfo.operatingSystemVersion
        return "macOS \(os.majorVersion).\(os.minorVersion)" + (os.patchVersion > 0 ? ".\(os.patchVersion)" : "")
    }()
    static let architecture: String = {
        #if arch(arm64)
            "Apple Silicon"
        #else
            "Intel"
        #endif
    }()
    static let project = URL(string: "https://github.com/\(UpdateChecker.repository)")!
    static var summary: String { "Bambu Bar \(version) (\(build)) · \(system) · \(architecture)" }
}
