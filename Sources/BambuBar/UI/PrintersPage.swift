import SwiftUI

/// Printer management and connection settings, separate from application preferences.
struct PrintersPage: View {
    @Environment(PrinterStore.self) private var store

    @State private var name = ""
    @State private var host = ""
    @State private var code = ""
    @State private var editingConnection = false
    @State private var confirmForget = false
    /// The camera editor, while it is open: the entered values, what Test said, and the serial it belongs to.
    @State private var cameraURL = ""
    @State private var cameraUser = ""
    @State private var cameraPassword = ""
    @State private var cameraURLProblem: String?
    @State private var testState: CameraTestState = .idle
    @State private var tester: CameraClient?
    /// The editor is open for a camera not entered yet: shown without anything being saved.
    @State private var editingCamera = false

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
                    if config.cameraEnabled == true { cameraSource(config) }
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
        if config.cameraURL != nil {
            return "Shows a camera you enter yourself, over RTSP. It works while the printer is off."
        }
        if CameraTransport.forModel(config.modelName) == .rtsps {
            return "Shows the printer's live view. It needs **LAN Only Liveview** turned on on the printer (Settings → Network on its screen)."
        }
        return "Shows the printer's live view."
    }

    // MARK: - Where the camera comes from

    /// What a test attempt has to say.
    private enum CameraTestState: Equatable {
        case idle
        case testing
        case worked(String)
        case failed(String)
    }

    /// The printer's own camera, or one entered by hand, with an editor that applies on Save so the live feed
    /// isn't reconnected on every keystroke.
    @ViewBuilder private func cameraSource(_ config: PrinterConfig) -> some View {
        Picker("", selection: Binding(
            get: { config.cameraURL != nil || editingCamera },
            set: { external in
                if external {
                    beginEditingCamera(config)
                } else {
                    endEditingCamera()
                    store.clearExternalCamera(for: config.serial)
                }
            })) {
            Text("Printer's built-in camera").tag(false)
            Text("External RTSP stream").tag(true)
        }
        .pickerStyle(.radioGroup)
        .labelsHidden()
        if config.cameraURL != nil || editingCamera { cameraEditor(config) }
    }

    @ViewBuilder private func cameraEditor(_ config: PrinterConfig) -> some View {
        SettingsRow("Stream URL") {
            TextField("rtsp://192.168.1.40:554/stream1", text: $cameraURL)
                .textFieldStyle(.roundedBorder).frame(width: 260)
                .onChange(of: cameraURL) { cameraURLProblem = nil; testState = .idle }
        }
        SettingsRow("Username") {
            TextField("Username", text: $cameraUser).textFieldStyle(.roundedBorder).frame(width: 260)
        }
        SettingsRow("Password") {
            SecureField("Password", text: $cameraPassword).textFieldStyle(.roundedBorder).frame(width: 260)
                .onSubmit { applyCamera(config) }
        }
        if let problem = cameraURLProblem {
            Text(problem).font(.system(size: 11.5)).foregroundStyle(Palette.paused)
                .fixedSize(horizontal: false, vertical: true)
        }
        HStack(spacing: 10) {
            switch testState {
            case .idle: EmptyView()
            case .testing: Text("Testing…").font(.system(size: 11.5)).foregroundStyle(.secondary)
            case .worked(let detail):
                Text("\u{2713} \(detail)").font(.system(size: 11.5)).foregroundStyle(Palette.accent)
            case .failed(let reason):
                Text(reason).font(.system(size: 11.5)).foregroundStyle(Palette.paused)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button("Test") { testCamera() }
                .controlSize(.small)
                .disabled(parsedCameraURL == nil || testState == .testing)
            Button("Save") { applyCamera(config) }
                .controlSize(.small)
                .buttonStyle(.borderedProminent)
                .tint(Palette.accentDeep)
                .disabled(parsedCameraURL == nil)
        }
    }

    private var parsedCameraURL: RTSPURL? {
        guard case .success(let url) = RTSPURL.parse(cameraURL) else { return nil }
        return url
    }

    /// Opens the editor on whatever the printer already has, or empty for a camera being added. Nothing is
    /// saved until Save: picking the option must not point a printer at a camera nobody has typed in.
    private func beginEditingCamera(_ config: PrinterConfig) {
        cameraURL = config.cameraURL ?? ""
        cameraUser = config.cameraUsername ?? ""
        cameraPassword = Keychain.cameraPassword(for: config.serial) ?? ""
        cameraURLProblem = nil
        testState = .idle
        editingCamera = true
    }

    private func endEditingCamera() {
        tester?.stop()
        tester = nil
        testState = .idle
        cameraURLProblem = nil
        editingCamera = false
    }

    /// Saves what was typed. Credentials inside the URL win and move into the fields, so what was pasted is
    /// what connects.
    private func applyCamera(_ config: PrinterConfig) {
        switch RTSPURL.parse(cameraURL) {
        case .failure(let problem):
            cameraURLProblem = problem.message
        case .success(let url):
            let entered = PrinterConfig.enteredCamera(url: url, username: cameraUser, password: cameraPassword)
            cameraUser = entered.username
            cameraPassword = entered.password
            cameraURL = entered.url.absolute
            cameraURLProblem = nil
            endEditingCamera()
            store.setExternalCamera(url: entered.url, username: entered.username, password: entered.password,
                                    for: config.serial)
        }
    }

    /// One attempt against what was typed: it saves nothing and leaves the live feed alone.
    private func testCamera() {
        guard let url = parsedCameraURL else { return }
        tester?.stop()
        let username = url.username.isEmpty ? cameraUser : url.username
        let password = url.password.isEmpty ? cameraPassword : url.password
        let source = CameraSource.external(ExternalCamera(url: url, username: username, password: password))
        let client = makeCameraClient(CameraConfig(host: url.host, serial: store.config?.serial ?? "",
                                                   accessCode: "", source: source))
        tester = client
        testState = .testing
        client.onEvent = { event in
            MainActor.assumeIsolated {
                guard client === tester else { return }
                switch event {
                case .started(let detail, _):
                    testState = .worked(detail)
                    endEditingCamera()
                case .image:
                    testState = .worked(String(localized: "The camera is streaming."))
                    endEditingCamera()
                case .failed(let failure):
                    testState = .failed(failure.message(from: source))
                    tester?.stop()
                    tester = nil
                case .video:
                    break
                }
            }
        }
        client.start()
        // Nothing in 15 seconds is a failure too, so Test always answers.
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) {
            if testState == .testing {
                testState = .failed(CameraFailure.timedOut.message(from: source))
                tester?.stop()
                tester = nil
            }
        }
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
