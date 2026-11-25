using System.Text;
using System.Text.Json;
using BambuBar.Core.App;
using BambuBar.Core.Model;

namespace BambuBar.Core.Tests;

/// <summary>The Activity page's log, as the Mac's ShellAndStateTests check it, plus what the store records.</summary>
public sealed class ActivityLogTests : IDisposable
{
    private readonly SerialContext _context = new("ActivityLogTests");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bambubar-activity-" + Guid.NewGuid());
    private PrinterStore? _store;

    public void Dispose()
    {
        if (_store is { } store) _context.Send(_ => store.Dispose(), null);
        _context.Dispose();
        try { Directory.Delete(_folder, true); } catch (DirectoryNotFoundException) { }
    }

    private static ActivityLog.CapabilitySummary Capability(string serialPrefix) => new()
    {
        PrinterName = "P2S Workshop", ModelName = "P2S", SerialPrefix = serialPrefix, IsOnline = true, AmsCount = 1,
        HasChamberSensor = true, HasChamberHeater = false, HasChamberLight = true, HasDualLight = false, CameraEnabled = true,
        NozzleCount = 1, HasAuxFan = true, HasChamberFan = true,
    };

    [Fact]
    public void TheRingBufferKeepsTheNewestEntries()
    {
        var log = new ActivityLog(capacity: 5);
        var changes = 0;
        log.Changed += () => changes++;
        for (var i = 1; i <= 8; i++) log.Record(ActivityLog.Category.Job, "P2S", $"Event {i}");
        Assert.Equal(5, log.Count);
        Assert.Equal(["Event 4", "Event 5", "Event 6", "Event 7", "Event 8"], log.Entries.Select(e => e.Summary));
        Assert.Equal(8, changes);

        log.Record(ActivityLog.Category.Action, "P2S", "Pause print", ActivityLog.Outcome.Applied);
        Assert.Equal(5, log.Count);
        Assert.Equal(ActivityLog.Outcome.Applied, log.Entries[^1].Outcome);

        var version = log.Version;
        log.Clear();
        Assert.Equal(0, log.Count);
        Assert.True(log.Version > version);
    }

    [Fact]
    public void ExportRedactsSecretsSerialsAndAddresses()
    {
        var log = new ActivityLog(capacity: 20);
        log.Record(ActivityLog.Category.Connection, "Workshop 22E00A123456789",
            "Failed connecting to 192.168.1.42:8883 with code 98765432 and serial 22E00A123456789 (printer.lan)", ActivityLog.Outcome.Failed);
        var json = Encoding.UTF8.GetString(log.ExportJson([Capability("22E00A123456789")], "3.1.0",
            serials: ["22E00A123456789"], hosts: ["printer.lan"], accessCodes: ["98765432"]));

        Assert.DoesNotContain("192.168.1.42", json);
        Assert.DoesNotContain("98765432", json);
        Assert.DoesNotContain("22E00A123456789", json);
        Assert.DoesNotContain("printer.lan", json);

        // The Mac's keys and values: sorted keys, ISO 8601 times, lowercase categories, a 3-character serial prefix.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(["appVersion", "capabilities", "events", "exportedAt"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("3.1.0", root.GetProperty("appVersion").GetString());
        Assert.Equal("22E", root.GetProperty("capabilities")[0].GetProperty("serialPrefix").GetString());
        var e = root.GetProperty("events")[0];
        Assert.Equal(["category", "id", "outcome", "printerName", "summary", "timestamp"], e.EnumerateObject().Select(p => p.Name));
        Assert.Equal("connection", e.GetProperty("category").GetString());
        Assert.Equal("failed", e.GetProperty("outcome").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", e.GetProperty("timestamp").GetString());
    }

    [Fact]
    public void OfflineReconnectFlappingIsCoalesced()
    {
        var log = new ActivityLog();
        for (var i = 0; i < 100; i++)
        {
            log.RecordConnectionTransition("SN1", "P2S", "connecting", "Connecting…");
            log.RecordConnectionTransition("SN1", "P2S", "offline", "Offline: Timed out");
        }
        Assert.True(log.Count <= 2);
        Assert.Equal("Offline: Timed out", log.Entries[^1].Summary);
        log.RecordConnectionTransition("SN1", "P2S", "online", "Connected");
        Assert.Equal("Connected", log.Entries[^1].Summary);
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void IssueReconnectFlappingIsCoalesced()
    {
        var log = new ActivityLog();
        for (var i = 0; i < 50; i++)
        {
            log.RecordConnectionTransition("SN1", "P2S", "connecting", "Connecting…");
            log.RecordConnectionTransition("SN1", "P2S", "issue:unauthorized", "Access code rejected");
        }
        Assert.True(log.Count <= 2);
        log.RecordConnectionTransition("SN1", "P2S", "online", "Connected");
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void Ipv6IsRedactedAndClockTimesSurvive()
    {
        var redacted = ActivityLog.Redact("Error at 12:34:56 connecting to fe80::1, 2001:db8::1, [2001:db8::1]:8883, fe80::1%en0, ::1, and 2001:0db8:85a3:0000:0000:8a2e:0370:7334 (logged 2026-11-25 23:59:59)");
        Assert.Contains("12:34:56", redacted);
        Assert.Contains("23:59:59", redacted);
        Assert.DoesNotContain("fe80::1", redacted);
        Assert.DoesNotContain("2001:db8::1", redacted);
        Assert.DoesNotContain("::1", redacted);
        Assert.DoesNotContain("2001:0db8:85a3:0000:0000:8a2e:0370:7334", redacted);
        Assert.Contains("<ip>", redacted);
    }

    [Fact]
    public void TheStoreRecordsPrintsIssuesAndCommands()
    {
        var store = _context.Invoke(() => _store = new PrinterStore(new PrinterStore.Services
        {
            Settings = new AppSettings(Path.Combine(_folder, "settings.json")),
            History = new HistoryStore(Path.Combine(_folder, "history.json")),
            Secrets = new MemorySecretStore(),
            DiscoveryPorts = [],
        }));
        _context.Send(_ => store.Connect(new PrinterConfig { Host = "127.0.0.1:1", Serial = "22E00A000000001", Name = "Workshop", ModelCode = "N7" }, "12345678"), null);
        var session = _context.Invoke(() => store.Session("22E00A000000001")!);
        _context.Send(_ =>
        {
            session.ShowStatus(new PrinterStatus { State = JobState.Printing, JobName = "Benchy", Progress = 10, Nozzle = new(220, 220) });
            session.ShowStatus(new PrinterStatus
            {
                State = JobState.Paused, JobName = "Benchy", Progress = 11, Nozzle = new(220, 220),
                Issues = [new PrinterIssue { Id = "0700_8006", Severity = IssueSeverity.Serious, Message = "Filament ran out at 127.0.0.1" }],
            });
            session.Resume();
        }, null);

        var entries = _context.Invoke(() => store.Activity.Entries.ToList());
        Assert.Contains(entries, e => e.Category == ActivityLog.Category.Job && e.Summary.EndsWith("· Benchy"));
        var issue = Assert.Single(entries, e => e.Category == ActivityLog.Category.Issue);
        Assert.Equal("0700_8006: Filament ran out at <ip>", issue.Summary);
        var action = Assert.Single(entries, e => e.Category == ActivityLog.Category.Action);
        Assert.Equal(("Resume print", ActivityLog.Outcome.Sent), (action.Summary, action.Outcome));
        Assert.All(entries, e => Assert.Equal("Workshop", e.PrinterName));

        var capability = Assert.Single(_context.Invoke(() => store.CapabilitySummaries()));
        Assert.Equal("22E", capability.SerialPrefix);
        var json = Encoding.UTF8.GetString(_context.Invoke(() => store.ExportActivityJson("3.1.0")));
        Assert.DoesNotContain("22E00A000000001", json);
        Assert.DoesNotContain("12345678", json);
    }
}
