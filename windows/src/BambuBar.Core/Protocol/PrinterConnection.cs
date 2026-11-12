using System.Text;
using System.Text.Json;
using BambuBar.Core.Json;
using BambuBar.Core.Model;

namespace BambuBar.Core.Protocol;

/// <summary>
/// Keeps one MQTT session to the printer alive: connects, re-requests a full report, merges the deltas into a status,
/// and reconnects with back-off when the printer drops off the network or the computer sleeps. Everything happens on
/// one <see cref="SynchronizationContext"/>: the one it's created on (the UI thread), or a serial thread of its own.
/// Call its methods from that context. A port of the Mac's PrinterConnection.swift.
/// </summary>
public sealed class PrinterConnection : IDisposable
{
    public abstract record Phase
    {
        public sealed record Idle : Phase;
        public sealed record Connecting : Phase;
        public sealed record Online : Phase;
        public sealed record Offline(string Reason) : Phase;
        public sealed record Unauthorized : Phase;
        public sealed record Certificate(string Reason) : Phase;
    }

    public Action<Phase>? OnPhase { get; set; }
    public Action<PrinterStatus, ReportDocument>? OnStatus { get; set; }
    /// <summary>Any message from the printer, changed or not.</summary>
    public Action? OnHeartbeat { get; set; }
    /// <summary>A command acknowledgement: command name, success, reason.</summary>
    public Action<string, bool, string?>? OnAck { get; set; }

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    private readonly SynchronizationContext _context;
    private readonly SerialContext? _ownContext;
    private readonly Func<DateTimeOffset> _now;
    private Phase _phase = new Phase.Idle();
    private PrinterConfig? _config;
    private string _accessCode = "";
    private bool _insecure;
    private MqttClient? _client;
    private readonly ReportDocument _document = new();
    private PrinterStatus? _lastStatus;
    private DateTimeOffset _lastMessage = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPushall = DateTimeOffset.MinValue;
    private int _attempt;
    private Timer? _retry;
    private Timer? _watchdog;
    private int _sequence;

    public PrinterConnection(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        if (SynchronizationContext.Current is { } current) _context = current;
        else _context = _ownContext = new SerialContext("BambuBar.PrinterConnection");
    }

    /// <summary>The context this connection runs on; post work for it here when calling from elsewhere.</summary>
    public SynchronizationContext Context => _context;

    public Phase CurrentPhase => _phase;

    private void SetPhase(Phase phase)
    {
        if (phase == _phase) return;
        _phase = phase;
        OnPhase?.Invoke(phase);
    }

    public void Start(PrinterConfig config, string accessCode, bool insecure = false)
    {
        Stop();
        _config = config;
        _accessCode = accessCode;
        _insecure = insecure;
        _attempt = 0;
        Open();
        _watchdog = new Timer(_ => _context.Post(_ => CheckHealth(), null), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Stop()
    {
        _retry?.Dispose();
        _watchdog?.Dispose();
        _retry = null;
        _watchdog = null;
        CloseClient();
        _config = null;
        _lastStatus = null;
        _document.Reset();
        SetPhase(new Phase.Idle());
    }

    /// <summary>Asks for a full report now (after wake, or when the person clicks refresh).</summary>
    public void Refresh()
    {
        if (_phase is Phase.Online) RequestFullReport();
        else if (_config is not null) ReconnectSoon(TimeSpan.Zero);
    }

    /// <summary>The computer is going to sleep: drop the session rather than let it time out.</summary>
    public void OnSleep()
    {
        _retry?.Dispose();
        _retry = null;
        CloseClient();
    }

    /// <summary>The computer woke up: reconnect once the network is back.</summary>
    public void OnWake() => ReconnectSoon(TimeSpan.FromSeconds(2));

    public void Dispose()
    {
        Stop();
        _ownContext?.Dispose();
    }

    // MARK: Commands (the JSON Bambu Studio sends)

    public void Pause() => SendPrint(new() { ["command"] = "pause" });
    public void Resume() => SendPrint(new() { ["command"] = "resume" });
    public void StopPrint() => SendPrint(new() { ["command"] = "stop" });
    public void SetSpeed(SpeedLevel level) => SendPrint(new() { ["command"] = "print_speed", ["param"] = $"{(int)level}" });

    public void SetChamberLight(bool on) => Send("system", new()
    {
        ["command"] = "ledctrl", ["led_node"] = "chamber_light", ["led_mode"] = on ? "on" : "off",
        ["led_on_time"] = 500, ["led_off_time"] = 500, ["loop_times"] = 0, ["interval_time"] = 0,
    });

    private void SendPrint(Dictionary<string, object?> body) => Send("print", body);

    private void Send(string key, Dictionary<string, object?> body)
    {
        if (_config is null || _client is null || _phase is not Phase.Online) return;
        _sequence++;
        body["sequence_id"] = $"{_sequence}";
        _client.Publish($"device/{_config.Serial}/request", JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { [key] = body }));
    }

    // MARK: Session

    private void Open()
    {
        if (_config is not { } config) return;
        CloseClient();
        SetPhase(new Phase.Connecting());
        // The watchdog measures silence from here, so it doesn't cut off an attempt that's still connecting.
        _lastMessage = _now();
        var (host, port) = config.Endpoint;
        var client = new MqttClient(new() { Host = host, Port = port, Password = _accessCode, Serial = config.Serial, Insecure = _insecure },
            _context);
        client.OnEvent = e =>
        {
            // Events from a client that has since been replaced are ignored.
            if (!ReferenceEquals(client, _client)) return;
            Handle(e);
        };
        _client = client;
        client.Connect();
    }

    private void CloseClient()
    {
        if (_client is null) return;
        _client.OnEvent = null;
        _client.Disconnect();
        _client = null;
    }

    private void Handle(MqttClient.Event e)
    {
        if (_config is not { } config) return;
        switch (e)
        {
            case MqttClient.Event.Connected:
                _lastMessage = _now();
                _client?.Subscribe($"device/{config.Serial}/report");
                SendRaw("info", new() { ["sequence_id"] = "0", ["command"] = "get_version" });
                RequestFullReport();
                // Stay "connecting" until the first report arrives, so a silent broker doesn't look online.
                break;

            case MqttClient.Event.Message message:
                _lastMessage = _now();
                OnHeartbeat?.Invoke();
                if (Decode(message.Payload) is not { } json) return;
                if (Cast.Dict(json.GetValueOrDefault("print")) is { } p
                    && Cast.AsString(p.GetValueOrDefault("command")) is { } command && command != "push_status"
                    && Cast.AsString(p.GetValueOrDefault("result")) is { } result)
                {
                    OnAck?.Invoke(command, result.Equals("success", StringComparison.OrdinalIgnoreCase),
                        Cast.AsString(p.GetValueOrDefault("reason")));
                }
                if (!_document.Apply(json)) return;
                if (!_document.HasFullReport && !_document.Print.ContainsKey("gcode_state")) return;
                if (_phase is not Phase.Online)
                {
                    // Back-off resets only once the printer is really talking, not on a CONNACK alone.
                    _attempt = 0;
                    SetPhase(new Phase.Online());
                }
                PublishStatus();
                break;

            case MqttClient.Event.Disconnected { Failure: var failure }:
                _client = null;
                switch (failure)
                {
                    case MqttClient.Failure.Refused { Code: 4 or 5 }:
                        // A wrong access code won't fix itself; wait for the person to change it.
                        SetPhase(new Phase.Unauthorized());
                        return;
                    case MqttClient.Failure.Certificate c:
                        // A certificate that doesn't check out won't fix itself either; don't keep sending it the code.
                        SetPhase(new Phase.Certificate(c.Reason));
                        return;
                    case MqttClient.Failure.Unreachable u:
                        SetPhase(new Phase.Offline(u.Reason));
                        break;
                    case MqttClient.Failure.TimedOut:
                        SetPhase(new Phase.Offline(Loc.T("The printer stopped answering.")));
                        break;
                    default:
                        SetPhase(new Phase.Offline(Loc.T("The connection to the printer closed.")));
                        break;
                }
                ScheduleReconnect();
                break;
        }
    }

    private void PublishStatus()
    {
        var status = ReportParser.Status(_document, HmsCatalog.Shared, _config?.Serial);
        status.UpdatedAt = _now();
        if (_lastStatus is { } last)
        {
            if (status.SameContent(last)) return;
            // Keep the timestamp the remaining time was measured at, so the ETA doesn't drift between reports.
            if (status.RemainingMinutes == last.RemainingMinutes && status.Progress == last.Progress && status.State == last.State)
            {
                status.UpdatedAt = last.UpdatedAt;
            }
        }
        _lastStatus = status;
        OnStatus?.Invoke(status, _document);
    }

    private void RequestFullReport()
    {
        _lastPushall = _now();
        SendRaw("pushing", new() { ["sequence_id"] = "0", ["command"] = "pushall" });
    }

    private void SendRaw(string key, Dictionary<string, object?> body)
    {
        if (_config is null || _client is null) return;
        _client.Publish($"device/{_config.Serial}/request", JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { [key] = body }));
    }

    /// <summary>Runs every 10 s: a session that went quiet is renewed, a stale picture refreshed.</summary>
    internal void CheckHealth()
    {
        if (_config is null) return;
        var silence = _now() - _lastMessage;
        switch (_phase)
        {
            case Phase.Online:
                if (silence > TimeSpan.FromSeconds(95))
                {
                    SetPhase(new Phase.Offline(Loc.T("The printer stopped sending updates.")));
                    ReconnectSoon(TimeSpan.Zero);
                }
                else if (silence > TimeSpan.FromSeconds(60) || _now() - _lastPushall > TimeSpan.FromSeconds(300))
                {
                    RequestFullReport();
                }
                break;
            case Phase.Connecting:
                if (_client is not null && silence > TimeSpan.FromSeconds(25)) ReconnectSoon(TimeSpan.Zero);
                break;
        }
    }

    private void ScheduleReconnect()
    {
        var delay = Backoff[Math.Min(_attempt, Backoff.Length - 1)];
        _attempt++;
        ReconnectSoon(delay);
    }

    private void ReconnectSoon(TimeSpan delay)
    {
        if (_config is null) return;
        _retry?.Dispose();
        _retry = new Timer(_ => _context.Post(_ => Open(), null), null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Some firmware sends bytes that aren't valid UTF-8; fall back to Latin-1 rather than drop the report.</summary>
    internal static Dictionary<string, object?>? Decode(byte[] payload)
    {
        // System.Text.Json only notices bad UTF-8 when a string is read, so check the bytes first.
        var text = System.Text.Unicode.Utf8.IsValid(payload) ? Encoding.UTF8.GetString(payload) : Encoding.Latin1.GetString(payload);
        try { return PlainJson.Parse(text) as Dictionary<string, object?>; }
        catch (JsonException) { return null; }
    }
}
