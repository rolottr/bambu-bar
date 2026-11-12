using System.Collections.Concurrent;
using BambuBar.Core.App;
using BambuBar.Core.Model;

namespace BambuBar.Core.Tests;

/// <summary>The store's rules: job history, notifications, the tray's printer, dismissed issues, settings and the
/// widgets' snapshot. Statuses are scripted; the printers are "configured" at a closed port, so nothing connects.</summary>
public sealed class StoreTests : IDisposable
{
    private sealed class Recorder : INotifier, IWidgetSink
    {
        public readonly ConcurrentQueue<(string Title, string Body, string Id)> Posts = new();
        public readonly ConcurrentQueue<(FleetSnapshot Fleet, bool Reload)> Writes = new();
        public void Post(string title, string body, string id) => Posts.Enqueue((title, body, id));
        public void RequestAuthorization() { }
        public void Write(FleetSnapshot fleet, bool reload) => Writes.Enqueue((fleet, reload));
    }

    private readonly SerialContext _context = new("StoreTests");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bambubar-tests-" + Guid.NewGuid());
    private readonly Recorder _recorder = new();
    private PrinterStore? _store;

    public void Dispose()
    {
        if (_store is { } store) _context.Send(_ => store.Dispose(), null);
        _context.Dispose();
        try { Directory.Delete(_folder, true); } catch (DirectoryNotFoundException) { }
    }

    private PrinterStore Store(params string[] args) => _context.Invoke(() =>
    {
        _store = new PrinterStore(new PrinterStore.Services
        {
            Settings = new AppSettings(Path.Combine(_folder, "settings.json")),
            History = new HistoryStore(Path.Combine(_folder, "history.json")),
            Secrets = new MemorySecretStore(),
            Notifier = _recorder,
            Widgets = _recorder,
            Arguments = args,
            DiscoveryPorts = [],
        });
        return _store;
    });

    private T On<T>(Func<T> f) => _context.Invoke(f);
    private void On(Action a) => _context.Send(_ => a(), null);

    private static PrinterConfig Printer(string serial, string name) =>
        // Port 1 on loopback: refused at once, so the session sits offline and only scripted statuses arrive.
        new() { Host = "127.0.0.1:1", Serial = serial, Name = name, ModelCode = "N7" };

    private static PrinterStatus Printing(int progress, string job = "Benchy", string? task = "42") => new()
    {
        State = JobState.Printing, StageId = 0, JobName = job, Progress = progress, RemainingMinutes = 100 - progress,
        Layer = progress, TotalLayers = 100, TaskId = task, Nozzle = new(220, 220),
    };

    [Fact]
    public void AJobIsRecordedAndItsEndNotified()
    {
        var store = Store();
        On(() => store.Connect(Printer("22E00A000000001", "Workshop"), "12345678"));
        var session = On(() => store.Session("22E00A000000001")!);
        On(() => session.ShowStatus(Printing(10)));   // first report: baseline, no "started"
        On(() => session.ShowStatus(Printing(50)));
        On(() => session.ShowStatus(new PrinterStatus { State = JobState.Finished, JobName = "Benchy", Progress = 100, TaskId = "42" }));

        var job = Assert.Single(On(() => store.History.ToList()));
        Assert.Equal(JobOutcome.Finished, job.Outcome);
        Assert.Equal("Benchy", job.Name);
        Assert.NotNull(job.EndedAt);
        var post = Assert.Single(_recorder.Posts);
        Assert.Equal("Print finished", post.Title);
        Assert.Equal("end.22E00A000000001", post.Id);
        Assert.StartsWith("Benchy finished in ", post.Body);
        // Saved to disk, and read back by the next run.
        Assert.Single(new HistoryStore(Path.Combine(_folder, "history.json")).Load());
    }

    [Fact]
    public void ANewPrintIsAnnouncedWhenAsked()
    {
        var store = Store();
        On(() => { store.NotifyStarted = true; store.Connect(Printer("22E00A000000001", "Workshop"), "12345678"); });
        var session = On(() => store.Session("22E00A000000001")!);
        On(() => session.ShowStatus(new PrinterStatus { State = JobState.Idle }));
        On(() => session.ShowStatus(Printing(1, "Vase", "7")));
        Assert.Contains(_recorder.Posts, p => p.Title == "Print started" && p.Body == "Vase");
    }

    [Fact]
    public void ACanceledJobIsCanceledNotFailed()
    {
        var store = Store();
        On(() => store.Connect(Printer("22E00A000000001", "Workshop"), "12345678"));
        var session = On(() => store.Session("22E00A000000001")!);
        On(() => session.ShowStatus(Printing(30)));
        // print_error 0300_400C: the printer's echo of a cancel.
        var failed = new PrinterStatus { State = JobState.Failed, JobName = "Benchy", Canceled = true };
        On(() => session.ShowStatus(failed, canceled: true));
        Assert.Equal(JobOutcome.Canceled, Assert.Single(On(() => store.History.ToList())).Outcome);
        Assert.Contains(_recorder.Posts, p => p.Title == "Print canceled");
    }

    [Fact]
    public void PausesAndNewSeriousIssuesAreNotified()
    {
        var store = Store();
        On(() => store.Connect(Printer("22E00A000000001", "Workshop"), "12345678"));
        var session = On(() => store.Session("22E00A000000001")!);
        On(() => session.ShowStatus(Printing(30)));
        var paused = Printing(30);
        paused.State = JobState.Paused;
        paused.StageId = 6;
        paused.Issues = [new PrinterIssue { Id = "HMS_0700_2300_0002_0001", Severity = IssueSeverity.Serious, Message = "Out of filament" }];
        On(() => session.ShowStatus(paused));
        Assert.Contains(_recorder.Posts, p => p.Title == "Print paused" && p.Body == "Benchy: Paused: filament ran out");
        Assert.Contains(_recorder.Posts, p => p.Title == "Workshop: Warning" && p.Body == "Out of filament");
    }

    [Fact]
    public void DismissedIssuesStayHidden()
    {
        var store = Store();
        On(() => store.Connect(Printer("22E00A000000001", "Workshop"), "12345678"));
        var session = On(() => store.Session("22E00A000000001")!);
        var issue = new PrinterIssue { Id = "0300_4006", Severity = IssueSeverity.Fatal, Message = "Clogged" };
        var report = Printing(30);
        report.Issues = [issue];
        On(() => session.ShowStatus(report));
        On(() => session.Dismiss(issue));
        Assert.Empty(On(() => session.Status!.Issues));
        On(() => session.ShowStatus(report));
        Assert.Empty(On(() => session.Status!.Issues));
    }

    [Fact]
    public void TheTrayFollowsTheBusiestPrinter()
    {
        var store = Store();
        On(() =>
        {
            store.Connect(Printer("22E00A000000001", "Idle one"), "1");
            store.Connect(Printer("22E00A000000002", "Busy one"), "2");
        });
        // Nothing busy: the selected printer (the last one connected).
        Assert.Equal("22E00A000000002", On(() => store.MenuBarSession?.Id));
        On(() => store.Select("22E00A000000001"));
        Assert.Equal("22E00A000000001", On(() => store.MenuBarSession?.Id));
        // Offline sessions don't count as busy, however they report; chosen printers always win.
        On(() => store.MenuBarSerial = "22E00A000000002");
        Assert.Equal("22E00A000000002", On(() => store.MenuBarSession?.Id));
    }

    [Fact]
    public void SettingsAreSavedUnderTheMacsKeys()
    {
        var store = Store();
        Assert.True(On(() => store.NotifyFinished));
        Assert.False(On(() => store.NotifyStarted));
        Assert.Equal(TrayStyle.Both, On(() => store.MenuBarStyle));
        On(() => { store.MenuBarStyle = TrayStyle.Percent; store.NotifyStarted = true; store.Connect(Printer("22e00a000000001", "Lower"), "1"); });
        var saved = new AppSettings(Path.Combine(_folder, "settings.json"));
        Assert.Equal("percent", saved.Get<string>("menuBarStyle"));
        Assert.True(saved.Get("notifyStarted", false));
        Assert.Equal("22e00a000000001", Assert.Single(saved.Get<List<PrinterConfig>>("printers")!).Serial);
    }

    [Fact]
    public void RenamingAndForgetting()
    {
        var store = Store();
        On(() => store.Connect(Printer("22E00A000000001", "Workshop"), "1"));
        On(() => store.Rename("22E00A000000001", "  "));
        Assert.Equal("22E00A000000001", On(() => store.Session("22E00A000000001")!.Config.Name));
        On(() => store.Forget("22E00A000000001"));
        Assert.False(On(() => store.IsConfigured));
        Assert.Empty(new AppSettings(Path.Combine(_folder, "settings.json")).Get<List<PrinterConfig>>("printers")!);
    }

    [Fact]
    public void WidgetsGetAReloadWhenTheStateChanges()
    {
        var store = Store();
        On(() => store.Connect(Printer("22E00A000000001", "Workshop"), "1"));
        var session = On(() => store.Session("22E00A000000001")!);
        _recorder.Writes.Clear();
        On(() => session.ShowStatus(Printing(10)));
        Assert.Contains(_recorder.Writes, w => w.Reload);
        _recorder.Writes.Clear();
        // One percent more is the same five-percent bucket: no redraw needed.
        On(() => session.ShowStatus(Printing(11)));
        Assert.DoesNotContain(_recorder.Writes, w => w.Reload);
        var last = _recorder.Writes.LastOrDefault().Fleet ?? On(() => store.Fleet);
        Assert.Equal("Workshop", Assert.Single(last.Printers).PrinterName);
    }

    [Fact]
    public void DemoFleetRunsWithoutSavingAnything()
    {
        var store = Store("--demo", "printing", "--fleet");
        On(store.Start);
        Assert.True(On(() => store.IsDemo));
        Assert.Equal(6, On(() => store.Sessions.Count));
        Assert.All(On(() => store.Sessions.ToList()), s => Assert.True(s.IsDemo));
        Assert.Equal("Rear Dry Pod", On(() => store.Current!.Status!.JobName));
        Assert.Equal("P2S Workshop", On(() => store.Current!.Config.Name));
        Assert.Empty(_recorder.Writes);
        Assert.False(File.Exists(Path.Combine(_folder, "settings.json")));
        Assert.NotEmpty(On(() => store.History.ToList()));
    }

    [Fact]
    public void HistoryKeepsFivePerPrinter()
    {
        var path = Path.Combine(_folder, "h.json");
        var history = new HistoryStore(path);
        var jobs = Enumerable.Range(0, 8).Select(i => new JobRecord
        {
            Name = $"Job {i}", StartedAt = DateTimeOffset.Now, Outcome = JobOutcome.Finished, PrinterSerial = i % 2 == 0 ? "A" : "B",
        });
        history.Save(jobs);
        var loaded = history.Load();
        Assert.Equal(8, loaded.Count);
        history.Save([.. loaded, .. loaded]);
        Assert.Equal(10, history.Load().Count);
    }

    [Theory]
    [InlineData(TrayStyle.Both, JobState.Printing, 64, 62, "64% · 1h 02m")]
    [InlineData(TrayStyle.Both, JobState.Paused, 64, 62, "64% · Paused")]
    [InlineData(TrayStyle.Both, JobState.Preparing, 0, 160, "Preparing")]
    [InlineData(TrayStyle.Remaining, JobState.Printing, 64, 0, "64%")]
    [InlineData(TrayStyle.Percent, JobState.Printing, 64, 62, "64%")]
    [InlineData(TrayStyle.Icon, JobState.Printing, 64, 62, null)]
    public void TrayText(TrayStyle style, JobState state, int progress, int minutes, string? expected) =>
        Assert.Equal(expected, style.Text(new PrinterStatus { State = state, Progress = progress, RemainingMinutes = minutes }));

    [Theory]
    [InlineData(0, "<1m")]
    [InlineData(45, "45m")]
    [InlineData(125, "2h 05m")]
    [InlineData(1500, "1d 1h")]
    public void Durations(int minutes, string expected) => Assert.Equal(expected, Format.Duration(minutes));

    [Fact]
    public void NaturalOrder() =>
        Assert.Equal(["P2S 2", "p2s 10", "X1C"], new[] { "X1C", "p2s 10", "P2S 2" }.Order(NaturalComparer.Instance));
}

public sealed class DemoConnectionScenes : IDisposable
{
    private readonly SerialContext _context = new("DemoConnectionScenes");
    public void Dispose() => _context.Dispose();

    [Theory]
    [InlineData("connecting", typeof(BambuBar.Core.Protocol.PrinterConnection.Phase.Connecting))]
    [InlineData("unauthorized", typeof(BambuBar.Core.Protocol.PrinterConnection.Phase.Unauthorized))]
    [InlineData("certificate", typeof(BambuBar.Core.Protocol.PrinterConnection.Phase.Certificate))]
    public void ConnectionCardsHaveDemoScenes(string scene, Type phase)
    {
        var store = _context.Invoke(() => new PrinterStore(new PrinterStore.Services
        {
            Settings = new AppSettings(null), History = new HistoryStore(null), Secrets = new MemorySecretStore(),
            Arguments = ["--demo", scene], DiscoveryPorts = [],
        }));
        _context.Send(_ => store.Start(), null);
        Assert.IsType(phase, _context.Invoke(() => store.Current!.Phase));
        Assert.Null(_context.Invoke(() => store.Current!.Status));
        _context.Send(_ => store.Dispose(), null);
    }
}
