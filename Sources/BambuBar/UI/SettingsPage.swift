import SwiftUI

struct SettingsPage: View {
    @Environment(PrinterStore.self) private var store
    @Environment(LoginItem.self) private var loginItem

    @State private var language = AppLanguage.override

    var body: some View {
        @Bindable var store = store
        VStack(alignment: .leading, spacing: 22) {
            PageHeader(title: "Settings")

            SettingsGroup("General", symbol: "gearshape") {
                SettingsRow("Open at login") {
                    Toggle("", isOn: Binding(get: { loginItem.isEnabled }, set: { loginItem.setEnabled($0) }))
                        .toggleStyle(.switch).labelsHidden().tint(Palette.tintDeep)
                }
                if loginItem.status == .requiresApproval {
                    HStack {
                        Text("Allow Bambu Bar in System Settings → General → Login Items.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        Spacer()
                        Button("Open System Settings") { loginItem.openSystemSettings() }.controlSize(.small)
                    }
                }
                if let error = loginItem.error {
                    Text(error).font(.system(size: 11.5)).foregroundStyle(Palette.failed)
                }
                Text("At login Bambu Bar starts in the menu bar only, without a Dock icon. Open it from the menu bar or Spotlight to see this window.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            SettingsGroup("Language", symbol: "globe") {
                SettingsRow("Language") {
                    Picker("", selection: $language) {
                        Text("System (\(AppLanguage.name(of: AppLanguage.system)))").tag(String?.none)
                        Divider()
                        ForEach(AppLanguage.available, id: \.self) { Text(verbatim: AppLanguage.name(of: $0)).tag(Optional($0)) }
                    }
                    .labelsHidden()
                    .fixedSize()
                    .onChange(of: language) { AppLanguage.override = language }
                }
                if (language ?? AppLanguage.system) != AppLanguage.current {
                    HStack {
                        Text("Bambu Bar switches to \(AppLanguage.name(of: language ?? AppLanguage.system)) when it restarts.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        Spacer()
                        Button("Restart Now") { AppLanguage.relaunch() }.controlSize(.small)
                    }
                } else {
                    Text("Bambu Bar follows the language of your Mac unless you pick another one here.")
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            AppearanceGroup()

            SettingsGroup("Camera", symbol: "video.fill") {
                SettingsToggle("Show the printer camera", isOn: $store.cameraEnabled)
                Text("Turn the camera on for each printer too, at the bottom of its Printer page. While it's off, the camera is hidden everywhere: the Printer page, the menu bar panel and the menus.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if store.cameraEnabled {
                    SettingsToggle("Keep the camera connected during prints", isOn: $store.cameraStaysOnWhilePrinting)
                    Text("The picture shows at once when you open it, but the stream runs for the whole print and uses one of the printer's few camera connections, so Bambu Handy or Bambu Studio may not get the camera meanwhile.")
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            SettingsGroup("Controls", symbol: "slider.horizontal.3") {
                SettingsToggle("Ask before stopping a print", isOn: $store.confirmStop)
                Text("Pause, stop and speed changes need **LAN Only** mode with **Developer Mode** on the printer (Settings → Network on its screen). Reading status and the chamber light work in any mode.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if UpdateChecker.isAvailable {
                UpdatesGroup()
            }
        }
    }
}

/// Light or dark, Liquid Glass or classic, and the accent colour.
private struct AppearanceGroup: View {
    @State private var appearance = Appearance.shared

    var body: some View {
        SettingsGroup("Appearance", symbol: "paintpalette") {
            SettingsRow("Mode") {
                Picker("", selection: $appearance.mode) {
                    ForEach(Appearance.Mode.allCases) { Text($0.title).tag($0) }
                }
                .labelsHidden()
                .fixedSize()
            }
            SettingsRow("Style") {
                Picker("", selection: $appearance.style) {
                    ForEach(Appearance.Style.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .tint(Palette.tintDeep)
            }
            Text("Liquid Glass gives the menu bar panel, the window's cards and the round controls the glass of macOS Tahoe. Classic keeps the frosted look of earlier versions.")
                .font(.system(size: 11.5)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Divider()
            SettingsRow("Accent colour") {
                AccentPicker(selection: $appearance.accent)
            }
            Text("The accent colour marks buttons, switches, links and selections. Printer states keep their own colours.")
                .font(.system(size: 11.5)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// A row of colour dots like System Settings' accent colours, the choice's name before them. One line, so the row
/// lines up with the others in the group and its dots end where their controls do.
private struct AccentPicker: View {
    @Binding var selection: Appearance.Accent

    var body: some View {
        HStack(spacing: 10) {
            Text(selection.title).font(.system(size: 11.5)).foregroundStyle(.secondary)
            HStack(spacing: 4) {
                ForEach(Appearance.Accent.allCases) { accent in
                    Button { selection = accent } label: { dot(accent) }
                        .buttonStyle(.plain)
                        .help(Text(accent.title))
                        .accessibilityLabel(Text(accent.title))
                        .accessibilityAddTraits(selection == accent ? .isSelected : [])
                }
            }
        }
    }

    private func dot(_ accent: Appearance.Accent) -> some View {
        let selected = selection == accent
        return ZStack {
            if accent == .system {
                // The Mac's own accent, whatever it is, as System Settings draws "Multicolour".
                Circle().fill(AngularGradient(colors: [.red, .orange, .yellow, .green, .blue, .purple, .pink, .red], center: .center))
            } else {
                Circle().fill(accent.color.gradient)
            }
            Circle().strokeBorder(Color.primary.opacity(0.15), lineWidth: 0.5)
            if selected {
                Circle().fill(.white).frame(width: 6, height: 6).shadow(color: .black.opacity(0.3), radius: 1)
            }
        }
        .frame(width: 18, height: 18)
        .padding(2)
        .overlay(Circle().strokeBorder(selected ? accent.color.opacity(0.6) : .clear, lineWidth: 1.5))
        .contentShape(Circle())
    }
}

/// Automatic update checks and downloads, the result of the last check and a manual one.
private struct UpdatesGroup: View {
    @State private var updates = UpdateChecker.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Updates", systemImage: "arrow.down.circle.fill")
                .font(.system(size: 13, weight: .semibold))
            VStack(alignment: .leading, spacing: 12) {
                #if !APP_STORE
                if !updates.supportsAutomaticUpdates {
                    Text("Activate your website supporter license to check GitHub for updates and enable automatic updates.")
                        .font(.system(size: 12.5)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Button("Activate Supporter License…") { MainWindow.show(.about) }
                        .controlSize(.small)
                }
                #endif
                HStack {
                    Text("Check for updates automatically").font(.system(size: 12.5))
                    Spacer()
                    Toggle("", isOn: $updates.checksAutomatically).toggleStyle(.switch).labelsHidden().tint(Palette.tintDeep)
                }
                .disabled(!updates.supportsAutomaticUpdates)
                HStack {
                    Text("Download and install updates automatically").font(.system(size: 12.5))
                    Spacer()
                    Toggle("", isOn: $updates.downloadsAutomatically).toggleStyle(.switch).labelsHidden().tint(Palette.tintDeep)
                }
                .disabled(!updates.checksAutomatically)
                Divider()
                HStack(spacing: 10) {
                    Image(systemName: symbol).foregroundStyle(tint).font(.system(size: 13, weight: .semibold))
                    VStack(alignment: .leading, spacing: 2) {
                        Text(title).font(.system(size: 12.5, weight: .medium))
                        Text(detail).font(.system(size: 11.5)).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if updates.available != nil {
                        Button("Update…") { updates.showUpdate() }
                            .buttonStyle(.borderedProminent).tint(Palette.tintDeep)
                    } else if updates.state == .checking {
                        ProgressView().controlSize(.small)
                    } else {
                        Button("Check Now") { updates.check() }
                            .disabled(!updates.canCheckForUpdates)
                    }
                }
                Text("When enabled, automatic checks run once a day. Updates show what’s new and install in place after their signature is verified.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .panel(radius: 16, padding: 16)
        }
    }

    private var symbol: String {
        switch updates.state {
        case .available: return "arrow.down.circle.fill"
        case .failed: return "exclamationmark.triangle.fill"
        case .upToDate: return "checkmark.circle.fill"
        default: return "clock.arrow.circlepath"
        }
    }

    private var tint: Color {
        switch updates.state {
        case .available, .upToDate: return Palette.tint
        case .failed: return Palette.paused
        default: return .secondary
        }
    }

    private var title: String {
        switch updates.state {
        case .available(let release): return String(localized: "Version \(release.version) is available")
        case .upToDate: return String(localized: "Bambu Bar is up to date")
        case .failed: return String(localized: "Couldn’t check for updates")
        case .checking: return String(localized: "Checking…")
        case .idle: return String(localized: "Version \(updates.currentVersion)")
        }
    }

    private var detail: String {
        if case .failed(let message) = updates.state { return message }
        if case .available = updates.state { return String(localized: "You have \(updates.currentVersion). Bambu Bar installs the update and restarts.") }
        guard let last = updates.lastChecked else { return String(localized: "Not checked yet") }
        return String(localized: "Version \(updates.currentVersion) · last checked \(last.formatted(.relative(presentation: .named)))")
    }
}
