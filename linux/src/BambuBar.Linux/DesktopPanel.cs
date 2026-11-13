using System.ComponentModel;
using System.Collections.Specialized;
using System.Text.Json;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;
using Tmds.DBus;

namespace BambuBar.Linux;

[DBusInterface("dev.rolottr.BambuBar.Desktop")]
public interface IDesktopPanel : IDBusObject
{
    Task<string> GetStateAsync();
    Task SetOpenAsync(bool open);
    Task InvokeAsync(string action, string printer);
    Task<IDisposable> WatchStateChangedAsync(Action<string> handler);
}

/// <summary>Small, event-driven desktop API. No credentials, camera frames or protocol access cross this boundary.</summary>
internal sealed class DesktopPanel : IDesktopPanel, IDisposable
{
    public const string BusName = Platform.AppId + ".Desktop";
    public const string ShellName = Platform.AppId + ".Shell";
    public ObjectPath ObjectPath => new("/dev/rolottr/BambuBar/Desktop");
    private readonly Connection _connection = new(Address.Session);
    private readonly PrinterStore _store;
    private readonly AppSettings _settings;
    private readonly Action<string, PrinterSession?> _invoke;
    private readonly SynchronizationContext _context = SynchronizationContext.Current!;
    private readonly HashSet<PrinterSession> _sessions = [];
    private event Action<string>? StateChanged;
    private string _state = "{}", _artKey = "", _art = "", _artSerial = "";
    private bool _queued, _open, _disposed, _rendering;

    public DesktopPanel(PrinterStore store, AppSettings settings, Action<string, PrinterSession?> invoke)
    {
        _store = store; _settings = settings; _invoke = invoke;
        _store.PropertyChanged += Changed;
        ((INotifyCollectionChanged)_store.Sessions).CollectionChanged += SessionsChanged;
        Subscribe();
        Publish();
    }
    public async Task Start()
    {
        await _connection.ConnectAsync();
        await _connection.RegisterObjectAsync(this);
        await _connection.RegisterServiceAsync(BusName);
    }
    private void SessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) { Subscribe(); Queue(); }
    private void Subscribe()
    {
        foreach (var old in _sessions.Where(s => !_store.Sessions.Contains(s)).ToArray()) { old.PropertyChanged -= Changed; _sessions.Remove(old); }
        foreach (var session in _store.Sessions) if (_sessions.Add(session)) session.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Queue();
    public void Queue()
    {
        if (_queued || _disposed) return;
        _queued = true;
        GLib.Functions.IdleAdd(200, () => { _queued = false; if (!_disposed) Publish(); return false; });
    }
    public Task<string> GetStateAsync() => Task.FromResult(Volatile.Read(ref _state));
    public Task SetOpenAsync(bool open)
    {
        _context.Post(_ =>
        {
            if (_disposed) return;
            _open = open;
            if (open && _store.MenuBarSession is { } session) _store.Select(session.Id);
            Publish();
        }, null);
        return Task.CompletedTask;
    }
    public Task InvokeAsync(string action, string printer)
    {
        _context.Post(_ =>
        {
            if (_disposed) return;
            var session = _store.Session(printer);
            if (session is not null) _store.Select(session.Id);
            // The app rechecks capabilities and lock state at invocation time, not just in the menu.
            _invoke(action, session);
            if (!_disposed) Publish();
        }, null);
        return Task.CompletedTask;
    }
    public Task<IDisposable> WatchStateChangedAsync(Action<string> handler)
    {
        StateChanged += handler;
        return Task.FromResult<IDisposable>(new Subscription(() => StateChanged -= handler));
    }
    private void Publish()
    {
        var session = _store.Current;
        var status = session?.Status;
        var online = session?.IsOnline == true;
        var controls = online && status?.ControlsLocked == false && _store.ShowPrinterActions;
        var tray = _store.MenuBarSession;
        if (_open && session is not null) RenderArt(session);
        var labelKeys = new[] { "Bambu Bar", "No printer set up", "Connected", "Printer", "Settings", "Refresh", "Open Bambu Bar", "Quit", "Camera", "Filament", "All Printers", "Nozzle", "Right", "Left", "Bed", "Chamber", "Fans", "Part cooling", "Auxiliary", "Light", "Turn light on", "Turn light off", "Pause", "Resume", "Stop", "Speed", "Enable Developer Mode on the printer to use controls", "Add Printer…", "Not connected" };
        var payload = new
        {
            version = 1,
            labels = labelKeys.ToDictionary(k => k, k => Loc.T(k)),
            panelText = tray?.Status is { } t && tray.IsOnline && t.State.IsActive() ? _store.MenuBarStyle.Text(t) ?? "" : "",
            title = tray is null ? "Bambu Bar" : tray.Config.Name + " · " + tray.ShortState,
            printers = _store.Sessions.Select(s => new { id = s.Id, name = s.Config.Name, state = s.ShortState }).ToArray(),
            printer = session is null ? null : new
            {
                id = session.Id, name = session.Config.Name, model = session.Config.ModelName,
                online, link = session.LinkSummary, state = session.ShortState,
                kind = online ? status?.State.ToString().ToLowerInvariant() ?? "idle" : "offline",
                job = status?.JobName is { Length: > 0 } job ? job : online ? Loc.T("Ready to print") : session.LinkSummary,
                progress = status?.Progress ?? 0,
                detail = status?.State.IsActive() == true ? Format.Duration(status.RemainingMinutes) + " " + Loc.T("left") : status is { TotalLayers: > 0 } ? Loc.F("Layer %lld of %lld", status.Layer, status.TotalLayers) : session.LinkSummary,
                issue = status?.MostSevereIssue?.Message ?? "",
                art = _artSerial == session.Id ? _art : "",
                temperatures = !_settings.Get(AppSettings.Keys.PanelTemperatures, true) ? [] : new[]
                {
                    new { name = Loc.T(status?.SecondNozzle is null ? "Nozzle" : "Right"), value = TemperatureText(status?.Nozzle) },
                    status?.SecondNozzle is null ? null : new { name = Loc.T("Left"), value = TemperatureText(status.SecondNozzle) },
                    new { name = Loc.T("Bed"), value = TemperatureText(status?.Bed) },
                    status?.Chamber is null ? null : new { name = Loc.T("Chamber"), value = $"{status.Chamber:0}°" },
                }.OfType<object>().ToArray(),
                spools = !_settings.Get(AppSettings.Keys.PanelFilament, true) ? [] : status?.AllSpools.Where(s => s.IsLoaded).Select(s => new { slot = s.SlotName, name = s.DisplayName, color = s.Color, remaining = s.RemainDetail }).ToArray(),
                fans = !_settings.Get(AppSettings.Keys.PanelFans, false) ? "" : Loc.T("Part cooling") + " " + (status?.PartFan is { } fan ? $"{fan}%" : "—"),
                camera = session.ShowsCamera && online,
                light = online && (session.Config.Model?.HasChamberLight == true || status?.ChamberLightOn is not null),
                lightOn = session.ChamberLightOn,
                showControls = _store.ShowPrinterActions && status?.State.IsActive() == true,
                canSpeed = controls,
                speed = (int)session.Speed,
                speeds = Enum.GetValues<SpeedLevel>().Select(s => new { id = (int)s, title = s.Title() }).ToArray(),
                pause = controls && status?.State is JobState.Printing or JobState.Paused,
                paused = status?.State == JobState.Paused,
                stop = controls && status?.State.IsActive() == true,
                locked = online && status?.ControlsLocked == true,
            },
        };
        var json = JsonSerializer.Serialize(payload);
        if (json == _state) return;
        Volatile.Write(ref _state, json);
        StateChanged?.Invoke(json);
    }
    private static string TemperatureText(Temperature? temperature) => temperature is { } t ? t.Target > 0 && !t.IsAtTarget ? $"{t.Current:0}° → {t.Target:0}°" : $"{t.Current:0}°" : "—";
    private async void RenderArt(PrinterSession session)
    {
        if (_rendering) return;
        var status = session.Status;
        var scene = new PrinterArt.Scene
        {
            Shape = session.Config.Model?.Shape ?? PrinterShape.Enclosed, State = status?.State ?? JobState.Idle,
            Progress = (status?.Progress ?? 0) / 100.0, Filament = status?.ActiveSpool?.Color,
            Spools = status?.Ams.SelectMany(a => a.Spools).Select(s => s.IsLoaded ? s.Color : null).ToArray() ?? [],
            LightOn = session.ChamberLightOn, Dimmed = !session.IsOnline,
        };
        var key = session.Id + ":" + JsonSerializer.Serialize(scene);
        if (_artKey == key) return;
        _artKey = key;
        _rendering = true;
        try
        {
            var image = await Task.Run(() => PrinterArt.Render(scene, 220, 280));
            if (_disposed || image is null) return;
            var folder = Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? Path.Combine(Platform.Home, ".cache"), "bambu-bar", "panel");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
            using var bytes = GLib.Bytes.New(image.Pixels);
            using var texture = Gdk.MemoryTexture.New(image.Width, image.Height, Gdk.MemoryFormat.R8g8b8a8, bytes, (nuint)(image.Width * 4));
            if (!texture.SaveToPng(path)) return;
            var previous = _art;
            _art = path; _artKey = key; _artSerial = session.Id;
            if (previous.Length > 0) File.Delete(previous);
        }
        catch (Exception) { /* Status and controls remain useful if artwork cannot be rendered. */ }
        finally { _rendering = false; if (!_disposed) Queue(); }
    }
    public void Dispose()
    {
        _disposed = true;
        _store.PropertyChanged -= Changed;
        ((INotifyCollectionChanged)_store.Sessions).CollectionChanged -= SessionsChanged;
        foreach (var session in _sessions) session.PropertyChanged -= Changed;
        _connection.Dispose();
        if (_art.Length > 0) try { File.Delete(_art); } catch (IOException) { }
    }
    private sealed class Subscription(Action unsubscribe) : IDisposable { public void Dispose() => unsubscribe(); }
}
