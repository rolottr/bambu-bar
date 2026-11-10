using System.Globalization;
using System.Text;

namespace BambuBar.Core.Camera;

/// <summary>
/// Plays the RTSPS live view of X1, H2, P2S and X2D printers (port 322, <c>bblp</c> and the access code).
///
/// RTSP runs over the TLS connection and so does the video: RTP is interleaved on the same connection (<c>$</c>,
/// channel, 16-bit length, packet), which avoids UDP ports and firewalls. DESCRIBE → SETUP → PLAY, then a keepalive
/// request every 20 seconds and TEARDOWN when done. A port of the Mac's RTSPCameraClient.swift.
/// </summary>
internal sealed class RtspCameraClient : ICameraClient
{
    public Action<CameraEvent>? OnEvent { get; set; }
    public Action<string>? Log { get; set; }

    private readonly CameraConfig _config;
    private readonly CameraQueue _queue;
    private readonly EventPump<CameraEvent> _events;
    private readonly string _url;
    private readonly int _port;
    private CameraSocket? _socket;
    private readonly RtspStreamReader _reader = new();
    private int _sequence;
    /// <summary>Requests awaiting a response, by CSeq.</summary>
    private readonly Dictionary<int, (string Method, string Uri, string[] Headers)> _requests = [];
    private RtspAuth? _auth;
    private string? _session;
    private byte _videoChannel;
    private VideoDepacketizer? _depacketizer;
    private string _keepAliveMethod = "GET_PARAMETER";
    private Timer? _keepAlive;
    private Timer? _watchdog;
    private bool _playing;
    private bool _started;
    private int _frames;
    private long _lastPacket = Environment.TickCount64;
    private long _openedAt = Environment.TickCount64;
    private bool _finished;

    public RtspCameraClient(CameraConfig config)
    {
        _config = config;
        _port = config.Port ?? CameraTransport.Rtsps.Port();
        _url = $"rtsps://{config.Host}:{CameraTransport.Rtsps.Port()}/streaming/live/1";
        _events = new EventPump<CameraEvent>(e => OnEvent?.Invoke(e));
        _queue = new CameraQueue(e => Finish(new CameraFailure.Malformed(e.Message)));
    }

    public void Start() => _queue.Run(Open);

    public void Stop() => _queue.Run(() => Finish(null));

    // MARK: Connection

    private void Open()
    {
        if (_finished || _socket is not null) return;
        var socket = new CameraSocket(_config.Host, _port, _config.Serial, _config.Insecure, _queue, _config.OnCertificate)
        {
            Log = Log,
        };
        socket.OnReady = () => Send("DESCRIBE", _url, "Accept: application/sdp");
        socket.OnData = Received;
        socket.OnClose = Finish;
        _socket = socket;
        _openedAt = Environment.TickCount64;
        _lastPacket = Environment.TickCount64;
        socket.Open();

        _watchdog = new Timer(_ => _queue.Run(() =>
        {
            if (_finished) return;
            if (_playing)
            {
                if (Environment.TickCount64 - _lastPacket > 10_000) Finish(new CameraFailure.TimedOut());
            }
            else if (Environment.TickCount64 - _openedAt > 20_000)
            {
                Finish(new CameraFailure.TimedOut());
            }
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void Send(string method, string uri, params string[] headers)
    {
        var message = Request(method, uri, headers);
        _requests[_sequence] = (method, uri, headers);
        Log?.Invoke($"> {method} {uri}");
        _socket?.Send(message);
    }

    /// <summary>The next request, numbered and signed.</summary>
    private byte[] Request(string method, string uri, params string[] headers)
    {
        _sequence++;
        var lines = new List<string> { $"{method} {uri} RTSP/1.0", $"CSeq: {_sequence}", "User-Agent: Bambu Bar" };
        if (_auth?.Header(method, uri) is { } header) lines.Add($"Authorization: {header}");
        if (_session is not null) lines.Add($"Session: {_session}");
        lines.AddRange(headers);
        return Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n");
    }

    // MARK: Parsing

    private void Received(byte[] data)
    {
        List<RtspStreamReader.Item> items;
        try
        {
            items = _reader.Append(data);
        }
        catch (RtspStreamException e)
        {
            // A length that makes no sense: the camera's stream can't be followed any more.
            Finish(new CameraFailure.Malformed(e.Failure.ToString()));
            return;
        }
        foreach (var item in items)
        {
            if (_finished) return;
            switch (item)
            {
                case RtspStreamReader.Item.Packet p:
                    if (p.Channel == _videoChannel && p.Data.Length >= 12) HandleRtp(p.Data);
                    break;
                case RtspStreamReader.Item.Message m:
                    if (m.Head.IsResponse) HandleResponse(m.Head, Encoding.UTF8.GetString(m.Body));
                    break;
            }
        }
    }

    private void HandleResponse(RtspMessage response, string body)
    {
        if (!int.TryParse(response.Header("CSeq"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cseq)
            || !_requests.Remove(cseq, out var request))
        {
            return;
        }
        Log?.Invoke($"< {response.StatusLine} ({request.Method})");
        if (request.Method == _keepAliveMethod && _playing)
        {
            // Some servers don't implement GET_PARAMETER; OPTIONS keeps the session alive too.
            if (response.Status != 200 && _keepAliveMethod == "GET_PARAMETER") _keepAliveMethod = "OPTIONS";
            return;
        }
        if (response.Status == 401)
        {
            if (_auth?.HasFailed == true)
            {
                Finish(new CameraFailure.Unauthorized());
                return;
            }
            if (RtspAuth.Create(response.Headers("WWW-Authenticate"), "bblp", _config.AccessCode) is not { } next)
            {
                Finish(new CameraFailure.Unauthorized());
                return;
            }
            // Credentials sent once and refused again mean the code is wrong.
            if (_auth is not null) next.HasFailed = true;
            _auth = next;
            Send(request.Method, request.Uri, request.Headers);
            return;
        }
        if (response.Status != 200)
        {
            if (response.Status is 404 or 454 or 503)
            {
                Finish(new CameraFailure.Unreachable(
                    Loc.F("The camera isn't streaming (%@). Is LAN Only Liveview on?", response.StatusLine)));
                return;
            }
            Finish(new CameraFailure.Malformed($"{request.Method} answered {response.StatusLine}"));
            return;
        }
        switch (request.Method)
        {
            case "DESCRIBE":
                Log?.Invoke("SDP:\n" + body);
                if (Sdp.VideoTrack(body) is not { } video)
                {
                    Finish(new CameraFailure.Unsupported(Loc.T("The camera offers no H.264 or H.265 video.")));
                    return;
                }
                Log?.Invoke($"video: {video.Codec.DisplayName()}, payload type {video.PayloadType}, control {video.Control ?? "-"}");
                _depacketizer = new VideoDepacketizer(video.Codec, video.ParameterSets);
                var @base = response.Header("Content-Base") ?? response.Header("Content-Location") ?? _url;
                Send("SETUP", Sdp.Resolve(video.Control, @base), "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
                break;
            case "SETUP":
                if (response.Header("Session") is not { } value)
                {
                    Finish(new CameraFailure.Malformed("SETUP without a session"));
                    return;
                }
                _session = value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Spaces.Trim).FirstOrDefault();
                if (response.Header("Transport") is { } transport && transport.IndexOf("interleaved=", StringComparison.Ordinal) is >= 0 and var at)
                {
                    var digits = new string(transport[(at + "interleaved=".Length)..].TakeWhile(char.IsDigit).ToArray());
                    if (byte.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var channel)) _videoChannel = channel;
                }
                var timeout = 60;
                foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Spaces.Trim(part).StartsWith("timeout=", StringComparison.Ordinal)) continue;
                    var last = part.Split('=', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                    timeout = int.TryParse(last, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var t) ? t : 60;
                }
                Send("PLAY", _url, "Range: npt=0.000-");
                StartKeepAlive(TimeSpan.FromSeconds(Math.Max(5, Math.Min(20, timeout / 2.0))));
                break;
            case "PLAY":
                _playing = true;
                _lastPacket = Environment.TickCount64;
                break;
        }
    }

    private void HandleRtp(byte[] packet)
    {
        if (packet[0] >> 6 != 2 || _depacketizer is not { } depacketizer) return;
        _lastPacket = Environment.TickCount64;
        var csrcs = packet[0] & 0x0F;
        var start = 12 + 4 * csrcs;
        if ((packet[0] & 0x10) != 0) // header extension
        {
            if (packet.Length < start + 4) return;
            start += 4 + 4 * (packet[start + 2] << 8 | packet[start + 3]);
        }
        var end = packet.Length;
        if ((packet[0] & 0x20) != 0) end -= packet[^1];
        if (start >= end) return;
        var marker = (packet[1] & 0x80) != 0;
        var sequence = (ushort)(packet[2] << 8 | packet[3]);
        var timestamp = (uint)packet[4] << 24 | (uint)packet[5] << 16 | (uint)packet[6] << 8 | packet[7];
        foreach (var frame in depacketizer.Push(packet.AsSpan(start, end - start), sequence, timestamp, marker))
        {
            _frames++;
            if (!_started && depacketizer.Format is { } format)
            {
                _started = true;
                Log?.Invoke($"first keyframe: {depacketizer.Codec.DisplayName()} {format.DimensionsText}");
                Deliver(new CameraEvent.Started($"{depacketizer.Codec.DisplayName()} · {format.DimensionsText}", format.Width, format.Height));
            }
            Deliver(new CameraEvent.Video(frame));
        }
    }

    private void StartKeepAlive(TimeSpan interval)
    {
        _keepAlive?.Dispose();
        _keepAlive = new Timer(_ => _queue.Run(() =>
        {
            if (_finished || !_playing) return;
            Send(_keepAliveMethod, _url);
        }), null, interval, interval);
    }

    private void Finish(CameraFailure? failure)
    {
        if (_finished) return;
        _finished = true;
        _keepAlive?.Dispose();
        _watchdog?.Dispose();
        _keepAlive = null;
        _watchdog = null;
        if (_socket is not null) _socket.OnClose = null;
        if (failure is null && _session is not null)
        {
            // Tell the printer we're gone; it serves only a few viewers at a time.
            _socket?.Close(Request("TEARDOWN", _url));
        }
        else
        {
            _socket?.Close();
        }
        _socket = null;
        if (failure is not null)
        {
            Log?.Invoke($"closed: {failure}");
            Deliver(new CameraEvent.Failed(failure));
        }
        _events.Complete();
        _queue.Close();
    }

    private void Deliver(CameraEvent e) => _events.Post(e);
}
