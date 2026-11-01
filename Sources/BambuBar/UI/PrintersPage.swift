import SwiftUI

/// Printer management and connection settings, separate from application preferences.
struct PrintersPage: View {
    @Environment(PrinterStore.self) private var store

    @State private var name = ""
    @State private var host = ""
    @State private var code = ""
    @State private var editingConnection = false
    @State private var confirmForget = false

    var body: some View {
        @Bindable var store = store
        VStack(alignment: .leading, spacing: 22) {
            PageHeader(title: "Printers")

            SettingsGroup("Printers", symbol: "printer.dotmatrix.fill") {
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
                SettingsGroup(store.hasMultiplePrinters ? "\(config.name)" : "Printer", symbol: "printer.fill") {
                    SettingsRow("Name") {
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
                    SettingsRow("Model") { Text("Bambu Lab \(config.modelName)").foregroundStyle(.secondary) }
                    SettingsRow("Serial number") { Text(config.serial).foregroundStyle(.secondary).textSelection(.enabled) }
                    SettingsRow("Status") {
                        HStack(spacing: 6) {
                            Circle().fill(store.isOnline ? Palette.accent : Palette.paused).frame(width: 7, height: 7)
                            Text(store.linkSummary).foregroundStyle(.secondary)
                        }
                    }
                    if editingConnection {
                        SettingsRow("IP address") {
                            TextField("IP address", text: $host).textFieldStyle(.roundedBorder).frame(width: 220)
                        }
                        SettingsRow("Access code") {
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
                        SettingsRow("IP address") {
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
                    SettingsToggle("Camera", isOn: Binding(
                        get: { config.cameraEnabled == true },
                        set: { store.setCameraEnabled($0, for: config.serial) }))
                    Text(cameraNote(for: config))
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    if config.cameraEnabled == true && !store.cameraEnabled {
                        HStack {
                            Text("The camera is off for every printer in Settings.")
                                .font(.system(size: 11.5)).foregroundStyle(Palette.paused)
                            Spacer()
                            Button("Open Settings") { MainWindow.show(.settings) }.controlSize(.small)
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

            SettingsGroup("Controls", symbol: "slider.horizontal.3") {
                SettingsToggle("Ask before stopping a print", isOn: $store.confirmStop)
                Text("Pause, stop and speed changes need **LAN Only** mode with **Developer Mode** on the printer (Settings → Network on its screen). Reading status and the chamber light work in any mode.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
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

    private func cameraNote(for config: PrinterConfig) -> LocalizedStringKey {
        if CameraTransport.forModel(config.modelName) == .rtsps {
            return "Shows the printer's live view. It needs **LAN Only Liveview** turned on on the printer (Settings → Network on its screen)."
        }
        return "Shows the printer's live view."
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

}
