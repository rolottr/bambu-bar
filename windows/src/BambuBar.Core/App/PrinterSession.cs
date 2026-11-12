using BambuBar.Core.Camera;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.App;

/// <summary>
/// One configured printer: its connection, live status and the commands in flight. <see cref="PrinterStore"/> owns
/// the sessions and handles what spans printers (history, notifications, widgets). Lives on the UI thread. A port of
/// the Mac's PrinterSession.swift.
/// </summary>
public sealed class PrinterSession : Observable, IDisposable
{
    /// <summary>The serial number, which never changes for a session.</summary>
    public string Id { get; }

    private PrinterConfig _config;
    public PrinterConfig Config
    {
        get => _config;
        set { if (Set(ref _config, value)) Changed(nameof(ShowsCamera)); }
    }

    private PrinterConnection.Phase _phase = new PrinterConnection.Phase.Idle();
    public PrinterConnection.Phase Phase { get => _phase; private set { if (Set(ref _phase, value)) DerivedChanged(); } }

    private PrinterStatus? _status;
    public PrinterStatus? Status { get => _status; private set { _status = value; Changed(); DerivedChanged(); } }

    /// <summary>Commands sent but not yet reflected in a report, so the controls respond immediately.</summary>
    private bool? _pendingLight;
    private SpeedLevel? _pendingSpeed;

    /// <summary>More codes to try if the printer rejects the current one (a slicer may hold an old and a new one).</summary>
    public List<string> FallbackCodes { get; set; } = [];

    /// <summary>False until the first report after connecting, so state already in progress doesn't notify.</summary>
    public bool HasBaseline { get; set; }

    private DateTimeOffset? _lastHeard;
    /// <summary>When the printer last sent anything, to the second ("Updated 3s ago").</summary>
    public DateTimeOffset? LastHeard { get => _lastHeard; private set => Set(ref _lastHeard, value); }

    public Action<PrinterSession, PrinterConnection.Phase>? OnPhase { get; set; }
    public Action<PrinterSession, PrinterStatus?, PrinterStatus, bool>? OnStatus { get; set; }
    public Action<PrinterSession, string, bool, string?>? OnAck { get; set; }
    public Action<PrinterSession>? OnHeartbeat { get; set; }

    private readonly PrinterStore _store;
    private readonly PrinterConnection _connection = new();
    private readonly SynchronizationContext _context;
    private DemoDriver? _demo;
    private bool _insecure;
    private Timer? _pendingTimer;
    /// <summary>The code the connection is using (with <c>--connect</c> it never reaches Credential Manager).</summary>
    private string? _activeCode;

    internal PrinterSession(PrinterConfig config, PrinterStore store)
    {
        Id = config.Serial;
        _config = config;
        _store = store;
        _context = _connection.Context;
        _connection.OnPhase = PhaseChanged;
        _connection.OnStatus = (status, doc) => Received(status, ReportParser.WasCanceled(doc));
        _connection.OnAck = (command, ok, reason) =>
        {
            if (!ok) { _pendingLight = null; _pendingSpeed = null; DerivedChanged(); }
            OnAck?.Invoke(this, command, ok, reason);
        };
        _connection.OnHeartbeat = () =>
        {
            // Whole seconds are enough for the label and spare the UI an update per message.
            if (LastHeard is not { } heard || DateTimeOffset.Now - heard >= TimeSpan.FromSeconds(1)) LastHeard = DateTimeOffset.Now;
            OnHeartbeat?.Invoke(this);
        };
        Camera = new CameraFeed(CameraSituation, store.CameraServices);
    }

    /// <summary>The printer's camera; streams only while a view shows it (or a print runs, if Settings say so).</summary>
    public CameraFeed Camera { get; }

    // MARK: Lifecycle

    public void Start(string accessCode, IEnumerable<string>? alternatives = null, bool insecure = false)
    {
        _insecure = insecure;
        FallbackCodes = (alternatives ?? []).Where(c => c != accessCode).ToList();
        Status = null;
        HasBaseline = false;
        _activeCode = accessCode;
        _connection.Start(Config, accessCode, insecure);
    }

    /// <summary>Runs a simulated printer instead of a connection (<c>--demo</c>).</summary>
    public void StartDemo(DemoDriver.Scenario scenario, int variant, bool animate = true)
    {
        var driver = new DemoDriver(scenario, variant)
        {
            OnStatus = (status, canceled) => Received(status, canceled),
            OnPhase = PhaseChanged,
        };
        _demo = driver;
        driver.Start(animate);
    }

    public void Stop()
    {
        Camera.Stop();
        _demo?.Dispose();
        _connection.Stop();
        Phase = new PrinterConnection.Phase.Idle();
    }

    public void Dispose()
    {
        Stop();
        _pendingTimer?.Dispose();
        _connection.Dispose();
    }

    public void Refresh() => _connection.Refresh();

    /// <summary>Reconnects with the stored code, e.g. after the printer moved to a new IP address.</summary>
    public void Restart()
    {
        if (_demo is not null || _store.Secrets.AccessCode(Config.Serial) is not { } code) return;
        _activeCode = code;
        _connection.Start(Config, code, _insecure);
        Camera.Restart();
    }

    /// <summary>The computer sleeps or wakes (Windows' PowerModeChanged).</summary>
    public void OnSleep() => _connection.OnSleep();
    public void OnWake() => _connection.OnWake();

    // MARK: Derived

    /// <summary>A live status is on hand: connected, or briefly reconnecting after a drop.</summary>
    public bool IsOnline => Status is not null && Phase is PrinterConnection.Phase.Online or PrinterConnection.Phase.Connecting;
    public bool ChamberLightOn => _pendingLight ?? Status?.ChamberLightOn ?? false;
    public SpeedLevel Speed => _pendingSpeed ?? Status?.Speed ?? SpeedLevel.Standard;
    public string? AccessCode => _store.Secrets.AccessCode(Config.Serial);
    public bool IsDemo => _demo is not null;
    public bool AllowsInsecureTls => _insecure;
    public string? CameraAccessCode => _activeCode ?? AccessCode;
    /// <summary>The camera appears only with both switches on: Settings → Camera, and this printer's on the Printers page.</summary>
    public bool ShowsCamera => !IsDemo && _store.CameraEnabled && Config.CameraEnabled == true;
    /// <summary>RTSPS or the JPEG stream, by model.</summary>
    public CameraTransport CameraTransport => CameraTransports.ForModel(Config.ModelName);

    /// <summary>What the camera feed needs to know, as of now.</summary>
    private CameraSituation CameraSituation() => new()
    {
        IsDemo = IsDemo, ShowsCamera = ShowsCamera, IsMockFleet = _store.IsMockFleet, IsOnline = IsOnline,
        Transport = CameraTransport, LiveViewEnabled = Status?.LiveViewEnabled, IsPrinting = Status?.State.IsActive() == true,
        StaysOnWhilePrinting = _store.CameraStaysOnWhilePrinting, Host = Config.Endpoint.Host, Serial = Config.Serial,
        AccessCode = CameraAccessCode, Insecure = _insecure,
    };

    public string LinkSummary => Phase switch
    {
        PrinterConnection.Phase.Idle => Loc.T("Not connected"),
        PrinterConnection.Phase.Connecting => Loc.T("Connecting…"),
        PrinterConnection.Phase.Online => Loc.T("Connected"),
        PrinterConnection.Phase.Offline => Loc.T("Offline"),
        PrinterConnection.Phase.Unauthorized => Loc.T("Access code rejected"),
        _ => Loc.T("Certificate not trusted"),
    };

    public string? LinkDetail => Phase switch
    {
        PrinterConnection.Phase.Offline o => o.Reason,
        PrinterConnection.Phase.Unauthorized => Loc.T("Check the access code on the printer's screen (Settings → Network) and enter it again."),
        PrinterConnection.Phase.Certificate c => Loc.F("The printer's TLS certificate couldn't be verified: %@", c.Reason),
        _ => null,
    };

    /// <summary>"Printing · 64%", "Ready", "Offline": one line for menus and lists.</summary>
    public string ShortState => Status is { } s && IsOnline
        ? s.State.IsActive() ? $"{s.State.Title()} · {s.Progress}%" : s.StateTitle
        : LinkSummary;

    private void DerivedChanged()
    {
        Changed(nameof(IsOnline));
        Changed(nameof(ChamberLightOn));
        Changed(nameof(Speed));
        Changed(nameof(LinkSummary));
        Changed(nameof(LinkDetail));
        Changed(nameof(ShortState));
    }

    // MARK: Commands

    public void Pause() { if (_demo is not null) _demo.Pause(); else _connection.Pause(); }
    public void Resume() { if (_demo is not null) _demo.Resume(); else _connection.Resume(); }
    public void StopPrint() { if (_demo is not null) _demo.Stop(); else _connection.StopPrint(); }

    public void ToggleLight()
    {
        var on = !ChamberLightOn;
        _pendingLight = on;
        if (_demo is not null) _demo.SetLight(on); else _connection.SetChamberLight(on);
        ClearPendingSoon();
        DerivedChanged();
    }

    public void SetSpeed(SpeedLevel level)
    {
        _pendingSpeed = level;
        if (_demo is not null) _demo.SetSpeed(level); else _connection.SetSpeed(level);
        ClearPendingSoon();
        DerivedChanged();
    }

    /// <summary>The optimistic light and speed give way to the printer's own word after 6 s.</summary>
    private void ClearPendingSoon()
    {
        _pendingTimer?.Dispose();
        _pendingTimer = new Timer(_ => _context.Post(_ =>
        {
            _pendingLight = null;
            _pendingSpeed = null;
            DerivedChanged();
        }, null), null, TimeSpan.FromSeconds(6), Timeout.InfiniteTimeSpan);
    }

    // MARK: Updates

    private void PhaseChanged(PrinterConnection.Phase phase)
    {
        if (phase is PrinterConnection.Phase.Unauthorized && FallbackCodes.Count > 0)
        {
            // Try the slicer's other stored code before telling anyone.
            var next = FallbackCodes[0];
            FallbackCodes.RemoveAt(0);
            if (!_store.IsDemo) _store.Secrets.SetAccessCode(next, Config.Serial);
            _activeCode = next;
            _connection.Start(Config, next, _insecure);
            return;
        }
        if (phase is PrinterConnection.Phase.Online) FallbackCodes = [];
        Phase = phase;
        OnPhase?.Invoke(this, phase);
        Camera.Update();
    }

    // MARK: Dismissed issues

    /// <summary>Issues the person has dismissed, with when the printer last reported each. A dismissal lasts while the
    /// printer keeps reporting the issue and ends once it has been gone for six hours, so a fault that clears and comes
    /// back later shows again, while one that flickers between reports stays hidden.</summary>
    private Dictionary<string, DateTimeOffset>? _dismissed;
    private static readonly TimeSpan DismissalExpiry = TimeSpan.FromHours(6);
    private DateTimeOffset _lastDismissalSave = DateTimeOffset.MinValue;

    private Dictionary<string, DateTimeOffset> Dismissed
    {
        get
        {
            if (_dismissed is null)
            {
                var saved = _store.Settings.Get<Dictionary<string, DateTimeOffset>>(AppSettings.Keys.DismissedIssues(Id)) ?? [];
                _dismissed = saved.Where(p => DateTimeOffset.Now - p.Value < DismissalExpiry).ToDictionary();
            }
            return _dismissed;
        }
    }

    public void Dismiss(PrinterIssue issue)
    {
        Dismissed[issue.Id] = DateTimeOffset.Now;
        _store.Settings.Set(AppSettings.Keys.DismissedIssues(Id), Dismissed);
        if (Status is { } status)
        {
            var copy = status.Clone();
            copy.Issues.RemoveAll(i => i.Id == issue.Id);
            Status = copy;
        }
    }

    /// <summary>Drops dismissed issues from a report and notes that the printer still has them.</summary>
    private void HideDismissed(PrinterStatus status)
    {
        if (Dismissed.Count == 0) return;
        var now = DateTimeOffset.Now;
        foreach (var issue in status.Issues.Where(i => Dismissed.ContainsKey(i.Id))) Dismissed[issue.Id] = now;
        foreach (var expired in Dismissed.Where(p => now - p.Value >= DismissalExpiry).Select(p => p.Key).ToList()) Dismissed.Remove(expired);
        status.Issues.RemoveAll(i => Dismissed.ContainsKey(i.Id));
        // Saving every report would be a write per second while printing; once a minute is plenty.
        if (now - _lastDismissalSave > TimeSpan.FromMinutes(1))
        {
            _store.Settings.Set(AppSettings.Keys.DismissedIssues(Id), Dismissed);
            _lastDismissalSave = now;
        }
    }

    /// <summary>Shows a scripted status, as if the printer had reported it (snapshots, tests).</summary>
    public void ShowStatus(PrinterStatus status, bool canceled = false) => Received(status, canceled);

    private void Received(PrinterStatus reported, bool canceled)
    {
        var fresh = reported.Clone();
        fresh.FitTo(Config.Model);
        HideDismissed(fresh);
        var old = Status;
        Status = fresh;
        LastHeard = DateTimeOffset.Now;
        if (_pendingLight is { } light && fresh.ChamberLightOn == light) _pendingLight = null;
        if (_pendingSpeed is { } level && fresh.Speed == level) _pendingSpeed = null;
        OnStatus?.Invoke(this, old, fresh, canceled);
        HasBaseline = true;
        // The camera follows the connection, the Liveview switch and (Settings → Camera) the print. The Mac's views
        // watch the first two; here the session does, so a feed with no view on screen hears of them too.
        if (old is null || old.State.IsActive() != fresh.State.IsActive() || old.LiveViewEnabled != fresh.LiveViewEnabled) Camera.Update();
    }
}
