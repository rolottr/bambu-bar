using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Camera;

/// <summary>How a printer serves its camera on the LAN.</summary>
public enum CameraTransport
{
    /// <summary>RTSP over TLS on port 322 with H.264/H.265 video (X1, H2, P2S, X2D). Needs "LAN Only Liveview" on.</summary>
    Rtsps,
    /// <summary>A proprietary TLS stream of JPEG frames on port 6000 (P1, A1).</summary>
    Jpeg,
}

public static class CameraTransports
{
    public static CameraTransport ForModel(string modelName) => modelName switch
    {
        "P1P" or "P1S" or "A1" or "A1 mini" or "A2L" => CameraTransport.Jpeg,
        _ => CameraTransport.Rtsps,
    };

    public static int Port(this CameraTransport transport) => transport == CameraTransport.Rtsps ? 322 : 6000;
}

public sealed record CameraConfig
{
    public required string Host { get; init; }
    /// <summary>The printer serial, which its certificate is named after.</summary>
    public required string Serial { get; init; }
    public required string AccessCode { get; init; }
    public required CameraTransport Transport { get; init; }
    /// <summary>Accept any certificate (the local emulators only).</summary>
    public bool Insecure { get; init; }
    /// <summary>Another port than the transport's (tests).</summary>
    public int? Port { get; init; }
    /// <summary>Called with the camera's certificate on every handshake, before it's judged (tests, probe).</summary>
    public Action<X509Certificate2>? OnCertificate { get; init; }
}

public abstract record CameraFailure
{
    public sealed record Unauthorized : CameraFailure;
    public sealed record Certificate(string Reason) : CameraFailure;
    public sealed record Unreachable(string Reason) : CameraFailure;
    public sealed record TimedOut : CameraFailure;
    public sealed record Closed : CameraFailure;
    public sealed record Unsupported(string Reason) : CameraFailure;
    public sealed record Malformed(string Reason) : CameraFailure;

    /// <summary>Retrying won't help until something changes (the access code, the printer, the app).</summary>
    public bool IsPermanent => this is Unauthorized or Certificate or Unsupported;

    /// <summary>What to tell the person.</summary>
    public string Message => this switch
    {
        Unauthorized => Loc.T("The printer didn't accept the access code for the camera."),
        Certificate c => Loc.F("The camera's certificate couldn't be verified: %@", c.Reason),
        Unreachable u => u.Reason,
        TimedOut => Loc.T("The camera stopped sending video."),
        Closed => Loc.T("The printer closed the camera connection."),
        Unsupported u => u.Reason,
        Malformed m => Loc.F("The camera sent something unexpected (%@).", m.Reason),
        _ => GetType().Name,
    };
}

public abstract record CameraEvent
{
    /// <summary>The first frame is on its way: a short description of the stream ("H.264 · 1920×1080") and its
    /// picture size.</summary>
    public sealed record Started(string Description, int Width, int Height) : CameraEvent;
    /// <summary>A compressed video frame (an access unit in Annex-B) for the decoder.</summary>
    public sealed record Video(VideoFrame Frame) : CameraEvent
    {
        public bool IsKeyframe => Frame.IsKeyframe;
    }
    /// <summary>A still from the JPEG stream, not decoded; its size is read from its header.</summary>
    public sealed record Image(byte[] Jpeg, int Width, int Height) : CameraEvent;
    public sealed record Failed(CameraFailure Failure) : CameraEvent;
}

/// <summary>
/// One connection to a printer's camera, used once: <see cref="Start"/>, then <see cref="Stop"/> or a
/// <see cref="CameraEvent.Failed"/>. Events are delivered on the <see cref="SynchronizationContext"/> the client was
/// created on (the UI thread in the app) or, without one, on a background reader, in order. <see cref="Log"/> is called
/// on the client's own queue (for the probe). Start and Stop may be called from any thread.
/// </summary>
public interface ICameraClient
{
    Action<CameraEvent>? OnEvent { get; set; }
    Action<string>? Log { get; set; }
    void Start();
    void Stop();
}

public static class CameraClient
{
    public static ICameraClient Create(CameraConfig config) => config.Transport switch
    {
        CameraTransport.Rtsps => new RtspCameraClient(config),
        _ => new JpegCameraClient(config),
    };
}

/// <summary>Runs posted work one item at a time, in order, on the thread pool: a camera client's own queue, like the
/// Mac's DispatchQueue. Work posted after <see cref="Close"/> is dropped.</summary>
internal sealed class CameraQueue : SynchronizationContext
{
    private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>(new() { SingleReader = true });

    public CameraQueue(Action<Exception> onError)
    {
        _ = Task.Run(async () =>
        {
            var previous = Current;
            await foreach (var work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                SetSynchronizationContext(this);
                try { work(); }
                catch (Exception e) { onError(e); }
                finally { SetSynchronizationContext(previous); }
            }
        });
    }

    public void Run(Action work) => _work.Writer.TryWrite(work);

    public override void Post(SendOrPostCallback d, object? state) => Run(() => d(state));

    public override SynchronizationContext CreateCopy() => this;

    public void Close() => _work.Writer.TryComplete();
}

/// <summary>
/// A TLS connection to one of the printer's camera ports, trusted the same way as MQTT (see <see cref="BambuTrust"/>):
/// TLS 1.2 only, no session resumption, the certificate checked on every connection. Every callback runs on the
/// queue passed in.
/// </summary>
internal sealed class CameraSocket
{
    public Action? OnReady { get; set; }
    public Action<byte[]>? OnData { get; set; }
    /// <summary>Called once, unless the owner closed the socket itself.</summary>
    public Action<CameraFailure>? OnClose { get; set; }
    public Action<string>? Log { get; set; }

    private readonly string _host;
    private readonly int _port;
    private readonly string _serial;
    private readonly bool _insecure;
    private readonly Action<X509Certificate2>? _onCertificate;
    private readonly CameraQueue _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    private readonly object _gate = new();
    private Socket? _socket;
    private SslStream? _stream;
    private Task? _writer;
    private BambuTrust.Verdict? _verdict;
    // Read and written on the queue only.
    private bool _closed;

    public CameraSocket(string host, int port, string serial, bool insecure, CameraQueue queue,
        Action<X509Certificate2>? onCertificate = null)
    {
        _host = host;
        _port = port;
        _serial = serial;
        _insecure = insecure;
        _queue = queue;
        _onCertificate = onCertificate;
    }

    /// <summary>Call on the queue.</summary>
    public void Open(TimeSpan? timeout = null)
    {
        Log?.Invoke($"connecting to {_host}:{_port}");
        var token = _stop.Token;
        var connectTimeout = timeout ?? TimeSpan.FromSeconds(10);
        _ = Task.Run(() => RunAsync(connectTimeout, token), CancellationToken.None);
    }

    public void Send(byte[] data)
    {
        if (_closed) return;
        _outbound.Writer.TryWrite(data);
    }

    /// <summary>Sends a last message, then closes without reporting it. Call on the queue.</summary>
    public void Close(byte[]? last = null)
    {
        if (_closed) return;
        _closed = true;
        Task? writer;
        lock (_gate) writer = _writer;
        if (last is not null && writer is not null)
        {
            _outbound.Writer.TryWrite(last);
            _outbound.Writer.TryComplete();
            _ = writer.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => Teardown(), TaskScheduler.Default);
        }
        else
        {
            _outbound.Writer.TryComplete();
            Teardown();
        }
    }

    private void Teardown()
    {
        Socket? socket;
        SslStream? stream;
        lock (_gate)
        {
            socket = _socket;
            stream = _stream;
            _socket = null;
            _stream = null;
        }
        _stop.Cancel();
        try { stream?.Dispose(); } catch (Exception) { }
        try { socket?.Dispose(); } catch (Exception) { }
    }

    private async Task RunAsync(TimeSpan connectTimeout, CancellationToken token)
    {
        try
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            lock (_gate)
            {
                if (token.IsCancellationRequested) { socket.Dispose(); return; }
                _socket = socket;
            }
            EnableKeepAlive(socket);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(connectTimeout);
                try
                {
                    await socket.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Fail(new CameraFailure.Unreachable(MqttClient.Describe(SocketError.TimedOut, "timed out")));
                    return;
                }
            }
            var stream = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
            lock (_gate)
            {
                if (token.IsCancellationRequested) { stream.Dispose(); return; }
                _stream = stream;
            }
            await stream.AuthenticateAsClientAsync(MqttClient.TlsOptions(_host, Verify), token).ConfigureAwait(false);
            var writer = Task.Run(() => WriteLoopAsync(stream, token), CancellationToken.None);
            lock (_gate) _writer = writer;
            _queue.Run(() =>
            {
                if (_closed) return;
                Log?.Invoke($"connected to port {_port}");
                OnReady?.Invoke();
            });
            await ReadLoopAsync(stream, token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Closed by the owner.
        }
        catch (AuthenticationException e)
        {
            Fail(_verdict is BambuTrust.Verdict.Rejected rejected
                ? new CameraFailure.Certificate(rejected.Reason)
                : new CameraFailure.Unreachable(e.Message));
        }
        catch (SocketException e)
        {
            Fail(new CameraFailure.Unreachable(MqttClient.Describe(e.SocketErrorCode, e.Message)));
        }
        catch (IOException e)
        {
            Fail(_verdict is BambuTrust.Verdict.Rejected rejected
                ? new CameraFailure.Certificate(rejected.Reason)
                : new CameraFailure.Unreachable(e.InnerException is SocketException se
                    ? MqttClient.Describe(se.SocketErrorCode, se.Message)
                    : e.Message));
        }
        catch (ObjectDisposedException)
        {
            Fail(new CameraFailure.Closed());
        }
    }

    private bool Verify(X509Certificate2? leaf, IEnumerable<X509Certificate2> presented)
    {
        if (leaf is null)
        {
            _verdict = new BambuTrust.Verdict.Rejected(Loc.T("The printer sent no certificate."));
            return false;
        }
        _onCertificate?.Invoke(leaf);
        if (_insecure) return true;
        var verdict = BambuTrust.Evaluate(leaf, presented, _serial);
        _verdict = verdict;
        _queue.Run(() => Log?.Invoke($"certificate on port {_port}: {verdict}"));
        return !verdict.IsRejected;
    }

    private async Task ReadLoopAsync(SslStream stream, CancellationToken token)
    {
        var chunk = new byte[512 * 1024];
        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0)
            {
                Fail(new CameraFailure.Closed());
                return;
            }
            var data = chunk.AsSpan(0, read).ToArray();
            _queue.Run(() =>
            {
                if (!_closed) OnData?.Invoke(data);
            });
        }
    }

    private async Task WriteLoopAsync(SslStream stream, CancellationToken token)
    {
        try
        {
            await foreach (var data in _outbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await stream.WriteAsync(data, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
        {
            if (!token.IsCancellationRequested) Fail(new CameraFailure.Unreachable(e.Message));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Reports a failure on the queue, unless the owner closed the socket first.</summary>
    private void Fail(CameraFailure failure) => _queue.Run(() =>
    {
        if (_closed) return;
        Close();
        OnClose?.Invoke(failure);
    });

    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        }
        catch (SocketException) { }
        catch (PlatformNotSupportedException) { }
    }
}
