import AppIntents
import SwiftUI
import WidgetKit

@main
struct BambuBarWidgets: WidgetBundle {
    var body: some Widget {
        PrinterWidget()
    }
}

struct PrinterWidget: Widget {
    var body: some WidgetConfiguration {
        // A new kind, not ".printer": macOS caches a widget's descriptor per kind, and the first builds
        // registered ".printer" as a static (unconfigurable) widget, so new instances came without an intent.
        AppIntentConfiguration(kind: "dev.rolottr.BambuBar.printer-widget", intent: SelectPrinterIntent.self, provider: Provider()) { entry in
            PrinterWidgetView(entry: entry)
                // Drawn for its dark tile in every appearance, like the Weather and Stocks widgets.
                .environment(\.colorScheme, .dark)
                .containerBackground(for: .widget) { WidgetBackdrop() }
                .widgetURL(URL(string: "bambubar://printer/\(entry.snapshot.serial)"))
        }
        .configurationDisplayName("Printer")
        .description("Print progress, time left, temperatures and filament. Edit the widget to choose the printer.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}

// MARK: - Configuration

/// A printer set up in Bambu Bar, offered in the widget's Edit menu.
struct PrinterEntity: AppEntity {
    static var typeDisplayRepresentation: TypeDisplayRepresentation = "Printer"
    static var defaultQuery = PrinterQuery()

    var id: String
    var name: String
    var model: String

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(name)", subtitle: "\(PrinterModel.named(model)?.fullName ?? model)")
    }
}

/// The printers come from the snapshot the app writes; the widget never talks to a printer itself.
struct PrinterQuery: EntityQuery {
    func entities(for identifiers: [PrinterEntity.ID]) async throws -> [PrinterEntity] {
        all().filter { identifiers.contains($0.id) }
    }

    func suggestedEntities() async throws -> [PrinterEntity] { all() }

    private func all() -> [PrinterEntity] {
        (SharedStore.load()?.printers ?? []).map { PrinterEntity(id: $0.serial, name: $0.printerName, model: $0.model) }
    }
}

struct SelectPrinterIntent: WidgetConfigurationIntent {
    static var title: LocalizedStringResource = "Printer"
    static var description = IntentDescription("Choose which printer the widget shows.")

    /// Empty follows the printer in the menu bar.
    @Parameter(title: "Printer", description: "Leave empty to follow the menu bar printer.")
    var printer: PrinterEntity?
}

// MARK: - Timeline

struct Entry: TimelineEntry {
    var date: Date
    var snapshot: SharedSnapshot
}

/// Reads the app's snapshot. While a print runs, it lays out entries every few minutes until the
/// estimated end, so progress and time left keep moving even when the app doesn't push an update.
struct Provider: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> Entry {
        Entry(date: Date(), snapshot: .placeholder)
    }

    func snapshot(for configuration: SelectPrinterIntent, in context: Context) async -> Entry {
        // The widget gallery shows a printer mid-print, so people see what they're adding.
        if context.isPreview { return Entry(date: Date(), snapshot: .placeholder) }
        return Entry(date: Date(), snapshot: current(configuration, now: Date()))
    }

    func timeline(for configuration: SelectPrinterIntent, in context: Context) async -> Timeline<Entry> {
        let now = Date()
        let snapshot = current(configuration, now: now)
        var entries = [Entry(date: now, snapshot: snapshot)]
        if let status = snapshot.status, status.state == .printing, let end = status.estimatedEnd, end > now {
            var t = now
            let step: TimeInterval = end.timeIntervalSince(now) > 4 * 3600 ? 600 : 300
            while t < end && entries.count < 60 {
                t = t.addingTimeInterval(step)
                entries.append(Entry(date: min(t, end), snapshot: snapshot))
            }
        }
        return Timeline(entries: entries, policy: .after(now.addingTimeInterval(30 * 60)))
    }

    private func current(_ configuration: SelectPrinterIntent, now: Date) -> SharedSnapshot {
        guard let fleet = SharedStore.load() else { return .unconfigured(now) }
        var snapshot = fleet.snapshot(for: configuration.printer?.id)
        // A snapshot the app hasn't refreshed for a while means the app (or printer) is gone; don't show stale progress.
        if snapshot.link == .online, now.timeIntervalSince(snapshot.savedAt) > 30 * 60 {
            snapshot.link = .offline
        }
        return snapshot
    }
}

struct PrinterWidgetView: View {
    var entry: Entry
    @Environment(\.widgetFamily) private var family

    var body: some View {
        switch family {
        case .systemMedium: MediumWidgetView(snapshot: entry.snapshot, date: entry.date)
        case .systemLarge: LargeWidgetView(snapshot: entry.snapshot, date: entry.date)
        default: SmallWidgetView(snapshot: entry.snapshot, date: entry.date)
        }
    }
}
