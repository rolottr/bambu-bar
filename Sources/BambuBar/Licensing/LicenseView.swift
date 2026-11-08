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
        VStack(alignment: .leading, spacing: 16) {
            Label(license.hasAccess ? "Hero Supporter · Thank you for your support!" : "Support Bambu Bar", systemImage: license.hasAccess ? "star.circle.fill" : "key.fill")
                .font(.title2.weight(.semibold))
                .fixedSize(horizontal: false, vertical: true)
            Text(license.hasAccess ? "Your supporter license enables automatic updates on this Mac." : "Bambu Bar is free to use. Activate a supporter license to enable automatic updates and receive the Hero Supporter badge.")
                .fixedSize(horizontal: false, vertical: true)
            Text("Manage your active installations in your account. Deactivate an old installation to make room for another.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if let saved = license.record {
                SavedLicenseKeyView(key: saved.key)
                    .id(saved.activationID)
                Text("Installation: Mac · \(saved.installationID.prefix(8))")
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
                }
            }
            if license.isBusy { ProgressView().controlSize(.small) }
            if let message = license.message {
                Text(message).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Link("Support for $9.99", destination: PolarConfiguration.supportURL(from: .license))
                Spacer()
                Link("Manage Subscription", destination: PolarConfiguration.portalURL)
            }
            Text("NINA LABS, LLC").font(.caption).foregroundStyle(.secondary)
            Text("Supporter activation needs an internet connection. Supporter benefits stay active for up to seven days offline after validation. Printer monitoring always remains available.")
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .lineLimit(nil)
        .padding(24)
        .frame(minWidth: 360, idealWidth: 440, maxWidth: 520)
        .fixedSize(horizontal: false, vertical: true)
        .confirmationDialog("Deactivate this installation?", isPresented: $confirmDeactivation) {
            Button("Deactivate", role: .destructive) { Task { await license.deactivate() } }
        } message: {
            Text("This frees an activation for another installation and turns off automatic updates. You can keep using Bambu Bar.")
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
        ProgressView("Opening Bambu Bar…")
            .padding(24)
            .frame(width: 360, height: 180)
            .preferredColorScheme(.dark)
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
                    NSPasteboard.general.clearContents()
                    copied = NSPasteboard.general.setString(key, forType: .string)
                }
            }
            Text(verbatim: isRevealed ? key : "•••• •••• \(key.suffix(4))")
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
