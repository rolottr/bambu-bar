import AppKit
import SwiftUI
import UserNotifications

/// Which print events post a notification, and a way to System Settings when macOS has them turned off.
struct NotificationsPage: View {
    @Environment(PrinterStore.self) private var store
    @State private var turnedOff = false

    var body: some View {
        @Bindable var store = store
        VStack(alignment: .leading, spacing: 22) {
            PageHeader(title: "Notifications", subtitle: String(localized: "Bambu Bar tells you when a print needs you, even while its window is closed."))

            if turnedOff {
                HStack(spacing: 10) {
                    Image(systemName: "bell.slash.fill")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(Palette.paused)
                    Text("Notifications for Bambu Bar are turned off in System Settings.")
                        .font(.system(size: 12, weight: .medium))
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 8)
                    Button("Open System Settings", action: openSystemSettings).controlSize(.small)
                }
                .panel(radius: 14, padding: 12, tint: Palette.paused)
            }

            SettingsGroup("Notify me when", symbol: "bell.badge.fill") {
                SettingsToggle("Print finished", isOn: $store.notifyFinished)
                SettingsToggle("Print failed, canceled or an error", isOn: $store.notifyFailed)
                SettingsToggle("Print paused (filament runout, user)", isOn: $store.notifyPaused)
                SettingsToggle("Print started", isOn: $store.notifyStarted)
            }
        }
        .task { await refresh() }
        // Back from System Settings, the banner goes once they're on.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            Task { await refresh() }
        }
    }

    private func refresh() async {
        // Notification Center needs a real app bundle; developer runs from a bare binary have none.
        guard Bundle.main.bundleURL.pathExtension == "app" else { return }
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        turnedOff = settings.authorizationStatus == .denied
    }

    private func openSystemSettings() {
        let id = Bundle.main.bundleIdentifier ?? ""
        if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=\(id)") {
            NSWorkspace.shared.open(url)
        }
    }
}
