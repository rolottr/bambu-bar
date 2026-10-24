import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// First run: pick the printer Bambu Bar found on the network (or type its address). The access code comes from
/// Bambu Studio or OrcaSlicer when either knows the printer (on this Mac, or a settings file from another Mac);
/// otherwise it's typed in from the printer's screen.
struct SetupPage: View {
    @Environment(PrinterStore.self) private var store
    @Environment(Navigation.self) private var navigation

    @State private var host = ""
    @State private var serial = ""
    @State private var name = ""
    @State private var modelCode = "N7"
    @State private var code = ""
    @State private var selected: String?
    @State private var manual = false
    /// The typed address is a Klipper printer with Moonraker, not a Bambu Lab printer.
    @State private var klipper = false
    @State private var openAtLogin = true
    /// The one found printer is picked automatically once; after that only the person changes the selection.
    @State private var autoPicked = false
    /// Where the access code came from ("Bambu Studio"…), nil when typed.
    @State private var codeSource: String?
    @State private var alternativeCodes: [String] = []
    @State private var importNote: String?
    @State private var dropTargeted = false
    /// Reading the serial from the printer at a typed address.
    @State private var identifying = false
    /// Why the last Connect didn't get anywhere (nothing at the address, not a Bambu printer…).
    @State private var problem: String?
    /// Connect was tried (Return in the code field) before the printer and code were both there.
    @State private var attempted = false
    @FocusState private var codeFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            if adding {
                PageHeader(title: "Add a Printer", subtitle: String(localized: "Bambu Bar watches every printer you add; pick which one the menu bar shows in Settings.")) {
                    Button("Cancel") { navigation.addingPrinter = false }
                }
            } else {
                hero
            }
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    SectionLabel("1  Choose your printer")
                    Spacer()
                    if newPrinters.isEmpty {
                        ProgressView().controlSize(.small)
                    }
                    Button("Search Again") { store.searchForPrinters() }
                        .controlSize(.small)
                }
                if store.localNetworkBlocked {
                    localNetworkNotice
                } else if newPrinters.isEmpty && !manual {
                    HStack(spacing: 10) {
                        Image(systemName: "wifi").foregroundStyle(.secondary)
                        Text("Looking for printers on this network. Printers announce themselves every few seconds; make sure yours is on and on the same network as this Mac.")
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .panel(radius: 14, padding: 12)
                }
                if store.readySlicerPrinters.count >= 2 {
                    addAllCard(store.readySlicerPrinters)
                }
                ForEach(newPrinters) { printer in
                    discoveredRow(printer)
                }
                ForEach(slicerOnlyPrinters) { printer in
                    discoveredRow(DiscoveredPrinter(serial: printer.serial, host: printer.host ?? "", name: printer.serial,
                                                    modelCode: "", firmware: nil, connectMode: nil, lastSeen: .distantPast))
                }
                DisclosureGroup("Enter address manually", isExpanded: $manual) {
                    VStack(spacing: 10) {
                        Toggle("Klipper printer with Moonraker (Creality K1, Voron…)", isOn: $klipper)
                            .toggleStyle(.checkbox)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .onChange(of: klipper) { selected = nil; problem = nil }
                        field("IP address", text: $host, prompt: Text(verbatim: "192.168.1.50"))
                        if !klipper {
                            field("Serial number", text: $serial, prompt: Text("Optional"))
                        }
                        field("Name", text: $name, prompt: Text("Optional"))
                        (klipper
                             ? Text("Bambu Bar connects to Moonraker on port 7125; no access code is needed on the local network.")
                             : Text("Only the IP address is needed: it's on the printer's screen under Settings → Network. Bambu Bar reads the serial number from the printer."))
                            .font(.system(size: 11.5, weight: .regular))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .padding(.top, 8)
                }
                .font(.system(size: 12, weight: .medium))
            }

            if !usesMoonraker {
                VStack(alignment: .leading, spacing: 10) {
                    SectionLabel("2  Access code")
                    if let codeSource {
                        foundCode(codeSource)
                    } else {
                        codeEntry
                    }
                }
            }

            if !adding {
                Toggle(isOn: $openAtLogin) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Open Bambu Bar at login").font(.system(size: 12.5, weight: .medium))
                        Text("It starts quietly in the menu bar, without a Dock icon.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.checkbox)
                .tint(Palette.accentDeep)
            }

            HStack(spacing: 12) {
                Button(action: connect) {
                    HStack(spacing: 6) {
                        if identifying || store.phase == .connecting { ProgressView().controlSize(.small).tint(.white) }
                        Text(identifying ? "Finding Printer…" : store.phase == .connecting ? "Connecting…" : "Connect")
                    }
                    .frame(width: 150)
                }
                .buttonStyle(ControlButtonStyle(tint: Palette.accentDeep, prominent: true))
                .frame(width: 150)
                .disabled(!canConnect)
                .keyboardShortcut(.defaultAction)
                if let detail = problem ?? store.linkDetail {
                    Label(detail, systemImage: "exclamationmark.triangle.fill")
                        .font(.system(size: 12))
                        .foregroundStyle(Palette.paused)
                        .fixedSize(horizontal: false, vertical: true)
                } else if let missing = missingStep, attempted || !code.isEmpty {
                    Label(missing, systemImage: "arrow.up.circle")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .onAppear {
            store.reloadSlicerPrinters()
            adoptCandidate()
        }
        .onChange(of: store.slicerPrinters) { fillCodeFromSlicer() }
        .onChange(of: navigation.setupCandidate) { adoptCandidate() }
        .onChange(of: host) { problem = nil }
        .onChange(of: code) { problem = nil }
        .onChange(of: newPrinters.map(\.serial)) {
            if !autoPicked, host.isEmpty, newPrinters.count == 1, let only = newPrinters.first {
                autoPicked = true
                pick(only)
            }
        }
    }

    /// The search can't get out: say why, and where the switch is.
    private var localNetworkNotice: some View {
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Palette.paused)
            Text(LocalNetwork.advice)
                .font(.system(size: 12))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            Button("Open Settings") { LocalNetwork.openSettings() }
                .controlSize(.small)
        }
        .panel(radius: 14, padding: 12, tint: Palette.paused)
    }

    private var hero: some View {
        HStack(spacing: 24) {
            PrinterArt(status: .sample, animated: true)
                .frame(width: 150, height: 180)
                .shadow(color: .black.opacity(0.3), radius: 14, y: 8)
            VStack(alignment: .leading, spacing: 8) {
                Text("Welcome to Bambu Bar")
                    .font(.system(size: 28, weight: .bold))
                Text("Your printer's progress, temperatures and filament, one click away in the menu bar. Bambu Bar talks to the printer directly over your network: no account, no cloud.")
                    .font(.system(size: 13.5))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.bottom, 4)
    }

    private var adding: Bool { store.isConfigured }

    /// Printers on the network that aren't set up yet.
    private var newPrinters: [DiscoveredPrinter] {
        store.discovered.filter { store.session($0.serial) == nil }
    }

    /// Printers a slicer knows (with an address) that haven't announced themselves on the network.
    private var slicerOnlyPrinters: [SlicerPrinter] {
        store.slicerPrinters.filter { p in
            p.host != nil && store.session(p.serial) == nil && !store.discovered.contains { $0.serial == p.serial }
        }
    }

    /// Several printers with saved codes: add them all in one go rather than one at a time.
    private func addAllCard(_ printers: [SlicerPrinter]) -> some View {
        let sources = ListFormatter.localizedString(byJoining: Set(printers.map(\.source)).sorted())
        let models = Dictionary(grouping: printers) { PrinterModel.find(code: modelCode(for: $0), serial: $0.serial)?.name ?? String(localized: "Other", comment: "Printers of an unknown model") }
            .sorted { $0.value.count == $1.value.count ? $0.key < $1.key : $0.value.count > $1.value.count }
            .map { "\($0.value.count) \($0.key)" }
            .joined(separator: " · ")
        return HStack(alignment: .center, spacing: 12) {
            Image(systemName: "square.stack.3d.down.right.fill")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(Palette.accent)
                .frame(width: 30, height: 30)
                .background(Circle().fill(Palette.accent.opacity(0.15)))
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: String(localized: "\(printers.count) printers") + " · " + sources).font(.system(size: 13, weight: .semibold))
                Text(models).font(.system(size: 11.5)).foregroundStyle(.secondary).lineLimit(2)
                Text("Their access codes are saved there, so they can all be added at once.")
                    .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            Button {
                let firstPrinter = !adding
                store.addAll(printers)
                navigation.addingPrinter = false
                if firstPrinter && openAtLogin && !LoginItem.shared.isEnabled && !store.isDemo { LoginItem.shared.setEnabled(true) }
            } label: {
                Text("Add All \(printers.count)").frame(width: 120)
            }
            .buttonStyle(ControlButtonStyle(tint: Palette.accentDeep, prominent: true))
            .frame(width: 120)
        }
        .panel(radius: 16, padding: 14, tint: Palette.accent)
    }

    private func modelCode(for printer: SlicerPrinter) -> String {
        store.discovered.first { $0.serial == printer.serial }?.modelCode ?? ""
    }

    /// The code is already known: say where from, and offer to type one instead.
    private func foundCode(_ source: String) -> some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "key.fill")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(Palette.accent)
                .frame(width: 30, height: 30)
                .background(Circle().fill(Palette.accent.opacity(0.15)))
            VStack(alignment: .leading, spacing: 2) {
                Text("Found in \(source)").font(.system(size: 13, weight: .semibold))
                Text("Bambu Bar uses the access code saved there for this printer. It's kept in your keychain and only ever sent to the printer.")
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            Button("Type a Code Instead") {
                codeSource = nil
                code = ""
                alternativeCodes = []
                codeFocused = true
            }
            .controlSize(.small)
        }
        .panel(radius: 16, padding: 14, tint: Palette.accent)
    }

    private var codeEntry: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 16) {
                VStack(alignment: .leading, spacing: 8) {
                    SecureField("8-character access code", text: $code)
                        .textFieldStyle(.roundedBorder)
                        .font(.system(size: 15, design: .monospaced))
                        .frame(maxWidth: 260)
                        .focused($codeFocused)
                        .onSubmit(connect)
                    Text("On the printer's screen open **Settings → Network** (the Wi-Fi/LAN page) and read the **Access Code**. It's stored in your keychain and only ever sent to the printer.")
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: 440, alignment: .leading)
                }
                Spacer()
            }
            Divider()
            HStack(alignment: .center, spacing: 10) {
                Image(systemName: "square.and.arrow.down.on.square")
                    .foregroundStyle(dropTargeted ? Palette.accent : .secondary)
                VStack(alignment: .leading, spacing: 1) {
                    Text("Bambu Studio on another Mac?").font(.system(size: 12, weight: .semibold))
                    (importNote.map { Text(LocalizedStringKey($0)) } ?? Text("Drop its **BambuStudio.conf** here, or its BambuStudio folder. It's in **Library → Application Support → BambuStudio** in that user's home folder."))
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                Button("Choose File…", action: chooseSettingsFile)
                    .controlSize(.small)
            }
        }
        .panel(radius: 16, padding: 14, tint: dropTargeted ? Palette.accent : nil)
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            guard let provider = providers.first else { return false }
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                guard let url else { return }
                DispatchQueue.main.async { importSettings(url) }
            }
            return true
        }
    }

    private func chooseSettingsFile() {
        let panel = NSOpenPanel()
        panel.title = String(localized: "Choose Bambu Studio's settings")
        panel.message = String(localized: "Pick BambuStudio.conf, or the BambuStudio folder it's in.")
        panel.prompt = String(localized: "Use", comment: "Open panel button")
        panel.allowsMultipleSelection = false
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        // Library is hidden in Finder; open where the file lives.
        panel.showsHiddenFiles = true
        panel.directoryURL = SlicerImport.suggestedFolder
        guard panel.runModal() == .OK, let url = panel.url else { return }
        importSettings(url)
    }

    private func importSettings(_ url: URL) {
        let count = store.importSlicerSettings(from: url)
        if count == 0 {
            importNote = String(localized: "No Bambu Studio or OrcaSlicer access codes found in \(url.lastPathComponent).")
            return
        }
        importNote = nil
        if serial.isEmpty, let first = store.slicerPrinters.first {
            if let found = store.discovered.first(where: { $0.serial == first.serial }) {
                pick(found)
            } else if let host = first.host {
                pick(DiscoveredPrinter(serial: first.serial, host: host, name: first.serial, modelCode: "", lastSeen: Date()))
            } else {
                serial = first.serial
            }
        }
        fillCodeFromSlicer()
        if codeSource == nil {
            importNote = serial.isEmpty
                ? String(localized: "\(url.lastPathComponent) doesn't know this printer. Pick another printer or type the code.")
                : String(localized: "\(url.lastPathComponent) doesn't know \(serial). Pick another printer or type the code.", comment: "A file name, then a serial number")
        }
    }

    /// Fills in the code a slicer has for the chosen printer.
    private func fillCodeFromSlicer() {
        guard !serial.isEmpty, let printer = store.slicerPrinter(serial: serial) else { return }
        guard codeSource != nil || code.isEmpty else { return }  // never overwrite a typed code
        code = printer.accessCodes[0]
        alternativeCodes = Array(printer.accessCodes.dropFirst())
        codeSource = printer.source
    }

    private func discoveredRow(_ printer: DiscoveredPrinter) -> some View {
        let isSelected = selected == printer.serial
        let knownCode = store.slicerPrinter(serial: printer.serial)
        return Button { pick(printer) } label: {
            HStack(spacing: 12) {
                Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 17))
                    .foregroundStyle(isSelected ? Palette.accent : .secondary)
                VStack(alignment: .leading, spacing: 2) {
                    Text(printer.name).font(.system(size: 13, weight: .semibold))
                    Text("Bambu Lab \(printer.modelName) · \(printer.host) · \(printer.serial)")
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                }
                Spacer()
                if let knownCode {
                    Label(printer.lastSeen == .distantPast ? "From \(knownCode.source)" : "Code in \(knownCode.source)",
                          systemImage: "key.fill")
                        .font(.system(size: 10.5, weight: .semibold))
                        .foregroundStyle(Palette.accent)
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Capsule().fill(Palette.accent.opacity(0.12)))
                }
                if let mode = printer.connectMode {
                    Text(mode.lowercased() == "lan" ? "LAN Only" : "Cloud")
                        .font(.system(size: 10.5, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Capsule().fill(Color.primary.opacity(0.07)))
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .panel(radius: 14, padding: 12, tint: isSelected ? Palette.accent : nil)
    }

    private func field(_ title: LocalizedStringKey, text: Binding<String>, prompt: Text) -> some View {
        HStack {
            Text(title).font(.system(size: 12)).foregroundStyle(.secondary).frame(width: 110, alignment: .leading)
            TextField(title, text: text, prompt: prompt)
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 280)
                .onChange(of: text.wrappedValue) { selected = nil }
            Spacer()
        }
    }

    private func pick(_ printer: DiscoveredPrinter) {
        host = printer.host
        serial = printer.serial
        name = printer.name
        modelCode = printer.modelCode
        if codeSource != nil {
            // A code that came with another printer doesn't belong to this one.
            code = ""
            codeSource = nil
            alternativeCodes = []
        }
        fillCodeFromSlicer()
        DispatchQueue.main.async {
            selected = printer.serial
            codeFocused = codeSource == nil
        }
    }

    private func adoptCandidate() {
        if let candidate = navigation.setupCandidate {
            pick(candidate)
            navigation.setupCandidate = nil
        } else if !autoPicked, host.isEmpty, let first = newPrinters.first, newPrinters.count == 1 {
            autoPicked = true
            pick(first)
        }
    }

    private var canConnect: Bool {
        missingStep == nil && !identifying && store.phase != .connecting
    }

    /// What Connect still needs. Not the serial: when it is left blank, it is read from the printer.
    private var missingStep: String? {
        if host.trimmingCharacters(in: .whitespaces).isEmpty {
            return newPrinters.isEmpty && slicerOnlyPrinters.isEmpty
                ? String(localized: "Bambu Bar hasn't found your printer yet. Type its IP address under \u{201C}Enter address manually\u{201D}.")
                : String(localized: "Choose your printer above, or enter its address manually.")
        }
        if !usesMoonraker && code.trimmingCharacters(in: .whitespaces).count < 4 { return String(localized: "Enter the printer's access code.") }
        return nil
    }

    /// The manual entry is set to a Klipper printer (and no discovered Bambu printer is picked instead).
    private var usesMoonraker: Bool { klipper && manual }

    private func connect() {
        guard canConnect else {
            attempted = true
            if host.trimmingCharacters(in: .whitespaces).isEmpty && newPrinters.isEmpty && slicerOnlyPrinters.isEmpty {
                manual = true
            }
            return
        }
        problem = nil
        let address = host.trimmingCharacters(in: .whitespaces)
        if usesMoonraker {
            connectMoonraker(at: address)
        } else if !serial.trimmingCharacters(in: .whitespaces).isEmpty {
            finishConnecting()
        } else if let seen = store.discovered.first(where: { $0.host == address }) {
            if adopt(seen.serial) { finishConnecting() }
        } else {
            identifying = true
            Task {
                let result = await store.identifyPrinter(at: address)
                identifying = false
                // The person may have moved on to another address meanwhile.
                guard host.trimmingCharacters(in: .whitespaces) == address else { return }
                switch result {
                case .success(let found):
                    if adopt(found) { finishConnecting() }
                case .failure(let failure):
                    problem = failure.message
                }
            }
        }
    }

    /// Takes the serial found for a typed address. A code that came with the printer picked before belongs to
    /// that one: swap in this printer's code from the slicer, or ask for it. False when the code is now missing.
    private func adopt(_ found: String) -> Bool {
        serial = found
        if codeSource != nil, store.slicerPrinter(serial: found)?.accessCodes.first != code {
            code = ""
            codeSource = nil
            alternativeCodes = []
            fillCodeFromSlicer()
        }
        guard code.trimmingCharacters(in: .whitespaces).count >= 4 else {
            attempted = true
            codeFocused = true
            return false
        }
        return true
    }

    /// Checks Moonraker answers before saving the printer, and names and types it from Klipper's hostname.
    private func connectMoonraker(at address: String) {
        identifying = true
        Task {
            let probe = PrinterConfig(host: address, serial: "", name: "", modelCode: "", kind: .moonraker).endpoint
            let result = await MoonrakerClient.hostname(host: probe.host, port: probe.port)
            identifying = false
            guard host.trimmingCharacters(in: .whitespaces) == address else { return }
            switch result {
            case .success(let hostname):
                let typedName = name.trimmingCharacters(in: .whitespaces)
                let config = PrinterConfig(host: address, serial: PrinterConfig.moonrakerID(host: address),
                                           name: typedName.isEmpty ? (hostname.isEmpty ? address : hostname) : typedName,
                                           modelCode: MoonrakerClient.modelCode(forHostname: hostname), kind: .moonraker)
                let firstPrinter = !adding
                store.connect(to: config, accessCode: "")
                navigation.addingPrinter = false
                if firstPrinter && openAtLogin && !LoginItem.shared.isEnabled { LoginItem.shared.setEnabled(true) }
            case .failure(let reason):
                problem = reason.message
            }
        }
    }

    private func finishConnecting() {
        let serial = self.serial.trimmingCharacters(in: .whitespaces).uppercased()
        let seen = store.discovered.first { $0.serial == serial }
        let typedName = name.trimmingCharacters(in: .whitespaces)
        let config = PrinterConfig(host: host.trimmingCharacters(in: .whitespaces),
                                   serial: serial,
                                   name: typedName.isEmpty ? store.announcedName(for: serial) ?? serial : typedName,
                                   // Typed in by hand, the serial's prefix tells the model better than the default.
                                   modelCode: selected == serial ? modelCode : seen?.modelCode ?? "")
        let firstPrinter = !adding
        store.connect(to: config, accessCode: code, alternatives: alternativeCodes)
        navigation.addingPrinter = false
        if firstPrinter && openAtLogin && !LoginItem.shared.isEnabled { LoginItem.shared.setEnabled(true) }
    }
}
