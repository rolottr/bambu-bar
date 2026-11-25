import SwiftUI

struct SettingsPage: View {
    @Environment(PrinterStore.self) private var store
    @Environment(LoginItem.self) private var loginItem
    @Environment(Navigation.self) private var navigation

    @State private var language = AppLanguage.override

    var body: some View {
        @Bindable var store = store
        VStack(alignment: .leading, spacing: 22) {
            PageHeader(title: "Settings")

            SettingsGroup("General", symbol: "gearshape") {
                SettingsRow(
                    "Open at login",
                    help: "At login Bambu Bar starts in the menu bar only, without a Dock icon."
                ) {
                    Toggle("Open at login", isOn: Binding(get: { loginItem.isEnabled }, set: { loginItem.setEnabled($0) }))
                        .toggleStyle(.switch).labelsHidden().tint(Palette.tintDeep)
                }
                if loginItem.error != nil || loginItem.status == .requiresApproval {
                    HStack(alignment: .firstTextBaseline) {
                        if let error = loginItem.error {
                            Text("Couldn’t change Open at Login. \(error)")
                                .foregroundStyle(Palette.failed)
                        } else {
                            Text("Allow Bambu Bar in System Settings → General → Login Items.")
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Open System Settings") { loginItem.openSystemSettings() }
                            .controlSize(.small).fixedSize()
                    }
                    .font(.system(size: 11.5))
                }
                Divider()
                SettingsRow("Setup guide") {
                    Button("Setup Guide…") { navigation.openOnboardingSplash(firstRun: false) }
                        .controlSize(.small)
                        .fixedSize()
                }
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
                        Text("Switches to \(AppLanguage.name(of: language ?? AppLanguage.system)) on restart.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        Spacer()
                        Button("Restart Now") { AppLanguage.relaunch() }
                            .controlSize(.small)
                            .fixedSize()
                    }
                }
            }

            AppearanceGroup()

            SettingsGroup("Camera", symbol: "video.fill") {
                SettingsToggle(
                    "Show the printer camera",
                    help: "Turn the camera on for each printer at the bottom of its Printer page. While off, the camera is hidden everywhere.",
                    isOn: $store.cameraEnabled
                )
                if store.cameraEnabled {
                    SettingsToggle(
                        "Keep the camera connected during prints",
                        help: "Shows immediately when opened, while using one of the printer's camera connections during prints.",
                        isOn: $store.cameraStaysOnWhilePrinting
                    )
                }
            }

            SettingsGroup("Controls", symbol: "slider.horizontal.3") {
                SettingsToggle(
                    "Ask before stopping a print",
                    help: "Pause, stop and speed changes require LAN Only mode with Developer Mode on the printer.",
                    isOn: $store.confirmStop
                )
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
            SettingsRow(
                "Style",
                help: "Liquid Glass uses macOS Tahoe glass panels; Classic keeps the frosted look."
            ) {
                Picker("", selection: $appearance.style) {
                    ForEach(Appearance.Style.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .tint(Palette.tintDeep)
            }
            Divider()
            SettingsRow(
                "Accent colour",
                help: "Applies to buttons, switches, links and selections. Printer states keep their own colours."
            ) {
                AccentPicker(selection: $appearance.accent)
            }
        }
    }
}

/// A row of colour dots like System Settings' accent colours, the choice's name before them.
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
        SettingsGroup("Updates", symbol: "arrow.down.circle.fill") {
            #if !APP_STORE
            if !updates.supportsAutomaticUpdates {
                HStack(spacing: 10) {
                    Text("Supporter license required for automatic updates.")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                    Spacer()
                    Button("Activate License…") { MainWindow.show(.about) }
                        .controlSize(.small)
                        .fixedSize()
                }
            }
            #endif
            SettingsToggle(
                "Check for updates automatically",
                help: "Checks once a day and verifies the update signature before installing.",
                isOn: $updates.checksAutomatically
            )
            SettingsToggle(
                "Download and install updates automatically",
                disabled: !updates.supportsAutomaticUpdates || !updates.checksAutomatically,
                isOn: $updates.downloadsAutomatically
            )
            Divider()
            HStack(spacing: 10) {
                Image(systemName: symbol).foregroundStyle(tint).font(.system(size: 13, weight: .semibold))
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.system(size: 12.5, weight: .medium))
                    Text(detail).font(.system(size: 11.5)).foregroundStyle(.secondary)
                }
                Spacer()
                if updates.available != nil {
                    if updates.supportsAutomaticUpdates {
                        Button("Update…") { updates.showUpdate() }
                            .buttonStyle(.borderedProminent).tint(Palette.tintDeep)
                            .fixedSize()
                    } else {
                        Button("Download…") { NSWorkspace.shared.open(Website.download) }
                            .buttonStyle(.borderedProminent).tint(Palette.tintDeep)
                            .fixedSize()
                    }
                } else if updates.state == .checking {
                    ProgressView().controlSize(.small)
                } else {
                    Button("Check Now") { updates.check() }
                        .disabled(!updates.canCheckForUpdates)
                        .fixedSize()
                }
            }
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
        if case .available = updates.state {
            return updates.supportsAutomaticUpdates
                ? String(localized: "Current: \(updates.currentVersion)")
                : String(localized: "Download from bambubar.app")
        }
        guard let last = updates.lastChecked else { return String(localized: "Not checked yet") }
        return String(localized: "Version \(updates.currentVersion) · checked \(last.formatted(.relative(presentation: .named)))")
    }
}
