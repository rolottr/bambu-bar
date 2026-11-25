import SwiftUI

struct SettingsGroup<Content: View>: View {
    let title: LocalizedStringKey
    let symbol: String
    @ViewBuilder var content: () -> Content

    init(_ title: LocalizedStringKey, symbol: String, @ViewBuilder content: @escaping () -> Content) {
        self.title = title
        self.symbol = symbol
        self.content = content
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(title, systemImage: symbol)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.primary)
            VStack(alignment: .leading, spacing: 12) { content() }
                .panel(radius: 16, padding: 16)
        }
    }
}

/// Compact "?" control beside a setting: hovering shows a tooltip, clicking opens a concise popover.
struct SettingsHelpButton: View {
    let text: LocalizedStringKey
    @State private var shown = false

    init(_ text: LocalizedStringKey) {
        self.text = text
    }

    var body: some View {
        Button { shown.toggle() } label: {
            Image(systemName: "questionmark.circle")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
        }
        .buttonStyle(.plain)
        .help(text)
        .accessibilityLabel(Text("Help"))
        .popover(isPresented: $shown, arrowEdge: .bottom) {
            Text(text)
                .font(.system(size: 12))
                .foregroundStyle(.primary)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: 280, alignment: .leading)
                .padding(12)
        }
    }
}

struct SettingsRow<Content: View>: View {
    let title: LocalizedStringKey
    let help: LocalizedStringKey?
    @ViewBuilder var content: () -> Content

    init(_ title: LocalizedStringKey, help: LocalizedStringKey? = nil, @ViewBuilder content: @escaping () -> Content) {
        self.title = title
        self.help = help
        self.content = content
    }

    var body: some View {
        HStack(spacing: 6) {
            Text(title).font(.system(size: 12.5))
            if let help { SettingsHelpButton(help) }
            Spacer(minLength: 12)
            content().font(.system(size: 12.5))
        }
    }
}

struct SettingsToggle: View {
    let title: LocalizedStringKey
    let help: LocalizedStringKey?
    let disabled: Bool
    @Binding var isOn: Bool

    init(_ title: LocalizedStringKey, help: LocalizedStringKey? = nil, disabled: Bool = false, isOn: Binding<Bool>) {
        self.title = title
        self.help = help
        self.disabled = disabled
        self._isOn = isOn
    }

    var body: some View {
        HStack(spacing: 6) {
            Text(title)
                .font(.system(size: 12.5))
                .foregroundStyle(disabled ? .secondary : .primary)
            if let help { SettingsHelpButton(help) }
            Spacer()
            Toggle(title, isOn: $isOn)
                .toggleStyle(.switch)
                .labelsHidden()
                .tint(Palette.tintDeep)
                .disabled(disabled)
        }
    }
}
