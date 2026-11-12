using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace BambuBar.Core.Protocol;

/// <summary>
/// A small MQTT 3.1.1 client over TLS 1.2, just what a Bambu printer's broker needs: CONNECT with username and
/// password, SUBSCRIBE, QoS 0 PUBLISH both ways, PUBACK for QoS 1, keepalive pings. Events are posted to the
/// <see cref="SynchronizationContext"/> the client was created on (the UI thread in the app), in order.
/// </summary>
public sealed class MqttClient
{
    public sealed record Configuration
    {
        public required string Host { get; init; }
        public int Port { get; init; } = 8883;
        public string Username { get; init; } = "bblp";
        public required string Password { get; init; }
        /// <summary>The printer serial, which its certificate is named after.</summary>
        public required string Serial { get; init; }
        public int KeepAliveSeconds { get; init; } = 30;
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(12);
        /// <summary>Accept any certificate (the printer emulators only).</summary>
        public bool Insecure { get; init; }
        /// <summary>Called with the printer's certificate on every handshake, before it's judged (tests, probe).</summary>
        public Action<X509Certificate2>? OnCertificate { get; init; }
    }

    public abstract record Failure
    {
        /// <summary>CONNACK return code: 4 bad username/password, 5 not authorised.</summary>
        public sealed record Refused(byte Code) : Failure;
        public sealed record Certificate(string Reason) : Failure;
        public sealed record Unreachable(string Reason) : Failure;
        public sealed record TimedOut : Failure;
        public sealed record Closed : Failure;
        public sealed record Malformed : Failure;

        public bool IsAuthentication => this is Refused { Code: 4 or 5 };
    }

    public abstract record Event
    {
        public sealed record Connected : Event;
        public sealed record Message(string Topic, byte[] Payload) : Event;
        public sealed record Disconnected(Failure Failure) : Event;
    }

    public Action<Event>? OnEvent { get; set; }

    private readonly Configuration _config;
    private readonly SynchronizationContext? _context;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    /// <summary>Events in order when there's no context to post them to (tests, the console probe).</summary>
    private readonly Channel<Event>? _events;
    private readonly object _gate = new();
    private Socket? _socket;
    private SslStream? _stream;
    private BambuTrust.Verdict? _verdict;
    private bool _open;
    private bool _finished;
    private ushort _nextPacketId = 1;
    private long _lastInboundTicks = Environment.TickCount64;

    /// <param name="context">Where events go; by default the context the client is created on.</param>
    public MqttClient(Configuration config, SynchronizationContext? context = null)
    {
        _config = config;
        _context = context ?? SynchronizationContext.Current;
        if (_context is null)
        {
            _events = Channel.CreateUnbounded<Event>(new() { SingleReader = true });
            _ = Task.Run(async () =>
            {
                await foreach (var e in _events.Reader.ReadAllAsync().ConfigureAwait(false)) OnEvent?.Invoke(e);
            });
        }
    }

    public void Connect() => _ = Task.Run(RunAsync);

    public void Subscribe(string topic)
    {
        var topicBytes = MqttString(topic);
        var body = new byte[2 + topicBytes.Length + 1];
        BinaryPrimitives.WriteUInt16BigEndian(body, AllocatePacketId());
        topicBytes.CopyTo(body, 2);
        body[^1] = 0; // QoS 0
        Send(0x82, body);
    }

    public void Publish(string topic, byte[] payload)
    {
        var topicBytes = MqttString(topic);
        Send(0x30, [.. topicBytes, .. payload]);
    }

    public void Disconnect()
    {
        bool open;
        lock (_gate) open = _open && !_finished;
        if (open) Send(0xE0, []);
        // Give the DISCONNECT a moment to leave before the socket closes.
        _ = Task.Delay(open ? 200 : 0).ContinueWith(_ => Finish(new Failure.Closed(), notify: false), TaskScheduler.Default);
    }

    // MARK: Connection

    private async Task RunAsync()
    {
        var token = _stop.Token;
        _ = Task.Delay(_config.ConnectTimeout + TimeSpan.FromSeconds(4), token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            bool open;
            lock (_gate) open = _open;
            if (!open) Finish(new Failure.TimedOut());
        }, TaskScheduler.Default);

        try
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            lock (_gate)
            {
                if (_finished) { socket.Dispose(); return; }
                _socket = socket;
            }
            EnableKeepAlive(socket);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connectTimeout.CancelAfter(_config.ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(_config.Host, _config.Port, connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Finish(new Failure.Unreachable(Loc.T("The printer didn't answer.")));
                    return;
                }
            }

            var stream = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
            lock (_gate) _stream = stream;
            await stream.AuthenticateAsClientAsync(TlsOptions(_config.Host, Verify), token).ConfigureAwait(false);

            // CONNECT goes first, ahead of anything queued while the connection was coming up.
            await stream.WriteAsync(ConnectPacket(), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            _ = Task.Run(() => WriteLoopAsync(stream, token), token);
            await ReadLoopAsync(stream, token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Finished elsewhere.
        }
        catch (AuthenticationException e)
        {
            Finish(_verdict is BambuTrust.Verdict.Rejected rejected
                ? new Failure.Certificate(rejected.Reason)
                : new Failure.Unreachable(e.Message));
        }
        catch (SocketException e)
        {
            Finish(new Failure.Unreachable(Describe(e.SocketErrorCode, e.Message)));
        }
        catch (IOException e) when (e.InnerException is SocketException se)
        {
            Finish(_verdict is BambuTrust.Verdict.Rejected rejected
                ? new Failure.Certificate(rejected.Reason)
                : new Failure.Unreachable(Describe(se.SocketErrorCode, se.Message)));
        }
        catch (IOException e)
        {
            Finish(_verdict is BambuTrust.Verdict.Rejected rejected
                ? new Failure.Certificate(rejected.Reason)
                : new Failure.Unreachable(e.Message));
        }
        catch (ObjectDisposedException)
        {
            Finish(new Failure.Closed());
        }
    }

    private bool Verify(X509Certificate2? leaf, IEnumerable<X509Certificate2> presented)
    {
        if (leaf is null)
        {
            _verdict = new BambuTrust.Verdict.Rejected(Loc.T("The printer sent no certificate."));
            return false;
        }
        _config.OnCertificate?.Invoke(leaf);
        if (_config.Insecure) return true;
        _verdict = BambuTrust.Evaluate(leaf, presented, _config.Serial);
        return !_verdict.IsRejected;
    }

    /// <summary>TLS as the printers speak it, with the certificate decision left to <paramref name="verify"/>.</summary>
    internal static SslClientAuthenticationOptions TlsOptions(string host, Func<X509Certificate2?, IEnumerable<X509Certificate2>, bool> verify) => new()
    {
        // The certificate names the serial; the host is only for SNI, which isn't sent for IP addresses.
        TargetHost = host,
        // A P2S never answers a TLS 1.3 ClientHello.
        EnabledSslProtocols = SslProtocols.Tls12,
        // A resumed session skips the certificate, and with it `verify`: the only check that the peer is the printer
        // with the expected serial (and, for PrinterIdentity, the only way to read that serial). Every connection
        // gets a full handshake instead; on a LAN it costs next to nothing.
        AllowTlsResume = false,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
        {
            if (certificate is null) return verify(null, []);
            var leaf = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
            // The intermediates the printer sent, which the platform put beside its own attempt at a chain.
            var presented = chain?.ChainPolicy.ExtraStore.Cast<X509Certificate2>().ToList() ?? [];
            return verify(leaf, presented);
        },
    };

    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 20);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        }
        catch (SocketException) { }
        catch (PlatformNotSupportedException) { }
    }

    /// <summary>Why a connection failed, in words for the person (Swift's <c>MQTTClient.describe</c>).</summary>
    public static string Describe(SocketError error, string fallback) => error switch
    {
        SocketError.ConnectionRefused => Loc.T("The printer refused the connection."),
        SocketError.HostUnreachable or SocketError.HostDown => Loc.T("The printer isn't reachable on the network."),
        SocketError.NetworkDown or SocketError.NetworkUnreachable => Loc.T("This PC isn't on a network."),
        SocketError.TimedOut => Loc.T("The printer didn't answer."),
        _ => fallback,
    };

    private byte[] ConnectPacket()
    {
        var body = new List<byte>();
        body.AddRange(MqttString("MQTT"));
        body.Add(4);    // protocol level 3.1.1
        body.Add(0xC2); // username, password, clean session
        body.Add((byte)(_config.KeepAliveSeconds >> 8));
        body.Add((byte)(_config.KeepAliveSeconds & 0xFF));
        body.AddRange(MqttString($"bambubar-{Guid.NewGuid().ToString()[..12]}"));
        body.AddRange(MqttString(_config.Username));
        body.AddRange(MqttString(_config.Password));
        return Packet(0x10, [.. body]);
    }

    private async Task WriteLoopAsync(SslStream stream, CancellationToken token)
    {
        try
        {
            await foreach (var packet in _outbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await stream.WriteAsync(packet, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested) Finish(new Failure.Unreachable(e.Message));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadLoopAsync(SslStream stream, CancellationToken token)
    {
        var buffer = new List<byte>(64 * 1024);
        var chunk = new byte[256 * 1024];
        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0)
            {
                Finish(new Failure.Closed());
                return;
            }
            Interlocked.Exchange(ref _lastInboundTicks, Environment.TickCount64);
            buffer.AddRange(chunk.AsSpan(0, read));
            if (!DrainPackets(buffer)) return;
        }
    }

    /// <summary>Handles every complete packet in <paramref name="buffer"/>; false once the connection is finished.</summary>
    private bool DrainPackets(List<byte> buffer)
    {
        while (true)
        {
            lock (_gate) if (_finished) return false;
            if (buffer.Count < 2) return true;
            // Remaining length: up to four 7-bit groups.
            int multiplier = 1, length = 0, index = 1;
            var done = false;
            for (var i = 0; i < 4; i++)
            {
                if (index >= buffer.Count) return true;
                int b = buffer[index];
                length += (b & 0x7F) * multiplier;
                multiplier *= 128;
                index++;
                if ((b & 0x80) == 0) { done = true; break; }
            }
            if (!done) { Finish(new Failure.Malformed()); return false; }
            if (buffer.Count - index < length) return true;
            var header = buffer[0];
            var body = buffer.GetRange(index, length).ToArray();
            buffer.RemoveRange(0, index + length);
            HandlePacket(header, body);
        }
    }

    private void HandlePacket(byte header, byte[] body)
    {
        switch (header >> 4)
        {
            case 2: // CONNACK
                if (body.Length < 2) { Finish(new Failure.Malformed()); return; }
                if (body[1] != 0) { Finish(new Failure.Refused(body[1])); return; }
                lock (_gate) _open = true;
                StartPings();
                Deliver(new Event.Connected());
                break;
            case 3: // PUBLISH
                var qos = (header >> 1) & 0x03;
                if (body.Length < 2) return;
                var topicLength = body[0] << 8 | body[1];
                var offset = 2 + topicLength;
                if (offset > body.Length) return;
                var topic = Encoding.UTF8.GetString(body, 2, topicLength);
                if (qos > 0)
                {
                    if (offset + 2 > body.Length) return;
                    var id = body[offset..(offset + 2)];
                    offset += 2;
                    if (qos == 1) Send(0x40, id); // PUBACK
                }
                Deliver(new Event.Message(topic, body[offset..]));
                break;
            default:
                break; // SUBACK, PINGRESP, PUBACK
        }
    }

    private void StartPings()
    {
        var interval = TimeSpan.FromSeconds(_config.KeepAliveSeconds);
        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    var silence = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastInboundTicks));
                    if (silence > interval * 1.5 + TimeSpan.FromSeconds(5))
                    {
                        Finish(new Failure.TimedOut());
                        return;
                    }
                    Send(0xC0, []);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void Send(byte type, byte[] body)
    {
        lock (_gate) if (_finished) return;
        _outbound.Writer.TryWrite(Packet(type, body));
    }

    private static byte[] Packet(byte type, byte[] body)
    {
        var packet = new List<byte>(body.Length + 5) { type };
        var length = body.Length;
        do
        {
            var b = (byte)(length % 128);
            length /= 128;
            if (length > 0) b |= 0x80;
            packet.Add(b);
        } while (length > 0);
        packet.AddRange(body);
        return [.. packet];
    }

    private static byte[] MqttString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var out_ = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(out_, (ushort)bytes.Length);
        bytes.CopyTo(out_, 2);
        return out_;
    }

    private ushort AllocatePacketId()
    {
        lock (_gate)
        {
            var id = _nextPacketId;
            _nextPacketId = _nextPacketId == ushort.MaxValue ? (ushort)1 : (ushort)(_nextPacketId + 1);
            return id;
        }
    }

    private void Finish(Failure failure, bool notify = true)
    {
        Socket? socket;
        SslStream? stream;
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            _open = false;
            socket = _socket;
            stream = _stream;
            _socket = null;
            _stream = null;
        }
        _stop.Cancel();
        _outbound.Writer.TryComplete();
        try { stream?.Dispose(); } catch (Exception) { }
        try { socket?.Dispose(); } catch (Exception) { }
        if (notify) Deliver(new Event.Disconnected(failure));
        _events?.Writer.TryComplete();
    }

    private void Deliver(Event e)
    {
        if (_events is not null) _events.Writer.TryWrite(e);
        else _context!.Post(_ => OnEvent?.Invoke(e), null);
    }
}
