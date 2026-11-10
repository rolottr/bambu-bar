using System.Buffers.Binary;
using System.Text;

namespace BambuBar.Core.Camera;

/// <summary>
/// The P1/A1 camera on port 6000. After an 80-byte login over TLS the printer streams JPEG frames, each behind a
/// 16-byte header that starts with the payload size (little-endian, three bytes used). Frames are handed on as JPEG
/// bytes; the app decodes them. A port of the Mac's JPEGCameraClient.swift.
/// </summary>
internal sealed class JpegCameraClient : ICameraClient
{
    public Action<CameraEvent>? OnEvent { get; set; }
    public Action<string>? Log { get; set; }

    private readonly CameraConfig _config;
    private readonly CameraQueue _queue;
    private readonly EventPump<CameraEvent> _events;
    private CameraSocket? _socket;
    private readonly JpegFrameReader _reader = new();
    private int _frames;
    private long _lastFrame = Environment.TickCount64;
    private Timer? _watchdog;
    private bool _finished;

    public JpegCameraClient(CameraConfig config)
    {
        _config = config;
        _events = new EventPump<CameraEvent>(e => OnEvent?.Invoke(e));
        _queue = new CameraQueue(e => Finish(new CameraFailure.Malformed(e.Message)));
    }

    public void Start() => _queue.Run(Open);

    public void Stop() => _queue.Run(() => Finish(null));

    private void Open()
    {
        if (_finished || _socket is not null) return;
        var socket = new CameraSocket(_config.Host, _config.Port ?? CameraTransport.Jpeg.Port(), _config.Serial, _config.Insecure,
            _queue, _config.OnCertificate)
        {
            Log = Log,
        };
        socket.OnReady = Login;
        socket.OnData = Received;
        socket.OnClose = failure =>
        {
            // The printer hangs up without a word when it doesn't like the login (or is busy with other viewers).
            if (failure is CameraFailure.Closed && _frames == 0)
            {
                Finish(new CameraFailure.Unreachable(Loc.T(
                    "The printer closed the camera connection before sending a picture. Check the access code, or close other apps that show this camera.")));
            }
            else
            {
                Finish(failure);
            }
        };
        _socket = socket;
        socket.Open();
        _lastFrame = Environment.TickCount64;
        _watchdog = new Timer(_ => _queue.Run(() =>
        {
            // Idle P1 printers send about one frame a second; give a slow start a little longer.
            if (!_finished && Environment.TickCount64 - _lastFrame > (_frames == 0 ? 15_000 : 12_000)) Finish(new CameraFailure.TimedOut());
        }), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void Login()
    {
        var packet = new byte[80];
        uint[] words = [0x40, 0x3000, 0, 0];
        for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4 * i), words[i]);
        string[] fields = ["bblp", _config.AccessCode];
        for (var i = 0; i < fields.Length; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(fields[i]);
            bytes.AsSpan(0, Math.Min(32, bytes.Length)).CopyTo(packet.AsSpan(16 + 32 * i));
        }
        Log?.Invoke("sent login");
        _socket?.Send(packet);
    }

    private void Received(byte[] data)
    {
        if (_reader.Append(data) is not { } jpegs)
        {
            Finish(new CameraFailure.Malformed("frame header"));
            return;
        }
        foreach (var jpeg in jpegs)
        {
            if (_finished) return;
            Take(jpeg);
        }
    }

    private void Take(byte[] jpeg)
    {
        if (JpegFrameReader.Size(jpeg) is not var (width, height))
        {
            Log?.Invoke($"skipped a frame that isn't a JPEG ({jpeg.Length} bytes)");
            return;
        }
        _frames++;
        _lastFrame = Environment.TickCount64;
        if (_frames == 1)
        {
            Log?.Invoke($"first frame: {width}×{height}, {jpeg.Length} bytes");
            Deliver(new CameraEvent.Started($"JPEG · {width}×{height}", width, height));
        }
        Deliver(new CameraEvent.Image(jpeg, width, height));
    }

    private void Finish(CameraFailure? failure)
    {
        if (_finished) return;
        _finished = true;
        _watchdog?.Dispose();
        _watchdog = null;
        if (_socket is not null) _socket.OnClose = null;
        _socket?.Close();
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
