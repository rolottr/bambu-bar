using System.Collections.ObjectModel;
using System.Text.Json;
using BambuBar.Core.Camera;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.App;

/// <summary>
/// The app's single source of truth: the printers, which one is in view, job history and settings. The window and the
/// tray panel show the <i>selected</i> printer; the members without a session argument act on it. The tray icon shows
/// <see cref="MenuBarSession"/>, which follows the Tray page's choice ("Automatic" picks whichever printer is busy).
/// Lives on the UI thread. A port of the Mac's PrinterStore.swift.
/// </summary>
public sealed class PrinterStore : Observable, IDisposable
{
    public sealed record Services
    {
        public required AppSettings Settings { get; init; }
        public required HistoryStore History { get; init; }
        public required ISecretStore Secrets { get; init; }
        public INotifier Notifier { get; init; } = new NullNotifier();
        public IWidgetSink? Widgets { get; init; }
        public IConfirmer? Confirmer { get; init; }
        /// <summary>The command line: <c>--demo</c>, <c>--fleet</c>, <c>--connect</c>, <c>--emulator</c>, <c>--insecure-tls</c>.</summary>
        public IReadOnlyList<string> Arguments { get; init; } = [];
        /// <summary>Extra UDP ports to listen on for announcements (the mock fleet's).</summary>
        public IReadOnlyList<int>? DiscoveryPorts { get; init; }
        /// <summary>How the camera feeds connect and decode; null for the real ones.</summary>
        public CameraServices? Camera { get; init; }
    }

    public sealed record ToastMessage(string Message, bool IsError)
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private readonly Services _services;
    private readonly SynchronizationContext _context;
    private readonly Discovery _discovery;
    private readonly ObservableCollection<PrinterSession> _sessions = [];
    private List<JobRecord> _history = [];
    private readonly List<DiscoveredPrinter> _discovered = [];
    private List<SlicerPrinter> _importedPrinters = [];
    private List<SlicerPrinter> _scannedPrinters = [];
    private bool _insecureTls;
    private bool _isStarted;
    private Timer? _toastTimer;
    private DateTimeOffset _lastWidgetReload = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSnapshotWrite = DateTimeOffset.MinValue;
    private string _lastSavedKey = "";
    private string _lastWidgetKey = "";

    public PrinterStore(Services services)
    {
        _services = services;
        _context = SynchronizationContext.Current ?? throw new InvalidOperationException("PrinterStore needs a SynchronizationContext (the UI thread, or a SerialContext).");
        _discovery = new Discovery(services.DiscoveryPorts ?? Discovery.MockPorts(services.Arguments));
        _insecureTls = services.Arguments.Contains("--insecure-tls");
        Sessions = new ReadOnlyObservableCollection<PrinterSession>(_sessions);
        var s = services.Settings;
        _menuBarStyle = TrayStyles.Parse(s.Get<string>(AppSettings.Keys.MenuBarStyle));
        _notifyFinished = s.Get(AppSettings.Keys.NotifyFinished, true);
        _notifyFailed = s.Get(AppSettings.Keys.NotifyFailed, true);
        _notifyPaused = s.Get(AppSettings.Keys.NotifyPaused, true);
        _notifyStarted = s.Get(AppSettings.Keys.NotifyStarted, false);
        _confirmStop = s.Get(AppSettings.Keys.ConfirmStop, true);
        _showPrinterActions = s.Get(AppSettings.Keys.ShowPrinterActions, true);
        _cameraStaysOnWhilePrinting = s.Get(AppSettings.Keys.CameraStaysOnWhilePrinting, false);
        _cameraEnabled = s.Get(AppSettings.Keys.CameraEnabled, false);
        _menuBarSerial = s.Get<string>(AppSettings.Keys.MenuBarPrinter);
        _selectedSerial = s.Get<string>(AppSettings.Keys.Selected);
    }

    internal AppSettings Settings => _services.Settings;
    internal CameraServices? CameraServices => _services.Camera;
    /// <summary>A run against the mock fleet (<c>--emulator</c>): its printers have no camera.</summary>
    internal bool IsMockFleet => EmulatorSettings(_services.Arguments) is not null;
    internal ISecretStore Secrets => _services.Secrets;

    public ReadOnlyObservableCollection<PrinterSession> Sessions { get; }
    public IReadOnlyList<JobRecord> History => _history;
    public IReadOnlyList<DiscoveredPrinter> Discovered => _discovered;

    private bool _discoveryBlocked;
    /// <summary>The search for printers can't get out or in: the firewall keeps it out (Windows) or every send fails.</summary>
    public bool DiscoveryBlocked { get => _discoveryBlocked; private set => Set(ref _discoveryBlocked, value); }

    /// <summary>Lets the Windows app tell the search whether the firewall blocks the printers' announcements.</summary>
    public Func<bool>? InboundBlocked { get => _discovery.InboundBlocked; set => _discovery.InboundBlocked = value; }

    private List<SlicerPrinter> _slicerPrinters = [];
    /// <summary>Printers whose access code a slicer already knows (this PC's Bambu Studio or OrcaSlicer, or a settings
    /// file imported this session).</summary>
    public IReadOnlyList<SlicerPrinter> SlicerPrinters => _slicerPrinters;

    private ToastMessage? _toast;
    /// <summary>A short-lived message after a command ("The printer refused…").</summary>
    public ToastMessage? Toast { get => _toast; private set => Set(ref _toast, value); }

    public bool IsDemo { get; private set; }

    private string? _selectedSerial;
    public string? SelectedSerial { get => _selectedSerial; private set { if (Set(ref _selectedSerial, value)) CurrentChanged(); } }

    // MARK: Settings

    private string? _menuBarSerial;
    /// <summary>The printer the tray icon follows; null = automatic.</summary>
    public string? MenuBarSerial
    {
        get => _menuBarSerial;
        set { if (Set(ref _menuBarSerial, value)) { Settings.Set(AppSettings.Keys.MenuBarPrinter, value); Changed(nameof(MenuBarSession)); WriteSnapshot(force: true); } }
    }

    private TrayStyle _menuBarStyle;
    public TrayStyle MenuBarStyle
    {
        get => _menuBarStyle;
        set { if (Set(ref _menuBarStyle, value)) Settings.Set(AppSettings.Keys.MenuBarStyle, value.RawValue()); }
    }

    private bool _notifyFinished, _notifyFailed, _notifyPaused, _notifyStarted;
    public bool NotifyFinished { get => _notifyFinished; set => SetNotify(ref _notifyFinished, value, AppSettings.Keys.NotifyFinished); }
    public bool NotifyFailed { get => _notifyFailed; set => SetNotify(ref _notifyFailed, value, AppSettings.Keys.NotifyFailed); }
    public bool NotifyPaused { get => _notifyPaused; set => SetNotify(ref _notifyPaused, value, AppSettings.Keys.NotifyPaused); }
    public bool NotifyStarted { get => _notifyStarted; set => SetNotify(ref _notifyStarted, value, AppSettings.Keys.NotifyStarted); }

    private void SetNotify(ref bool field, bool value, string key, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return;
        Settings.Set(key, value);
        if (_notifyFinished || _notifyFailed || _notifyPaused || _notifyStarted) _services.Notifier.RequestAuthorization();
    }

    private bool _confirmStop;
    public bool ConfirmStop { get => _confirmStop; set { if (Set(ref _confirmStop, value)) Settings.Set(AppSettings.Keys.ConfirmStop, value); } }

    private bool _showPrinterActions;
    public bool ShowPrinterActions { get => _showPrinterActions; set { if (Set(ref _showPrinterActions, value)) Settings.Set(AppSettings.Keys.ShowPrinterActions, value); } }

    private bool _cameraEnabled;
    /// <summary>The camera, for every printer (Settings → Camera); each printer has its own switch too. Off by default.</summary>
    public bool CameraEnabled
    {
        get => _cameraEnabled;
        set { if (Set(ref _cameraEnabled, value)) { Settings.Set(AppSettings.Keys.CameraEnabled, value); CameraSwitchesChanged(); } }
    }

    private bool _cameraStaysOnWhilePrinting;
    /// <summary>Keep the camera streaming for the whole of a print, so it shows at once. Off: only while it's on screen.</summary>
    public bool CameraStaysOnWhilePrinting
    {
        get => _cameraStaysOnWhilePrinting;
        set
        {
            if (!Set(ref _cameraStaysOnWhilePrinting, value)) return;
            Settings.Set(AppSettings.Keys.CameraStaysOnWhilePrinting, value);
            foreach (var session in _sessions) session.Camera.ViewersChanged();
            CameraSettingsChanged?.Invoke();
        }
    }

    /// <summary>A camera switch changed, after the feeds heard of it: the app closes the camera windows of printers whose
    /// camera is now off.</summary>
    public Action? CameraSettingsChanged { get; set; }

    /// <summary>A camera now off stops streaming (the Mac's cameraSettingsChanged).</summary>
    private void CameraSwitchesChanged()
    {
        foreach (var session in _sessions) session.Camera.Update();
        CameraSettingsChanged?.Invoke();
    }

    // MARK: Lifecycle

    /// <summary><c>--emulator &lt;BambuStudio.conf&gt; [--add-all]</c>: the mock fleet from shared/scripts/fleet. Its
    /// printers come from the settings file it writes; nothing is saved, not even the access codes.</summary>
    public static string? EmulatorSettings(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("--emulator");
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    /// <summary><c>--connect &lt;host&gt; &lt;serial&gt; &lt;code&gt;</c>: a throwaway session (the emulator, a borrowed printer).</summary>
    public static (string Host, string Serial, string Code)? DeveloperConnection(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("--connect");
        return i >= 0 && i + 3 < args.Count ? (args[i + 1], args[i + 2], args[i + 3]) : null;
    }

    /// <summary>Runs, demo and developer sessions that must never touch the saved printers, history or widgets.</summary>
    public static bool IsEphemeralRun(IReadOnlyList<string> args) =>
        args.Contains("--demo") || args.Contains("--connect") || args.Contains("--emulator");

    public void Start()
    {
        if (_isStarted) return;
        _isStarted = true;
        _discovery.OnFound = Found;
        _discovery.OnBlocked = blocked => DiscoveryBlocked = blocked;
        var args = _services.Arguments;

        if (DemoDriver.RequestedScenario(args) is { } scenario)
        {
            StartDemo(scenario, args.Contains("--fleet"));
            return;
        }
        if (DeveloperConnection(args) is { } dev)
        {
            // Nothing is saved: not the printer, not the access code, not the history.
            IsDemo = true;
            // The model follows the serial's prefix (a P2S if it's not one Bambu Lab uses).
            var model = PrinterModel.Find("", dev.Serial) ?? PrinterModel.Find("N7");
            var session = Add(new PrinterConfig { Host = dev.Host, Serial = dev.Serial, Name = $"{model?.Name ?? "P2S"} Emulator", ModelCode = model?.Code ?? "N7" });
            session.Start(dev.Code, insecure: _insecureTls);
            SelectedSerial = session.Id;
            return;
        }
        if (EmulatorSettings(args) is { } emulator)
        {
            IsDemo = true;
            _insecureTls = true;
            _discovery.Start();
            ImportSlicerSettings(emulator);
            if (args.Contains("--add-all")) _ = AddAllWhenAnnouncedAsync();
            return;
        }

        foreach (var config in LoadConfigs())
        {
            var session = Add(config);
            if (Secrets.AccessCode(config.Serial) is { } code) session.Start(code, insecure: _insecureTls);
        }
        _history = _services.History.Load();
        Changed(nameof(History));
        if (SelectedSerial is null || Session(SelectedSerial) is null) SelectedSerial = _sessions.FirstOrDefault()?.Id;
        _discovery.Start();
        ReloadSlicerPrinters();
        WriteSnapshot(force: true);
    }

    /// <summary>Waits a little for the fleet's announcements, which carry the printers' names and models.</summary>
    private async Task AddAllWhenAnnouncedAsync()
    {
        for (var i = 0; i < 16; i++)
        {
            if (_slicerPrinters.All(p => _discovered.Any(d => d.Serial == p.Serial))) break;
            await Task.Delay(500);
        }
        _context.Post(_ => AddAll(_slicerPrinters), null);
    }

    public void Stop()
    {
        foreach (var session in _sessions) session.Stop();
        _discovery.Stop();
    }

    public void Dispose()
    {
        foreach (var session in _sessions) session.Dispose();
        _discovery.Dispose();
        _toastTimer?.Dispose();
    }

    /// <summary>The computer sleeps or wakes (Windows' PowerModeChanged): every connection follows.</summary>
    public void OnSleep() { foreach (var s in _sessions) s.OnSleep(); }
    public void OnWake() { foreach (var s in _sessions) s.OnWake(); _discovery.Search(); }

    private List<PrinterConfig> LoadConfigs()
    {
        var saved = Settings.Get<List<PrinterConfig>>(AppSettings.Keys.Printers) ?? [];
        var configs = PrinterConfig.Deduplicated(saved);
        if (!configs.SequenceEqual(saved))
        {
            // A serial saved in another case keeps its access code under its new spelling.
            foreach (var config in saved.Where(c => c.Serial != c.Serial.ToUpperInvariant()))
            {
                var upper = config.Serial.ToUpperInvariant();
                if (Secrets.AccessCode(upper) is null && Secrets.AccessCode(config.Serial) is { } code) Secrets.SetAccessCode(code, upper);
            }
            Settings.Set(AppSettings.Keys.Printers, configs);
        }
        return configs;
    }

    private void SaveConfigs()
    {
        if (IsDemo) return;
        Settings.Set(AppSettings.Keys.Printers, _sessions.Select(s => s.Config).ToList());
    }

    private PrinterSession Add(PrinterConfig config)
    {
        var session = new PrinterSession(config, this)
        {
            // Reconnect attempts flip between offline and connecting; only settled states earn a widget reload.
            OnPhase = (_, phase) => { WriteSnapshot(force: phase is PrinterConnection.Phase.Online or PrinterConnection.Phase.Unauthorized or PrinterConnection.Phase.Idle); PrintersChanged(); },
            OnStatus = (session, old, fresh, canceled) =>
            {
                TrackJob(session, old, fresh, canceled);
                WriteSnapshot(force: old?.State != fresh.State);
                PrintersChanged();
            },
            OnAck = (session, _, ok, reason) => Acknowledged(session, ok, reason),
            OnHeartbeat = _ =>
            {
                // A quiet, unchanging printer is still alive; keep the widgets' snapshot from looking stale.
                if (DateTimeOffset.Now - _lastSnapshotWrite > TimeSpan.FromMinutes(5)) WriteSnapshot(force: false);
            },
        };
        _sessions.Add(session);
        PrintersChanged();
        return session;
    }

    private void PrintersChanged()
    {
        Changed(nameof(MenuBarSession));
        Changed(nameof(HasMultiplePrinters));
        Changed(nameof(IsConfigured));
    }

    private void CurrentChanged()
    {
        Changed(nameof(Current));
        Changed(nameof(MenuBarSession));
        Changed(nameof(LastJob));
    }

    // MARK: Printers

    public PrinterSession? Session(string serial) => _sessions.FirstOrDefault(s => s.Id == serial);

    /// <summary>The printer the window and panel show.</summary>
    public PrinterSession? Current => (SelectedSerial is null ? null : Session(SelectedSerial)) ?? _sessions.FirstOrDefault();

    public void Select(string serial)
    {
        if (Session(serial) is null || SelectedSerial == serial) return;
        SelectedSerial = serial;
        if (!IsDemo) Settings.Set(AppSettings.Keys.Selected, serial);
    }

    /// <summary>The printer the tray icon follows: the one chosen on the Tray page, or automatically the busiest.</summary>
    public PrinterSession? MenuBarSession
    {
        get
        {
            if (MenuBarSerial is not null && Session(MenuBarSerial) is { } chosen) return chosen;
            static int Rank(PrinterSession s)
            {
                if (s.Status is not { } status || !s.IsOnline) return 0;
                if (status.MostSevereIssue is { Severity: <= IssueSeverity.Serious } && status.State.IsActive()) return 4;
                return status.State switch
                {
                    JobState.Paused => 4,
                    JobState.Printing or JobState.Preparing => 3,
                    JobState.Failed or JobState.Finished => 1,
                    _ => 0,
                };
            }
            // Swift's max(by:) keeps the first of equals.
            var best = _sessions.Aggregate((PrinterSession?)null, (b, s) => b is null || Rank(s) > Rank(b) ? s : b);
            return best is not null && Rank(best) >= 3 ? best : Current;
        }
    }

    public bool HasMultiplePrinters => _sessions.Count > 1;

    /// <summary>Saves a printer (new, or a new address/code for a known one) and connects to it.</summary>
    public void Connect(PrinterConfig config, string accessCode, IEnumerable<string>? alternatives = null)
    {
        var code = accessCode.Trim();
        if (!IsDemo) Secrets.SetAccessCode(code, config.Serial);
        PrinterSession session;
        if (Session(config.Serial) is { } existing)
        {
            existing.Config = config;
            session = existing;
        }
        else
        {
            session = Add(config);
        }
        SaveConfigs();
        Select(config.Serial);
        session.Start(code, alternatives, _insecureTls);
        WriteSnapshot(force: true);
    }

    /// <summary>Slicer printers that can be added right away: not set up yet, with an address from the network or the
    /// slicer's settings.</summary>
    public IReadOnlyList<SlicerPrinter> ReadySlicerPrinters =>
        _slicerPrinters.Where(p => Session(p.Serial) is null && HostFor(p) is not null).ToList();

    private string? HostFor(SlicerPrinter printer) => _discovered.FirstOrDefault(d => d.Serial == printer.Serial)?.Host ?? printer.Host;

    /// <summary>Adds every printer a slicer knows in one go, named and typed from their network announcements when
    /// they've been seen, else after their model and serial ("P2S 495").</summary>
    public void AddAll(IEnumerable<SlicerPrinter> printers)
    {
        var added = new List<PrinterSession>();
        foreach (var printer in SlicerImport.Merge(printers).OrderBy(NameFor, NaturalComparer.Instance))
        {
            if (Session(printer.Serial) is not null) continue;
            if (HostFor(printer) is not { } host || printer.AccessCodes.Count == 0) continue;
            var seen = _discovered.FirstOrDefault(d => d.Serial == printer.Serial);
            var config = new PrinterConfig { Host = host, Serial = printer.Serial, Name = NameFor(printer), ModelCode = seen?.ModelCode ?? "" };
            var code = printer.AccessCodes[0];
            if (!IsDemo) Secrets.SetAccessCode(code, config.Serial);
            var session = Add(config);
            session.Start(code, printer.AccessCodes.Skip(1), _insecureTls);
            added.Add(session);
        }
        if (added.Count == 0) return;
        SaveConfigs();
        if (SelectedSerial is null || Session(SelectedSerial) is null) Select(added[0].Id);
        WriteSnapshot(force: true);
    }

    private string NameFor(SlicerPrinter printer)
    {
        var seen = _discovered.FirstOrDefault(d => d.Serial == printer.Serial);
        if (seen?.Name is { Length: > 0 } name && name != printer.Serial) return name;
        return PrinterConfig.PlaceholderName(printer.Serial, seen?.ModelCode ?? "");
    }

    /// <summary>The name the printer announces on the network (set in Bambu Handy or Bambu Studio), when it has been seen.</summary>
    public string? AnnouncedName(string serial) =>
        _discovered.FirstOrDefault(d => d.Serial == serial)?.Name is { Length: > 0 } name && name != serial ? name : null;

    public void Rename(string serial, string name)
    {
        if (Session(serial) is not { } session) return;
        var trimmed = name.Trim();
        session.Config = session.Config with { Name = trimmed.Length == 0 ? serial : trimmed };
        SaveConfigs();
        WriteSnapshot(force: true);
    }

    /// <summary>Turns one printer's camera on or off (Printers page).</summary>
    public void SetCameraEnabled(bool on, string serial)
    {
        if (Session(serial) is not { } session || session.Config.CameraEnabled == on) return;
        session.Config = session.Config with { CameraEnabled = on };
        SaveConfigs();
        CameraSwitchesChanged();
    }

    public void Forget(string serial)
    {
        if (Session(serial) is not { } session) return;
        session.Dispose();
        Secrets.RemoveAccessCode(serial);
        _sessions.Remove(session);
        if (SelectedSerial == serial) SelectedSerial = _sessions.FirstOrDefault()?.Id;
        if (MenuBarSerial == serial) MenuBarSerial = null;
        SaveConfigs();
        PrintersChanged();
        WriteSnapshot(force: true);
    }

    public void Refresh()
    {
        _discovery.Search();
        Current?.Refresh();
    }

    public void SearchForPrinters()
    {
        _discovery.Search();
        ReloadSlicerPrinters();
    }

    private void Found(DiscoveredPrinter printer)
    {
        var i = _discovered.FindIndex(d => d.Serial == printer.Serial);
        if (i >= 0) _discovered[i] = printer; else _discovered.Add(printer);
        Changed(nameof(Discovered));
        if (Session(printer.Serial) is { } session && !IsDemo)
        {
            var changed = false;
            // Follow a printer when DHCP hands it a new address.
            if (session.Config.Host != printer.Host)
            {
                session.Config = session.Config with { Host = printer.Host };
                session.Restart();
                changed = true;
            }
            // Adopt the network-announced name once it arrives, when the stored one is still a name Bambu Bar made up.
            // A name set by hand is left alone.
            if (session.Config.HasGeneratedName && AnnouncedName(printer.Serial) is { } announced && announced != session.Config.Name)
            {
                session.Config = session.Config with { Name = announced };
                changed = true;
            }
            // Correct a saved model id from the printer's own announcement.
            if (printer.ModelCode.Length > 0 && session.Config.ModelCode != printer.ModelCode && PrinterModel.Find(printer.ModelCode) is not null)
            {
                session.Config = session.Config with { ModelCode = printer.ModelCode };
                changed = true;
            }
            if (changed)
            {
                SaveConfigs();
                WriteSnapshot(force: true);
            }
        }
    }

    /// <summary>The serial of the printer at <paramref name="host"/> (an IP address, or <c>host:port</c> for a mock
    /// printer), read from its certificate.</summary>
    public Task<(string? Serial, PrinterIdentity.Failure? Failure)> IdentifyPrinterAsync(string host)
    {
        var (h, port) = new PrinterConfig { Host = host, Serial = "", Name = "" }.Endpoint;
        return PrinterIdentity.SerialAsync(h, port, _insecureTls);
    }

    // MARK: Access codes from slicers

    /// <summary>Rescans this PC's slicer settings.</summary>
    public void ReloadSlicerPrinters()
    {
        _ = Task.Run(() =>
        {
            var scanned = SlicerImport.Scan();
            _context.Post(_ =>
            {
                _scannedPrinters = scanned;
                MergeSlicerPrinters();
            }, null);
        });
    }

    /// <summary>Reads a <c>BambuStudio.conf</c> / <c>OrcaSlicer.conf</c> the person picked or dropped (say, copied from
    /// the computer that runs the slicer). Returns how many printers it knew; they're kept for this session only.</summary>
    public int ImportSlicerSettings(string path)
    {
        var found = SlicerImport.ReadPicked(path);
        // The file just picked replaces what an earlier one said about the same printers.
        foreach (var printer in found)
        {
            _importedPrinters.RemoveAll(p => p.Serial == printer.Serial);
            _importedPrinters.Insert(0, printer);
        }
        MergeSlicerPrinters();
        return found.Count;
    }

    /// <summary>Picked files first, then this PC's slicers; a printer known to both keeps the codes of both to try.</summary>
    private void MergeSlicerPrinters()
    {
        var all = SlicerImport.Merge([.. _importedPrinters, .. _scannedPrinters]);
        if (all.SequenceEqual(_slicerPrinters)) return;
        _slicerPrinters = all;
        Changed(nameof(SlicerPrinters));
        Changed(nameof(ReadySlicerPrinters));
        Changed(nameof(ReplacementCode));
    }

    public SlicerPrinter? SlicerPrinter(string serial) =>
        _slicerPrinters.FirstOrDefault(p => string.Equals(p.Serial, serial, StringComparison.OrdinalIgnoreCase));

    /// <summary>A slicer code for the current printer that differs from the one that was just rejected.</summary>
    public SlicerPrinter? ReplacementCode =>
        Current is { } current && SlicerPrinter(current.Id) is { } printer && printer.AccessCodes.Any(c => c != current.AccessCode)
            ? printer : null;

    public void UseReplacementCode()
    {
        if (Current is not { } current || ReplacementCode is not { } printer) return;
        var codes = printer.AccessCodes.Where(c => c != current.AccessCode).ToList();
        Connect(current.Config, codes[0], codes.Skip(1));
    }

    // MARK: The current printer

    public bool IsConfigured => _sessions.Count > 0;
    public string PrinterName => Current?.Config.Name ?? Loc.T("Printer");
    public string LinkSummary => Current?.LinkSummary ?? Loc.T("No printer set up");

    /// <summary>The current printer's most recent print (for "Last: …" and "finished 3h ago").</summary>
    public JobRecord? LastJob => HistoryFor(Current?.Id).FirstOrDefault();

    public List<JobRecord> HistoryFor(string? serial) => serial is null ? [] : _history.Where(j => j.PrinterSerial == serial).ToList();

    // MARK: Commands (current printer)

    public void Pause()
    {
        if (Current is not { } current || !EnsureControllable(current)) return;
        current.Pause();
        ShowToast(Loc.T("Pausing…"));
    }

    public void Resume()
    {
        if (Current is not { } current || !EnsureControllable(current)) return;
        current.Resume();
        ShowToast(Loc.T("Resuming…"));
    }

    public void StopPrint()
    {
        if (Current is not { } current || !EnsureControllable(current)) return;
        if (ConfirmStop && _services.Confirmer is { } confirmer)
        {
            var job = current.Status?.JobName ?? "";
            var title = job.Length == 0 ? Loc.T("Stop this print?") : Loc.F("Stop “%@”?", job);
            var warning = Loc.T("The print can't be resumed after it's stopped.");
            var detail = HasMultiplePrinters ? Loc.F("On %@.", current.Config.Name) + " " + warning : warning;
            if (!confirmer.ConfirmStop(title, detail)) return;
        }
        current.StopPrint();
        ShowToast(Loc.T("Stopping…"));
    }

    public void ToggleLight() => Current?.ToggleLight();

    public void SetSpeed(SpeedLevel level)
    {
        if (Current is not { } current || !EnsureControllable(current)) return;
        current.SetSpeed(level);
    }

    /// <summary>Print commands need the printer in Developer Mode; say so instead of sending something it will refuse.</summary>
    private bool EnsureControllable(PrinterSession session)
    {
        if (session.Status?.ControlsLocked != true) return true;
        ShowToast(Loc.T("Turn on LAN Only and Developer Mode on the printer to control prints from Bambu Bar."), error: true);
        return false;
    }

    private void Acknowledged(PrinterSession session, bool ok, string? reason)
    {
        if (ok) return;
        var detail = (reason ?? "").Contains("verify", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("The printer only accepts commands in Developer Mode.")
            : reason is not null ? Loc.F("The printer refused: %@", reason) : Loc.T("The printer refused the command.");
        ShowToast(HasMultiplePrinters ? $"{session.Config.Name}: {detail}" : detail, error: true);
    }

    public void ShowToast(string message, bool error = false)
    {
        var toast = new ToastMessage(message, error);
        Toast = toast;
        _toastTimer?.Dispose();
        _toastTimer = new Timer(_ => _context.Post(_ => { if (Toast == toast) Toast = null; }, null), null,
            TimeSpan.FromSeconds(error ? 6 : 3), Timeout.InfiniteTimeSpan);
    }

    // MARK: Recent prints and notifications

    private void TrackJob(PrinterSession session, PrinterStatus? old, PrinterStatus fresh, bool canceled)
    {
        var serial = session.Id;
        // Simulated printers never post real notifications.
        var notify = session.HasBaseline && !IsDemo;
        var name = fresh.JobName.Length == 0 ? Loc.T("Untitled print") : fresh.JobName;
        var spool = fresh.ActiveSpool;
        // Notification titles name the printer once there's more than one.
        var prefix = HasMultiplePrinters ? $"{session.Config.Name}: " : "";
        var notifier = _services.Notifier;
        var historyChanged = false;

        // Start of a job, or the first report of one already running.
        if (fresh.State.IsActive() && (old is null || !old.State.IsActive()))
        {
            var current = _history.FirstOrDefault(j => j.PrinterSerial == serial && j.Outcome == JobOutcome.Running);
            var sameJob = current?.Name == name && (current.TaskId is null || fresh.TaskId is null || current.TaskId == fresh.TaskId);
            if (!sameJob)
            {
                // Anything still marked running on this printer ended while the app wasn't looking.
                for (var i = 0; i < _history.Count; i++)
                {
                    if (_history[i].PrinterSerial == serial && _history[i].Outcome == JobOutcome.Running)
                        _history[i] = _history[i] with { Outcome = JobOutcome.Ended };
                }
                _history.Insert(0, new JobRecord
                {
                    Name = name, StartedAt = DateTimeOffset.Now - EstimateElapsed(fresh), Outcome = JobOutcome.Running,
                    TotalLayers = fresh.TotalLayers, FilamentColor = spool?.Color, FilamentMaterial = spool?.Material,
                    TaskId = fresh.TaskId, PrinterSerial = serial,
                });
                historyChanged = true;
            }
            if (notify && old is not null && NotifyStarted)
                notifier.Post(prefix + Loc.T("Print started"), name, $"start.{serial}");
        }
        var index = _history.FindIndex(j => j.PrinterSerial == serial && j.Outcome == JobOutcome.Running);
        if (index >= 0)
        {
            var job = _history[index];
            // Keep details fresh while it runs (the name arrives late on printer-started jobs).
            var updated = job with
            {
                Name = name,
                TotalLayers = fresh.TotalLayers > 0 ? fresh.TotalLayers : job.TotalLayers,
                FilamentColor = spool?.Color ?? job.FilamentColor,
                FilamentMaterial = spool?.Material ?? job.FilamentMaterial,
            };
            if (!fresh.State.IsActive())
            {
                updated = updated with
                {
                    EndedAt = DateTimeOffset.Now,
                    Outcome = fresh.State switch
                    {
                        JobState.Finished => JobOutcome.Finished,
                        JobState.Failed => canceled ? JobOutcome.Canceled : JobOutcome.Failed,
                        // Straight back to idle without FINISH: stopped from the printer, or the report was missed.
                        _ => canceled ? JobOutcome.Canceled : JobOutcome.Ended,
                    },
                };
                if (notify) NotifyEnd(updated, fresh, prefix, serial);
            }
            if (updated != job)
            {
                _history[index] = updated;
                historyChanged = true;
            }
        }
        if (notify && old is not null && old.State is JobState.Printing or JobState.Preparing && fresh.State == JobState.Paused && NotifyPaused)
            notifier.Post(prefix + Loc.T("Print paused"), $"{name}: {fresh.DetailText}", $"pause.{serial}");
        if (notify && old is not null)
        {
            var newIssue = fresh.Issues.FirstOrDefault(i => i.Severity <= IssueSeverity.Serious && !old.Issues.Exists(o => o.Id == i.Id));
            if (newIssue is not null && (NotifyFailed || NotifyPaused))
                notifier.Post($"{session.Config.Name}: {newIssue.Severity.Title()}", newIssue.Message, $"{newIssue.Id}.{serial}");
        }
        if (historyChanged)
        {
            _services.History.Save(_history);
            Changed(nameof(History));
            Changed(nameof(LastJob));
        }
    }

    private void NotifyEnd(JobRecord job, PrinterStatus status, string prefix, string serial)
    {
        var notifier = _services.Notifier;
        switch (job.Outcome)
        {
            case JobOutcome.Finished when NotifyFinished:
                var body = job.Duration is { } d
                    ? Loc.F("%@ finished in %@. It's ready to take off the plate.", job.Name, Format.Duration(d))
                    : Loc.F("%@ finished. It's ready to take off the plate.", job.Name);
                notifier.Post(prefix + Loc.T("Print finished"), body, $"end.{serial}");
                break;
            case JobOutcome.Failed when NotifyFailed:
                var reason = status.Issues.FirstOrDefault()?.Message ?? Loc.T("the printer stopped the job.", "After the job name and a colon");
                notifier.Post(prefix + Loc.T("Print failed"), $"{job.Name}: {reason}", $"end.{serial}");
                break;
            case JobOutcome.Canceled when NotifyFailed:
                notifier.Post(prefix + Loc.T("Print canceled"), job.Name, $"end.{serial}");
                break;
        }
    }

    private static TimeSpan EstimateElapsed(PrinterStatus s) =>
        s.Progress is > 0 and < 100 && s.RemainingMinutes > 0
            ? TimeSpan.FromSeconds(s.RemainingMinutes * 60.0 * s.Progress / (100 - s.Progress))
            : TimeSpan.Zero;

    // MARK: Widgets

    public PrinterSnapshot Snapshot(PrinterSession session) => new()
    {
        Serial = session.Id, PrinterName = session.Config.Name, Model = session.Config.ModelName,
        Link = session.Phase switch
        {
            PrinterConnection.Phase.Online => PrinterSnapshot.LinkState.Online,
            PrinterConnection.Phase.Connecting => session.Status is null ? PrinterSnapshot.LinkState.Connecting : PrinterSnapshot.LinkState.Online,
            PrinterConnection.Phase.Unauthorized => PrinterSnapshot.LinkState.Unauthorized,
            _ => PrinterSnapshot.LinkState.Offline,
        },
        Status = session.Status,
        LastJob = _history.FirstOrDefault(j => j.PrinterSerial == session.Id && j.Outcome != JobOutcome.Running),
        SavedAt = DateTimeOffset.Now,
    };

    public FleetSnapshot Fleet => new()
    {
        Printers = _sessions.Select(Snapshot).ToList(), MenuBarSerial = MenuBarSession?.Id, SavedAt = DateTimeOffset.Now,
    };

    /// <summary>Hands the widgets their snapshot when it's worth writing, and asks them to redraw when something a
    /// person would notice changed. Widgets extrapolate progress on their own, so routine progress needs only
    /// occasional writes.</summary>
    private void WriteSnapshot(bool force)
    {
        // Demo and developer sessions must never show up in the real widgets.
        if (IsDemo || _services.Widgets is not { } sink) return;
        var fleet = Fleet;
        var key = string.Join("#", fleet.Printers.Select(snap =>
        {
            var s = snap.Status;
            return $"{snap.Serial}|{snap.Link}|{snap.PrinterName}|{s?.State.ToString() ?? "-"}|{s?.StageId ?? -9}|{(s?.Progress ?? 0) / 5}|{s?.JobName ?? ""}|{s?.Issues.Count ?? 0}|{s?.ActiveSpoolId ?? -1}|{string.Concat(s?.Ams.SelectMany(u => u.Spools).Select(sp => sp.Color) ?? [])}";
        })) + $"|{fleet.MenuBarSerial ?? ""}";
        var now = DateTimeOffset.Now;
        // Reports arrive every second; the file only needs to be fresh when the widget next reads it.
        var write = force || key != _lastSavedKey || now - _lastSnapshotWrite > TimeSpan.FromSeconds(20);
        var reload = key != _lastWidgetKey && (force || now - _lastWidgetReload > TimeSpan.FromMinutes(2));
        if (!write && !reload) return;
        if (write)
        {
            _lastSnapshotWrite = now;
            _lastSavedKey = key;
        }
        if (reload)
        {
            _lastWidgetKey = key;
            _lastWidgetReload = now;
        }
        sink.Write(fleet, reload);
    }

    // MARK: Demo

    private void StartDemo(DemoDriver.Scenario scenario, bool fleet)
    {
        IsDemo = true;
        if (scenario == DemoDriver.Scenario.Unconfigured) return;
        // Not saved: a demo run must not replace the real printers' settings.
        var printers = new List<(PrinterConfig, DemoDriver.Scenario)>
        {
            (new() { Host = "192.168.1.50", Serial = "22E00A000000000", Name = "P2S Workshop", ModelCode = "N7" }, scenario),
        };
        if (fleet)
        {
            printers.Add((new() { Host = "192.168.1.51", Serial = "00M00A000000000", Name = "X1C Garage", ModelCode = "BL-P001" }, DemoDriver.Scenario.Idle));
            printers.Add((new() { Host = "192.168.1.52", Serial = "039000000000000", Name = "A1 Desk", ModelCode = "N2S" }, DemoDriver.Scenario.Paused));
            printers.Add((new() { Host = "192.168.1.53", Serial = "094000000000000", Name = "H2D Studio", ModelCode = "O1D" }, DemoDriver.Scenario.Printing));
            printers.Add((new() { Host = "192.168.1.54", Serial = "030000000000000", Name = "A1 mini Shelf", ModelCode = "N1" }, DemoDriver.Scenario.Finished));
            printers.Add((new() { Host = "192.168.1.55", Serial = "01P000000000000", Name = "P1S Office", ModelCode = "C12" }, DemoDriver.Scenario.Printing));
        }
        _history = DemoDriver.SampleHistory(printers.Select(p => p.Item1.Serial).ToList());
        for (var i = 0; i < printers.Count; i++) Add(printers[i].Item1).StartDemo(printers[i].Item2, i);
        SelectedSerial = _sessions.FirstOrDefault()?.Id;
        Changed(nameof(History));
    }
}

/// <summary>Orders names the way Finder and Explorer do ("P2S 2" before "P2S 10"), case-insensitively.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a is null || b is null) return string.Compare(a, b, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
                continue;
            }
            var cmp = string.Compare(a[i].ToString(), b[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
            if (cmp != 0) return cmp;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
