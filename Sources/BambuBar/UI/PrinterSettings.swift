import SwiftUI

/// The selected printer's own settings, at the bottom of the Printer page: its name (with the announced one to fall
/// back on), model, serial, status, address and access code, camera, ignored codes, and Forget.
struct PrinterSettingsGroup: View {
    @Environment(PrinterStore.self) private var store

    @State private var name = ""
    @State private var host = ""
    @State private var code = ""
    @State private var editingConnection = false
    @State private var confirmForget = false

    var body: some View {
        Group {
            if let config = store.config {
                SettingsGroup("Settings", symbol: "gearshape") {
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
                    ignoredCodes(config)
                    Divider()
                    HStack {
                        Text("Bambu Bar follows the printer if its IP address changes.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        Spacer()
                        Button("Forget Printer…", role: .destructive) { confirmForget = true }
                    }
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

    private func cameraNote(for config: PrinterConfig) -> LocalizedStringKey {
        if CameraTransport.forModel(config.modelName) == .rtsps {
            return "Shows the printer's live view. It needs **LAN Only Liveview** turned on on the printer (Settings → Network on its screen)."
        }
        return "Shows the printer's live view."
    }

    /// The codes hidden with Ignore next to an issue, each with Bambu's text for this model and a way to show it again.
    /// Codes are only added from an issue the printer reported, never typed in.
    @ViewBuilder private func ignoredCodes(_ config: PrinterConfig) -> some View {
        let ids = config.ignoredIssues ?? []
        Divider()
        SettingsRow("Ignored codes") {
            if ids.isEmpty { Text("None", comment: "Printers page: no ignored codes").foregroundStyle(.secondary) }
        }
        if ids.isEmpty {
            Text("To stop showing a code for this printer, click **Ignore** next to the issue on the Printer page.")
                .font(.system(size: 11.5)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        ForEach(ids, id: \.self) { id in
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(id).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                    if let text = HMSCatalog.shared.issueText(id, model: String(config.serial.prefix(3))), !text.isEmpty {
                        Text(text).font(.system(size: 11.5)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                Spacer(minLength: 12)
                Button("Show Again") { store.unignore(id, for: config.serial) }
                    .controlSize(.small)
                    .help("Remove this code from the ignored codes")
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
