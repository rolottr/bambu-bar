import SwiftUI

struct SettingsPage: View {
    @Environment(PrinterStore.self) private var store
    @Environment(LoginItem.self) private var loginItem

    @State private var name = ""
    @State private var host = ""
    @State private var code = ""
    @State private var editingConnection = false
    @State private var confirmForget = false

    var body: some View {
        @Bindable var store = store
        VStack(alignment: .leading, spacing: 22) {
            PageHeader(title: "Settings")

            group("Printers", symbol: "printer.dotmatrix.fill") {
                ForEach(store.sessions) { session in
                    printerRow(session)
                    Divider()
                }
                HStack {
                    Text(store.isConfigured ? "Bambu Bar watches all of them at once." : "No printer set up yet.")
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    Spacer()
                    Button("Add Printer…") { MainWindow.addPrinter() }
                }
            }

            if let config = store.config {
                group(store.hasMultiplePrinters ? config.name : "Printer", symbol: "printer.fill") {
                    row("Name") {
                        HStack(spacing: 8) {
                            if name != config.name {
                                Button("Revert") { name = config.name }.controlSize(.small)
                                Button("Save") { store.rename(config.serial, to: name) }
                                    .controlSize(.small)
                                    .buttonStyle(.borderedProminent)
                                    .tint(Palette.accentDeep)
                                    .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
                            }
                            TextField("Name", text: $name)
                                .textFieldStyle(.roundedBorder)
                                .frame(width: 220)
                                .onSubmit { store.rename(config.serial, to: name) }
                        }
                    }
                    if let announced = store.announcedName(for: config.serial), announced != config.name {
                        HStack {
                            Text("The printer calls itself \u{201C}\(announced)\u{201D}. This name is only used in Bambu Bar.")
                                .font(.system(size: 11.5)).foregroundStyle(.secondary)
                            Spacer()
                            Button("Use \u{201C}\(announced)\u{201D}") { store.rename(config.serial, to: announced) }
                                .controlSize(.small)
                        }
                    }
                    row("Model") { Text("Bambu Lab \(config.modelName)").foregroundStyle(.secondary) }
                    row("Serial number") { Text(config.serial).foregroundStyle(.secondary).textSelection(.enabled) }
                    row("Status") {
                        HStack(spacing: 6) {
                            Circle().fill(store.isOnline ? Palette.accent : Palette.paused).frame(width: 7, height: 7)
                            Text(store.linkSummary).foregroundStyle(.secondary)
                        }
                    }
                    if editingConnection {
                        row("IP address") {
                            TextField("IP address", text: $host).textFieldStyle(.roundedBorder).frame(width: 220)
                        }
                        row("Access code") {
                            SecureField("Access code", text: $code).textFieldStyle(.roundedBorder).frame(width: 220)
                                .onSubmit(applyConnection)
                        }
                        HStack {
                            Spacer()
                            Button("Cancel") { editingConnection = false }
                            Button("Reconnect", action: applyConnection)
                                .buttonStyle(.borderedProminent)
                                .tint(Palette.accentDeep)
                                .disabled(host.isEmpty || code.count < 4)
                        }
                    } else {
                        row("IP address") {
                            HStack(spacing: 10) {
                                Text(config.host).foregroundStyle(.secondary).textSelection(.enabled)
                                Button("Change…") {
                                    host = config.host
                                    code = store.accessCode ?? ""
                                    editingConnection = true
                                }
                                .controlSize(.small)
                            }
                        }
                    }
                    Divider()
                    HStack {
                        Text("Bambu Bar follows the printer if its IP address changes.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        Spacer()
                        Button("Forget Printer…", role: .destructive) { confirmForget = true }
                    }
                }
            }

            group("Menu bar", symbol: "menubar.rectangle") {
                if store.hasMultiplePrinters {
                    row("Printer in the menu bar") {
                        Picker("", selection: $store.menuBarSerial) {
                            Text("Automatic").tag(String?.none)
                            Divider()
                            ForEach(store.sessions) { Text($0.config.name).tag(Optional($0.id)) }
                        }
                        .labelsHidden()
                        .frame(width: 220)
                    }
                    if store.menuBarSerial == nil {
                        Text("Automatic shows whichever printer is busy (paused first, then printing), otherwise the one you last looked at.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                row("While printing, show") {
                    Picker("", selection: $store.menuBarStyle) {
                        ForEach(MenuBarStyle.allCases) { Text($0.title).tag($0) }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                row("Open at login") {
                    Toggle("", isOn: Binding(get: { loginItem.isEnabled }, set: { loginItem.setEnabled($0) }))
                        .toggleStyle(.switch).labelsHidden().tint(Palette.accentDeep)
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

            group("Notifications", symbol: "bell.badge.fill") {
                toggle("Print finished", isOn: $store.notifyFinished)
                toggle("Print failed, canceled or an error", isOn: $store.notifyFailed)
                toggle("Print paused (filament runout, user)", isOn: $store.notifyPaused)
                toggle("Print started", isOn: $store.notifyStarted)
            }

            group("Controls", symbol: "slider.horizontal.3") {
                toggle("Ask before stopping a print", isOn: $store.confirmStop)
                Text("Pause, stop and speed changes need **LAN Only** mode with **Developer Mode** on the printer (Settings → Network on its screen). Reading status and the chamber light work in any mode.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            group("Camera", symbol: "video.fill") {
                toggle("Keep the camera connected during prints", isOn: $store.cameraStaysOnWhilePrinting)
                Text("The picture shows at once when you open it, but the stream runs for the whole print and uses one of the printer's few camera connections, so Bambu Handy or Bambu Studio may not get the camera meanwhile.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            UpdatesGroup()

            group("Desktop widget", symbol: "widget.small") {
                HStack {
                    Text("See what each widget size shows, your printers as widgets, and how to add one.")
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    Spacer()
                    Button("Show Widgets") { MainWindow.show(.widgets) }
                }
            }
        }
        .onAppear { name = store.config?.name ?? "" }
        .onChange(of: store.config) { name = store.config?.name ?? "" }
        .alert("Forget \(store.printerName)?", isPresented: $confirmForget) {
            Button("Forget", role: .destructive) { if let serial = store.config?.serial { store.forget(serial) } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Bambu Bar disconnects and removes the access code from your keychain. Print history stays.")
        }
    }

    private func printerRow(_ session: PrinterSession) -> some View {
        let isCurrent = session.id == store.current?.id
        return HStack(spacing: 12) {
            PrinterArt(status: session.status, model: session.config.modelName, dimmed: !session.isOnline, animated: false)
                .frame(width: 28, height: 34)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(session.config.name).font(.system(size: 12.5, weight: .semibold))
                    if store.hasMultiplePrinters && session.id == store.menuBarSession?.id {
                        Image(systemName: "menubar.rectangle")
                            .font(.system(size: 10))
                            .foregroundStyle(.secondary)
                            .help("Shown in the menu bar")
                    }
                }
                Text("Bambu Lab \(session.config.modelName) · \(session.config.host)")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            }
            Spacer()
            HStack(spacing: 5) {
                SessionDot(session: session)
                Text(session.shortState).font(.system(size: 11.5)).foregroundStyle(.secondary)
            }
            if store.hasMultiplePrinters {
                Button(isCurrent ? "Showing" : "Show") { store.select(session.id) }
                    .controlSize(.small)
                    .disabled(isCurrent)
            }
        }
    }

    private func applyConnection() {
        guard var config = store.config, !host.isEmpty, code.count >= 4 else { return }
        config.host = host.trimmingCharacters(in: .whitespaces)
        store.connect(to: config, accessCode: code)
        editingConnection = false
    }

    private func group<Content: View>(_ title: String, symbol: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(title, systemImage: symbol)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.primary)
            VStack(alignment: .leading, spacing: 12) { content() }
                .panel(radius: 16, padding: 16)
        }
    }

    private func row<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        HStack {
            Text(title).font(.system(size: 12.5))
            Spacer(minLength: 12)
            content().font(.system(size: 12.5))
        }
    }

    private func toggle(_ title: String, isOn: Binding<Bool>) -> some View {
        HStack {
            Text(title).font(.system(size: 12.5))
            Spacer()
            Toggle("", isOn: isOn).toggleStyle(.switch).labelsHidden().tint(Palette.accentDeep)
        }
    }
}

/// Automatic update checks, the result of the last one and a manual check.
private struct UpdatesGroup: View {
    @State private var updates = UpdateChecker.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Updates", systemImage: "arrow.down.circle.fill")
                .font(.system(size: 13, weight: .semibold))
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text("Check for updates automatically").font(.system(size: 12.5))
                    Spacer()
                    Toggle("", isOn: $updates.checksAutomatically).toggleStyle(.switch).labelsHidden().tint(Palette.accentDeep)
                }
                Divider()
                HStack(spacing: 10) {
                    Image(systemName: symbol).foregroundStyle(tint).font(.system(size: 13, weight: .semibold))
                    VStack(alignment: .leading, spacing: 2) {
                        Text(title).font(.system(size: 12.5, weight: .medium))
                        Text(detail).font(.system(size: 11.5)).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if let release = updates.available {
                        Button("Download") { updates.download(release) }
                            .buttonStyle(.borderedProminent).tint(Palette.accentDeep)
                    } else if updates.state == .checking {
                        ProgressView().controlSize(.small)
                    } else {
                        Button("Check Now") { Task { await updates.check() } }
                    }
                }
                Text("Bambu Bar reads the latest release of rolottr/bambu-bar on GitHub once a day and never installs anything by itself.")
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
        case .available, .upToDate: return Palette.accent
        case .failed: return Palette.paused
        default: return .secondary
        }
    }

    private var title: String {
        switch updates.state {
        case .available(let release): return "Version \(release.version) is available"
        case .upToDate: return "Bambu Bar is up to date"
        case .failed: return "Couldn’t check for updates"
        case .checking: return "Checking…"
        case .idle: return "Version \(updates.currentVersion)"
        }
    }

    private var detail: String {
        if case .failed(let message) = updates.state { return message }
        if case .available = updates.state { return "You have \(updates.currentVersion). Replace the app in Applications with the new download." }
        guard let last = updates.lastChecked else { return "Not checked yet" }
        return "Version \(updates.currentVersion) · last checked \(last.formatted(.relative(presentation: .named)))"
    }
}

/// A newer release, offered once in the main window's corner (Later hides it until the next launch).
struct UpdateOffer: View {
    var updates = UpdateChecker.shared

    var body: some View {
        ZStack {
            if let release = updates.offered {
                VStack(alignment: .leading, spacing: 12) {
                    HStack(alignment: .top, spacing: 12) {
                        Image(systemName: "arrow.down.circle.fill")
                            .font(.system(size: 26))
                            .foregroundStyle(Palette.accent)
                        VStack(alignment: .leading, spacing: 3) {
                            Text("Bambu Bar \(release.version) is available").font(.system(size: 13.5, weight: .semibold))
                            Text("You have \(updates.currentVersion). Download it from GitHub, then replace the app in your Applications folder.")
                                .font(.system(size: 12)).foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    HStack(spacing: 8) {
                        Spacer()
                        Button("Skip This Version") { updates.skip(release) }
                        Button("Later") { updates.dismissOffer() }
                        Button("Download") {
                            updates.download(release)
                            updates.dismissOffer()
                        }
                        .buttonStyle(.borderedProminent).tint(Palette.accentDeep)
                    }
                }
                .padding(16)
                .frame(width: 370)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(.white.opacity(0.1)))
                .shadow(color: .black.opacity(0.3), radius: 20, y: 8)
                .transition(.move(edge: .trailing).combined(with: .opacity))
            }
        }
        .animation(.spring(response: 0.45, dampingFraction: 0.85), value: updates.offered)
        .padding(22)
    }
}

/// Approximates the desktop widget chrome around a widget view.
struct WidgetFrame<Content: View>: View {
    var width: CGFloat
    var height: CGFloat
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(16)
            .frame(width: width, height: height)
            .background(WidgetBackdrop())
            .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).strokeBorder(.white.opacity(0.12), lineWidth: 0.75))
            .shadow(color: .black.opacity(0.25), radius: 10, y: 4)
            .environment(\.colorScheme, .dark)
    }
}
