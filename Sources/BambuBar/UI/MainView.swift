import SwiftUI

struct MainView: View {
    var body: some View {
        MainContentView()
            .frame(minWidth: MainWindow.minimumContentSize.width,
                   minHeight: MainWindow.minimumContentSize.height)
    }
}

private struct MainContentView: View {
    @Environment(PrinterStore.self) private var store
    @Environment(Navigation.self) private var navigation

    var body: some View {
        @Bindable var navigation = navigation
        NavigationSplitView {
            List(selection: $navigation.section) {
                ForEach(AppSection.allCases.filter { $0 != .fleet || store.hasMultiplePrinters }) { row($0) }
            }
            .navigationSplitViewColumnWidth(min: 190, ideal: 210, max: 260)
            .safeAreaInset(edge: .top) {
                if store.hasMultiplePrinters {
                    SidebarPrinterPicker()
                        .padding(.horizontal, 12)
                        .padding(.bottom, 6)
                }
            }
            .safeAreaInset(edge: .bottom) {
                if !store.hasMultiplePrinters { sidebarFooter }
            }
        } detail: {
            GeometryReader { geometry in
                ScrollView {
                    page
                        .frame(maxWidth: 860)
                        .padding(.horizontal, 28)
                        .padding(.vertical, 24)
                        .frame(maxWidth: .infinity)
                }
                // Keep the initial heading below the window controls, but allow scrolled
                // content to pass beneath the floating supporter link in the title bar.
                .safeAreaPadding(.top, geometry.safeAreaInsets.top)
                .ignoresSafeArea(.container, edges: .top)
                .scrollContentBackground(.hidden)
                .scrollEdgeEffectHidden(true, for: .top)
            }
            .background(PageBackground(status: store.status))
            .navigationTitle(navigation.section.title)
            .toolbar(removing: .title)
            .toolbarBackground(.hidden, for: .windowToolbar)
        }
        #if !APP_STORE
        .overlay(alignment: .topTrailing) {
            supporterControl
                .padding(.top, 16)
                .padding(.trailing, 24)
                .ignoresSafeArea(.container, edges: .top)
        }
        #endif
    }

    #if !APP_STORE
    /// Floats over the window's title-bar area without reserving space in the scrolling page.
    private var supporterControl: some View {
        Group {
            if LicenseStore.shared.hasAccess {
                Button { MainWindow.show(.about) } label: {
                    Label("Hero Supporter", systemImage: "star.circle.fill")
                }
                .foregroundStyle(Palette.accent)
                .help("Thanks for supporting Bambu Bar")
            } else {
                Link(destination: PolarConfiguration.supportURL(from: .mainWindow)) {
                    Label("Support this project", systemImage: "heart")
                }
                .foregroundStyle(Palette.accent)
            }
        }
        .buttonStyle(.plain)
        .labelStyle(.titleAndIcon)
        .font(.system(size: 12, weight: .semibold))
        .fixedSize()
    }
    #endif

    private func row(_ section: AppSection) -> some View {
        Label(section.title, systemImage: section.icon).tag(section)
    }

    @ViewBuilder
    private var page: some View {
        switch navigation.section {
        case .fleet:
            if store.hasMultiplePrinters && !navigation.addingPrinter { FleetPage() } else if store.isConfigured { OverviewPage() } else { SetupPage() }
        case .overview:
            if store.isConfigured && !navigation.addingPrinter { OverviewPage() } else { SetupPage() }
        case .filament: FilamentPage()
        case .widgets: WidgetsPage()
        case .menuBar: MenuBarPage()
        case .notifications: NotificationsPage()
        case .printers: PrintersPage()
        case .settings: SettingsPage()
        case .about: AboutPage()
        }
    }

    /// Printer name and live state, always visible under the sidebar.
    private var sidebarFooter: some View {
        HStack(spacing: 10) {
            PrinterArt(status: store.status, model: store.config?.modelName, dimmed: !store.isOnline, animated: false)
                .frame(width: 26, height: 31)
            VStack(alignment: .leading, spacing: 1) {
                Text(store.isConfigured ? store.printerName : String(localized: "No printer"))
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(1)
                Text(footerLine)
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
    }

    private var footerLine: String {
        guard store.isConfigured else { return String(localized: "Set up to begin") }
        guard let s = store.status, store.isOnline else { return store.linkSummary }
        if s.state.isActive { return "\(s.state.title) · \(s.progress)%" }
        return s.state.title
    }
}

/// A faint wash in the colour of the current state behind every page.
struct PageBackground: View {
    var status: PrinterStatus?

    var body: some View {
        let color = status.map { Palette.color(for: $0.state) } ?? .clear
        ZStack {
            Color(nsColor: .windowBackgroundColor)
            RadialGradient(colors: [color.opacity(0.10), .clear], center: .topLeading, startRadius: 0, endRadius: 700)
            RadialGradient(colors: [Palette.accent.opacity(0.05), .clear], center: .bottomTrailing, startRadius: 0, endRadius: 600)
        }
        .ignoresSafeArea()
        .animation(.smooth(duration: 0.6), value: status?.state)
    }
}

/// Page heading used by every section.
struct PageHeader<Trailing: View>: View {
    var title: LocalizedStringKey
    var subtitle: String?
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .bottom) {
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.system(size: 26, weight: .bold))
                if let subtitle {
                    Text(subtitle).font(.system(size: 13)).foregroundStyle(.secondary)
                }
            }
            Spacer()
            trailing
        }
        .padding(.bottom, 4)
    }
}

extension PageHeader where Trailing == EmptyView {
    init(title: LocalizedStringKey, subtitle: String? = nil) {
        self.init(title: title, subtitle: subtitle) { EmptyView() }
    }
}
