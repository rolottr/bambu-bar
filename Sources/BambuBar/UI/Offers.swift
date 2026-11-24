import AppKit
import SwiftUI

/// Floating, dismissible cards in the main window's bottom-right corner.
struct OfferStack: View {
    var body: some View {
        VStack(alignment: .trailing, spacing: 12) {
            LoginItemOffer()
        }
        .padding(20)
    }
}

/// Shared floating card look: icon, title, short body, and a right-aligned row of buttons.
struct FloatingOffer<Buttons: View>: View {
    let symbol: String
    let title: String
    let message: String
    @ViewBuilder var buttons: Buttons

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: symbol)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 32, height: 32)
                    .background(Circle().fill(Palette.tintDeep))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text(title)
                        .font(.system(size: 13.5, weight: .semibold))
                    Text(message)
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack(spacing: 8) {
                Spacer()
                buttons
            }
            .controlSize(.regular)
        }
        .padding(16)
        .frame(width: 352)
        .background {
            let shape = RoundedRectangle(cornerRadius: 16, style: .continuous)
            if Appearance.shared.usesGlass {
                shape.fill(.regularMaterial)
                    .glassEffect(.regular, in: shape)
                    .shadow(color: .black.opacity(0.28), radius: 18, y: 8)
            } else {
                shape.fill(.regularMaterial)
                    .overlay(shape.strokeBorder(Color.primary.opacity(0.10), lineWidth: 0.75))
                    .shadow(color: .black.opacity(0.28), radius: 18, y: 8)
            }
        }
        .accessibilityElement(children: .contain)
    }
}

private struct OfferTransition: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let shown: Bool

    func body(content: Content) -> some View {
        content
            .transition(reduceMotion ? .opacity : .move(edge: .trailing).combined(with: .opacity))
            .animation(reduceMotion ? nil : .spring(response: 0.45, dampingFraction: 0.85), value: shown)
    }
}

/// One-time, dismissible offer to open Bambu Bar at login, shown 3 seconds after the first
/// printer connects successfully (never in developer/demo runs, never if already enabled,
/// never after "Not Now", and never while the Setup Guide sheet is open).
struct LoginItemOffer: View {
    @Environment(LoginItem.self) private var loginItem
    @Environment(PrinterStore.self) private var store
    @Environment(Navigation.self) private var navigation
    @State private var visible = false

    private var hasConnectedPrinter: Bool {
        store.sessions.contains(where: \.isOnline)
    }

    private var canPresent: Bool {
        hasConnectedPrinter
            && loginItem.shouldOffer
            && !store.isDemo
            && !LaunchContext.isDeveloperRun
            && !navigation.showOnboardingSplash
    }

    var body: some View {
        let shown = visible && canPresent
        ZStack {
            if shown {
                FloatingOffer(
                    symbol: "power",
                    title: String(localized: "Open Bambu Bar at login?"),
                    message: String(localized: "Starts quietly in the menu bar so your prints stay a glance away.")
                ) {
                    Button("Not Now") {
                        visible = false
                        loginItem.answerOffer()
                    }
                    .fixedSize()
                    Button("Open at Login") { accept() }
                        .buttonStyle(.borderedProminent)
                        .tint(Palette.tintDeep)
                        .fixedSize()
                }
            }
        }
        .modifier(OfferTransition(shown: shown))
        .task(id: canPresent) {
            guard canPresent, !visible else { return }
            try? await Task.sleep(for: .seconds(3))
            guard !Task.isCancelled, canPresent else { return }
            visible = true
        }
    }

    private func accept() {
        visible = false
        loginItem.setEnabled(true)
        loginItem.answerOffer()
        switch loginItem.status {
        case .enabled:
            store.showToast(String(localized: "Opens at login"))
        case .requiresApproval:
            store.showToast(String(localized: "Allow Bambu Bar in System Settings to finish turning on Open at Login."), error: true)
            loginItem.openSystemSettings()
        case .disabled:
            store.showToast(String(localized: "Couldn’t turn on Open at Login. Add Bambu Bar in System Settings → General → Login Items."), error: true)
            loginItem.openSystemSettings()
        }
    }
}
