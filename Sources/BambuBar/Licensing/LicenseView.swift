import SwiftUI

#if !APP_STORE

struct LicenseView: View {
    private var license: LicenseStore { .shared }
    @State private var key = ""
    @State private var confirmDeactivation = false

    var body: some View {
        if license.isInitializing {
            LicenseLoadingView()
        } else {
            form
        }
    }

    private var form: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label(license.hasAccess ? "Hero Supporter · Thank you for your support!" : "Bambu Bar License",
                  systemImage: license.hasAccess ? "star.circle.fill" : "key.fill")
                .font(.title3.weight(.semibold))
                .fixedSize(horizontal: false, vertical: true)
            Text(license.hasAccess
                 ? "This installation is activated and receives automatic updates."
                 : "Activate a supporter license to enable automatic updates and receive the Hero Supporter badge.")
                .fixedSize(horizontal: false, vertical: true)
            Text("Your license allows two active installations. Deactivate an old installation in Manage Purchase to make room for another.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if let saved = license.record {
                SavedLicenseKeyView(key: saved.key)
                    .id(saved.activationID)
                Text("Installation: \(saved.installationLabel)")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("Check License") { Task { await license.refresh() } }
                    Button("Deactivate This Installation", role: .destructive) { confirmDeactivation = true }
                }
                .disabled(license.isBusy)
            } else {
                HStack(spacing: 8) {
                    SecureField("License key", text: $key)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit { activate() }
                    Button("Activate") { activate() }
                        .buttonStyle(.borderedProminent)
                        .fixedSize()
                        .disabled(license.isBusy || key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    Link("Get a License", destination: PolarConfiguration.checkoutURL)
                        .fixedSize()
                }
            }
            if license.isBusy { ProgressView().controlSize(.small) }
            if let message = license.message {
                Text(message).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Text("© 2026 Clever Mechanisms").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Link("Manage Purchase", destination: PolarConfiguration.portalURL)
                    .font(.callout)
            }
        }
        .lineLimit(nil)
        .frame(maxWidth: .infinity, alignment: .leading)
        .confirmationDialog("Deactivate this installation?", isPresented: $confirmDeactivation) {
            Button("Deactivate", role: .destructive) { Task { await license.deactivate() } }
        } message: {
            Text("Frees one of your 2 activations and turns off automatic updates. You can keep using Bambu Bar.")
        }
    }

    private func activate() {
        guard !license.isBusy else { return }
        Task {
            let hadAccess = license.hasAccess
            await license.activate(key: key)
            guard license.hasAccess else { return }
            key = ""
            if !hadAccess { Confetti.celebrate() }
        }
    }
}

private struct LicenseLoadingView: View {
    var body: some View {
        ProgressView("Checking license…")
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct SavedLicenseKeyView: View {
    let key: String
    @State private var isRevealed = false
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("License key").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button(isRevealed ? "Hide License Key" : "Show License Key") {
                    isRevealed.toggle()
                }
                Button(copied ? "Copied" : "Copy") {
                    let pasteboard = NSPasteboard.general
                    let concealed = NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")
                    pasteboard.clearContents()
                    pasteboard.declareTypes([.string, concealed], owner: nil)
                    copied = pasteboard.setString(key, forType: .string)
                    pasteboard.setData(Data(), forType: concealed)
                }
            }
            Text(verbatim: isRevealed ? key : "••••-••••-••••-\(key.suffix(4))")
                .font(.system(.callout, design: .monospaced))
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
        .onDisappear {
            isRevealed = false
            copied = false
        }
    }
}
#endif
