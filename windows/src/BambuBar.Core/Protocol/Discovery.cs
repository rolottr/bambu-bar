using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace BambuBar.Core.Protocol;

/// <summary>A printer that announced itself on the local network.</summary>
public sealed record DiscoveredPrinter
{
    public required string Serial { get; init; }
    public required string Host { get; init; }
    public required string Name { get; init; }
    public required string ModelCode { get; init; }
    public string? Firmware { get; init; }
    /// <summary>"lan" (LAN Only mode) or "cloud".</summary>
    public string? ConnectMode { get; init; }
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.Now;

    public string ModelName => Model.PrinterModel.NameFor(ModelCode, Serial);
}

/// <summary>
/// Listens for the SSDP-style NOTIFY packets Bambu printers broadcast on UDP 2021 (and answers to an M-SEARCH), so the
/// printer can be picked from a list and its IP address followed when DHCP changes it. Bambu Studio listens on 2021
/// too: Windows has no SO_REUSEPORT, so the sockets share the port with <c>ExclusiveAddressUse = false</c> and
/// <c>ReuseAddress</c>, set before binding.
/// </summary>
public sealed class Discovery : IDisposable
{
    /// <summary>The M-SEARCH Bambu Studio sends.</summary>
    public const string SearchMessage =
        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nMAN: \"ssdp:discover\"\r\nMX: 3\r\nST: urn:bambulab-com:device:3dprinter:1\r\n\r\n";

    private static readonly IPAddress Group = IPAddress.Parse("239.255.255.250");

    private readonly EventPump<DiscoveredPrinter> _found;
    private readonly EventPump<bool> _blockedEvents;
    private readonly IReadOnlyList<int> _ports;
    private readonly List<Socket> _sockets = [];
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private bool _blocked;
    private Timer? _retry;

    public Action<DiscoveredPrinter>? OnFound { get; set; }

    /// <summary>True when the search can't leave this computer although it's on a network. Delivered on changes only.</summary>
    public Action<bool>? OnBlocked { get; set; }

    /// <summary>Set by the Windows app: whether the firewall keeps the printers' announcements out. A blocked inbound
    /// port doesn't make sends fail, so the send check below can't see it.</summary>
    public Func<bool>? InboundBlocked { get; set; }

    /// <param name="extraPorts">Where else to listen, besides the ports printers use (the mock fleet's).</param>
    public Discovery(IEnumerable<int>? extraPorts = null)
    {
        _ports = [2021, 1990, .. extraPorts ?? []];
        _found = new(p => OnFound?.Invoke(p));
        _blockedEvents = new(b => OnBlocked?.Invoke(b));
    }

    /// <summary>The mock fleet announces itself on a port of its own (shared/scripts/fleet), so a copy of Bambu Bar in
    /// daily use, also bound to 2021, doesn't take its unicast packets.</summary>
    public static IReadOnlyList<int> MockPorts(IEnumerable<string> arguments) => arguments.Contains("--emulator") ? [2121] : [];

    public bool IsRunning { get { lock (_gate) return _stop is not null; } }

    /// <summary>The ports it could bind (a port another program holds exclusively is skipped).</summary>
    public IReadOnlyList<int> ListeningPorts
    {
        get { lock (_gate) return _sockets.Select(sk => ((System.Net.IPEndPoint)sk.LocalEndPoint!).Port).ToList(); }
    }

    public void Start()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_stop is not null) return;
            _stop = new CancellationTokenSource();
            token = _stop.Token;
            foreach (var port in _ports)
            {
                if (OpenSocket(port) is { } socket) _sockets.Add(socket);
            }
        }
        foreach (var socket in _sockets.ToList()) _ = Task.Run(() => ListenAsync(socket, token), token);
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
        Search();
    }

    public void Stop()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
        lock (_gate)
        {
            _stop?.Cancel();
            _stop = null;
            _retry?.Dispose();
            _retry = null;
            foreach (var socket in _sockets) socket.Dispose();
            _sockets.Clear();
        }
    }

    public void Dispose() => Stop();

    private void OnNetworkAvailability(object? sender, NetworkAvailabilityEventArgs e)
    {
        // Joining a network (or learning that there is one): ask right away.
        if (e.IsAvailable) Search();
    }

    /// <summary>Asks every printer to answer now instead of waiting for its next broadcast (they announce every
    /// ~30 s). Sent from a listening socket: printers answer unicast to the port the search came from.</summary>
    public void Search()
    {
        Socket? socket;
        lock (_gate) socket = _sockets.FirstOrDefault();
        if (socket is null) return;
        var message = Encoding.ASCII.GetBytes(SearchMessage);
        var failures = new List<SocketError>();
        foreach (var (address, port) in new[] { ("239.255.255.250", 2021), ("239.255.255.250", 1990), ("255.255.255.255", 2021) })
        {
            try { socket.SendTo(message, new IPEndPoint(IPAddress.Parse(address), port)); }
            catch (SocketException e) { failures.Add(e.SocketErrorCode); }
            catch (ObjectDisposedException) { return; }
        }
        // Every send failing while there's a network means something keeps the search in; without a network the
        // sends fail too, and that's not a permission problem.
        var networkUp = NetworkInterface.GetIsNetworkAvailable();
        var denied = networkUp && failures.Count == 3
            && !failures.Any(f => f is SocketError.NetworkUnreachable or SocketError.NetworkDown);
        SetBlocked(denied || (networkUp && InboundBlocked?.Invoke() == true));
    }

    /// <summary>While blocked, searches again every few seconds, so the printers turn up as soon as access is on.</summary>
    private void SetBlocked(bool value)
    {
        lock (_gate)
        {
            if (value && _retry is null && _stop is not null)
            {
                _retry = new Timer(_ => Search(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
            else if (!value)
            {
                _retry?.Dispose();
                _retry = null;
            }
            if (value == _blocked) return;
            _blocked = value;
        }
        _blockedEvents.Post(value);
    }

    private static Socket? OpenSocket(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Shared with Bambu Studio (and other copies of Bambu Bar): both must be set before binding.
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (!OperatingSystem.IsWindows()) TrySetReusePort(socket);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, IPAddress.Any));
            }
            catch (SocketException) { /* No multicast route (no network yet): broadcasts and unicast answers still arrive. */ }
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>macOS and Linux need SO_REUSEPORT to share a UDP port (development runs beside the Mac app).</summary>
    private static void TrySetReusePort(Socket socket)
    {
        const int SoReusePortMac = 0x0200, SoReusePortLinux = 15;
        try
        {
            socket.SetRawSocketOption(OperatingSystem.IsMacOS() ? 0xFFFF : 1, OperatingSystem.IsMacOS() ? SoReusePortMac : SoReusePortLinux,
                BitConverter.GetBytes(1));
        }
        catch (SocketException) { }
    }

    private async Task ListenAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue; // An ICMP "port unreachable" for an earlier send, or an oversized packet.
            }
            catch (SocketException) { return; }
            var sender = (result.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            var text = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
            if (Parse(text, sender) is { } printer)
            {
                // A printer got through, so nothing is blocked (whatever the last search's sends said).
                SetBlocked(false);
                _found.Post(printer);
            }
        }
    }

    public static DiscoveredPrinter? Parse(string text, string sender)
    {
        if (!text.StartsWith("NOTIFY", StringComparison.Ordinal) && !text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim(' ', '\t').ToLowerInvariant()] = line[(colon + 1)..].Trim(' ', '\t');
        }
        if (headers.GetValueOrDefault("nt")?.Contains("bambulab", StringComparison.Ordinal) != true
            && headers.GetValueOrDefault("st")?.Contains("bambulab", StringComparison.Ordinal) != true) return null;
        if (headers.GetValueOrDefault("usn") is not { Length: > 0 } rawSerial) return null;
        // Match the case used everywhere a session is stored, so a printer that announces a lowercase serial isn't
        // taken for a second, unconfigured one.
        var serial = rawSerial.ToUpperInvariant();
        var host = headers.GetValueOrDefault("location") is { Length: > 0 } location ? location : sender;
        return new DiscoveredPrinter
        {
            Serial = serial, Host = host, Name = headers.GetValueOrDefault("devname.bambu.com") ?? serial,
            ModelCode = headers.GetValueOrDefault("devmodel.bambu.com") ?? "",
            Firmware = headers.GetValueOrDefault("devversion.bambu.com"),
            ConnectMode = headers.GetValueOrDefault("devconnect.bambu.com"),
        };
    }
}
