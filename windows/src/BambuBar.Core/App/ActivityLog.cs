using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BambuBar.Core.App;

/// <summary>
/// The Activity page's record: a bounded ring buffer of connection, print, issue, camera and command events, newest
/// last. A port of the Mac's ActivityLog (SingleInstance.swift), with the same categories, reconnect-noise suppression,
/// redaction and JSON export. Lives on the UI thread. Recording is a list append; <see cref="Changed"/> tells a visible
/// page to redraw, and a hidden page ignores it.
/// </summary>
public sealed partial class ActivityLog
{
    public enum Category { Connection, Job, Issue, Camera, Action }

    public enum Outcome { Applied, Sent, Failed }

    public sealed record Entry(Guid Id, DateTimeOffset Timestamp, Category Category, string PrinterName, string Summary, Outcome? Outcome);

    /// <summary>What one printer reports it has, for the page's first card and the export.</summary>
    public sealed record CapabilitySummary
    {
        public required string PrinterName { get; init; }
        public required string ModelName { get; init; }
        public required string SerialPrefix { get; init; }
        public required bool IsOnline { get; init; }
        public required int AmsCount { get; init; }
        public required bool HasChamberSensor { get; init; }
        public required bool HasChamberHeater { get; init; }
        public required bool HasChamberLight { get; init; }
        public required bool HasDualLight { get; init; }
        public required bool CameraEnabled { get; init; }
        public required int NozzleCount { get; init; }
        public required bool HasAuxFan { get; init; }
        public required bool HasChamberFan { get; init; }
    }

    public static IReadOnlyList<Category> Categories { get; } = Enum.GetValues<Category>();

    /// <summary>The filter chip's title, as on the Mac.</summary>
    public static string Title(Category category) => category switch
    {
        Category.Connection => Loc.T("Connection", "Activity filter"),
        Category.Job => Loc.T("Print", "Activity filter"),
        Category.Issue => Loc.T("Issues", "Activity filter"),
        Category.Camera => Loc.T("Camera", "Activity filter"),
        _ => Loc.T("Actions", "Activity filter"),
    };

    public static string Title(Outcome outcome) => outcome switch
    {
        Outcome.Applied => Loc.T("Applied", "Activity outcome"),
        Outcome.Sent => Loc.T("Sent", "Activity outcome"),
        _ => Loc.T("Failed", "Activity outcome"),
    };

    /// <summary>The lowercase name the Mac's export and the native pages use ("connection", "job"…).</summary>
    public static string Key(Category category) => category.ToString().ToLowerInvariant();
    public static string Key(Outcome outcome) => outcome.ToString().ToLowerInvariant();

    public static Category? ParseCategory(string? key) =>
        Categories.Cast<Category?>().FirstOrDefault(c => Key(c!.Value) == key);

    public int Capacity { get; }
    private readonly List<Entry> _buffer = [];
    /// <summary>The last logged connection state per printer, so offline ↔ connecting retries don't flood the log.</summary>
    private readonly Dictionary<string, string> _connectionStates = [];

    public ActivityLog(int capacity = 500) => Capacity = Math.Max(1, capacity);

    /// <summary>An entry was added or the log was cleared.</summary>
    public event Action? Changed;

    /// <summary>Goes up with every change, so a page can skip redrawing an unchanged list.</summary>
    public long Version { get; private set; }

    public int Count => _buffer.Count;

    /// <summary>A copy of every entry, newest last.</summary>
    public IReadOnlyList<Entry> Entries => _buffer.ToArray();

    /// <summary>
    /// Records a connection change for <paramref name="printerId"/>. <paramref name="state"/> is "idle", "connecting",
    /// "online", "offline" or "issue:…". Reconnect attempts from offline or from a persistent issue are left out; the
    /// first failure and the recovery are kept.
    /// </summary>
    public void RecordConnectionTransition(string printerId, string printerName, string state, string summary, DateTimeOffset? timestamp = null)
    {
        _connectionStates.TryGetValue(printerId, out var previous);
        if (previous == state) return;
        if (state == "connecting" && (previous == "offline" || previous?.StartsWith("issue:", StringComparison.Ordinal) == true)) return;
        _connectionStates[printerId] = state;
        Record(Category.Connection, printerName, summary, null, timestamp);
    }

    public void Record(Category category, string printerName, string summary, Outcome? outcome = null, DateTimeOffset? timestamp = null)
    {
        _buffer.Add(new Entry(Guid.NewGuid(), timestamp ?? DateTimeOffset.Now, category, printerName, summary, outcome));
        if (_buffer.Count > Capacity) _buffer.RemoveRange(0, _buffer.Count - Capacity);
        Version++;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_buffer.Count == 0) return;
        _buffer.Clear();
        Version++;
        Changed?.Invoke();
    }

    // MARK: Redaction

    [GeneratedRegex(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?::[0-9]{1,5})?\b")]
    private static partial Regex Ipv4();

    private const string H = "[0-9A-Fa-f]{1,4}";
    private const string Ipv6Core =
        "(?:" + H + ":){7}" + H + "|::(?:[Ff]{4}:)?(?:[0-9]{1,3}\\.){3}[0-9]{1,3}|(?:" + H + ":){1,6}:" + H +
        "|(?:" + H + ":){1,5}(?::" + H + "){1,2}|(?:" + H + ":){1,4}(?::" + H + "){1,3}|(?:" + H + ":){1,3}(?::" + H + "){1,4}" +
        "|(?:" + H + ":){1,2}(?::" + H + "){1,5}|" + H + ":(?::" + H + "){1,6}|:(?::" + H + "){1,7}|(?:" + H + ":){1,7}:";
    private const string Zone = "(?:%[0-9A-Za-z_.-]+)?";

    [GeneratedRegex("(?<![0-9A-Za-z_:])(?:\\[(?:" + Ipv6Core + ")" + Zone + "\\](?::[0-9]{1,5})?|(?:" + Ipv6Core + ")" + Zone + ")(?![0-9A-Za-z_:])")]
    private static partial Regex Ipv6();

    /// <summary>Bambu's 15-character serials (22E00A123456789).</summary>
    [GeneratedRegex(@"\b[0-9A-Z]{3}[0-9A-Z]{12}\b")]
    private static partial Regex Serial();

    /// <summary>Replaces IPv4/IPv6 addresses, the given hosts, full serial numbers and access codes with placeholders.</summary>
    public static string Redact(string text, IEnumerable<string>? serials = null, IEnumerable<string>? hosts = null, IEnumerable<string>? accessCodes = null)
    {
        var output = text;
        foreach (var code in accessCodes ?? []) if (code.Length > 0) output = output.Replace(code, "<redacted>", StringComparison.OrdinalIgnoreCase);
        foreach (var serial in serials ?? []) if (serial.Length >= 6) output = output.Replace(serial, "<serial>", StringComparison.OrdinalIgnoreCase);
        foreach (var host in hosts ?? []) if (host.Length > 0) output = output.Replace(host, "<host>", StringComparison.OrdinalIgnoreCase);
        // IPv6 first (IPv4-mapped too), keeping HH:MM:SS times intact.
        output = Ipv6().Replace(output, "<ip>");
        output = Ipv4().Replace(output, "<ip>");
        return Serial().Replace(output, "<serial>");
    }

    // MARK: Export

    /// <summary>The Mac's export, key for key: pretty-printed, sorted keys, ISO 8601 times, everything redacted.</summary>
    public byte[] ExportJson(IEnumerable<CapabilitySummary> capabilities, string appVersion, IReadOnlyCollection<string>? serials = null,
        IReadOnlyCollection<string>? hosts = null, IReadOnlyCollection<string>? accessCodes = null, DateTimeOffset? exportedAt = null)
    {
        string Clean(string s) => Redact(s, serials, hosts, accessCodes);
        static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("appVersion", appVersion);
            json.WriteStartArray("capabilities");
            foreach (var cap in capabilities)
            {
                json.WriteStartObject();
                json.WriteNumber("amsCount", cap.AmsCount);
                json.WriteBoolean("cameraEnabled", cap.CameraEnabled);
                json.WriteBoolean("hasAuxFan", cap.HasAuxFan);
                json.WriteBoolean("hasChamberFan", cap.HasChamberFan);
                json.WriteBoolean("hasChamberHeater", cap.HasChamberHeater);
                json.WriteBoolean("hasChamberLight", cap.HasChamberLight);
                json.WriteBoolean("hasChamberSensor", cap.HasChamberSensor);
                json.WriteBoolean("hasDualLight", cap.HasDualLight);
                json.WriteBoolean("isOnline", cap.IsOnline);
                json.WriteString("modelName", cap.ModelName);
                json.WriteNumber("nozzleCount", cap.NozzleCount);
                json.WriteString("printerName", Clean(cap.PrinterName));
                json.WriteString("serialPrefix", cap.SerialPrefix[..Math.Min(3, cap.SerialPrefix.Length)].ToUpperInvariant());
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("events");
            foreach (var entry in _buffer)
            {
                json.WriteStartObject();
                json.WriteString("category", Key(entry.Category));
                json.WriteString("id", entry.Id.ToString().ToUpperInvariant());
                if (entry.Outcome is { } outcome) json.WriteString("outcome", Key(outcome));
                json.WriteString("printerName", Clean(entry.PrinterName));
                json.WriteString("summary", Clean(entry.Summary));
                json.WriteString("timestamp", Time(entry.Timestamp));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteString("exportedAt", Time(exportedAt ?? DateTimeOffset.Now));
            json.WriteEndObject();
        }
        return stream.ToArray();
    }
}
