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

struct SettingsRow<Content: View>: View {
    let title: LocalizedStringKey
    @ViewBuilder var content: () -> Content

    init(_ title: LocalizedStringKey, @ViewBuilder content: @escaping () -> Content) {
        self.title = title
        self.content = content
    }

    var body: some View {
        HStack {
            Text(title).font(.system(size: 12.5))
            Spacer(minLength: 12)
            content().font(.system(size: 12.5))
        }
    }
}

struct SettingsToggle: View {
    let title: LocalizedStringKey
    @Binding var isOn: Bool

    init(_ title: LocalizedStringKey, isOn: Binding<Bool>) {
        self.title = title
        self._isOn = isOn
    }

    var body: some View {
        HStack {
            Text(title).font(.system(size: 12.5))
            Spacer()
            Toggle("", isOn: $isOn).toggleStyle(.switch).labelsHidden().tint(Palette.tintDeep)
        }
    }
}
