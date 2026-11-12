using System.Text.Json;
using BambuBar.Core.Model;

namespace BambuBar.Core.App;

/// <summary>What the app hands the widgets about one printer.</summary>
public sealed record PrinterSnapshot
{
    public enum LinkState { Unconfigured, Connecting, Online, Offline, Unauthorized }

    public string Serial { get; init; } = "";
    public required string PrinterName { get; init; }
    public required string Model { get; init; }
    public required LinkState Link { get; init; }
    public PrinterStatus? Status { get; init; }
    public JobRecord? LastJob { get; init; }
    public required DateTimeOffset SavedAt { get; init; }

    public static PrinterSnapshot Unconfigured(DateTimeOffset now) =>
        new() { PrinterName = "Bambu Bar", Model = "P2S", Link = LinkState.Unconfigured, SavedAt = now };
}

/// <summary>Every printer, for the widgets.</summary>
public sealed record FleetSnapshot
{
    public required IReadOnlyList<PrinterSnapshot> Printers { get; init; }
    /// <summary>The printer the tray icon shows; widgets without a chosen printer follow it.</summary>
    public string? MenuBarSerial { get; init; }
    public required DateTimeOffset SavedAt { get; init; }

    /// <summary>The snapshot for a widget: its chosen printer, else the tray's, else the first.</summary>
    public PrinterSnapshot For(string? serial) =>
        (serial is null ? null : Printers.FirstOrDefault(p => p.Serial == serial))
        ?? (MenuBarSerial is null ? null : Printers.FirstOrDefault(p => p.Serial == MenuBarSerial))
        ?? Printers.FirstOrDefault()
        ?? PrinterSnapshot.Unconfigured(SavedAt);
}

/// <summary>The last few prints of every printer, newest first. Only what the app saw while running; it backs
/// "Last: …", finish times and end-of-print notifications, not a print log. Without a path nothing is saved.</summary>
public sealed class HistoryStore(string? path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<JobRecord> Load()
    {
        if (path is null || !File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(path), Options) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public void Save(IEnumerable<JobRecord> jobs)
    {
        if (path is null) return;
        // A handful per printer is all the UI uses.
        var kept = new List<JobRecord>();
        var perPrinter = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            var key = job.PrinterSerial ?? "";
            if (perPrinter.GetValueOrDefault(key) >= 5) continue;
            perPrinter[key] = perPrinter.GetValueOrDefault(key) + 1;
            kept.Add(job);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileWrite.Atomic(path, JsonSerializer.Serialize(kept, Options));
    }
}
