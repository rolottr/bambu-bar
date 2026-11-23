using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BambuBar.Linux;

/// <summary>
/// Private main-window transport. Unlike DesktopPanel, this channel can carry camera images and
/// user-entered secrets. The socket is owner-only, requests are bounded, and nothing is logged.
/// Desktop companions continue to use the separate sanitized D-Bus interface.
/// </summary>
internal sealed class NativeUiServer : IDisposable
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<JsonElement, Task> _request;
    private readonly Action _connected, _disconnected;
    private readonly HashSet<Client> _clients = [];
    private readonly string _path;
    public bool Connected => _clients.Count > 0;

    public NativeUiServer(Func<JsonElement, Task> request, Action connected, Action disconnected, string suffix = "")
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        _request = request; _connected = connected; _disconnected = disconnected;
        // The app runtime directory is shared with the host and stays below Unix socket path limits.
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? throw new IOException("No desktop runtime directory.");
        var directory = Path.Combine(runtime, "app", Platform.AppId, "native");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(directory, "ui" + suffix + ".sock");
        if (File.Exists(_path)) File.Delete(_path); // GApplication has already acquired the instance name.
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(4);
        _ = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var socket = await _listener.AcceptAsync(_lifetime.Token);
                if (_clients.Count >= 4) { socket.Dispose(); continue; }
                var client = new Client(socket);
                _clients.Add(client);
                _connected();
                _ = Serve(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task Serve(Client client)
    {
        var writer = Write(client);
        try
        {
            var buffer = new byte[4096];
            var pending = new List<byte>();
            while (!_lifetime.IsCancellationRequested)
            {
                var read = await client.Stream.ReadAsync(buffer, _lifetime.Token);
                if (read == 0) break;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != 10)
                    {
                        if (pending.Count >= 2 * 1024 * 1024) throw new IOException("Request too large");
                        pending.Add(buffer[i]);
                        continue;
                    }
                    using var document = JsonDocument.Parse(pending.ToArray());
                    pending.Clear();
                    await _request(document.RootElement);
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or JsonException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            client.Dispose();
            _clients.Remove(client);
            if (!Connected) _disconnected();
            await writer;
        }
    }

    private async Task Write(Client client)
    {
        try
        {
            await foreach (var _ in client.Wake.Reader.ReadAllAsync(_lifetime.Token))
                while (client.Take() is {} message)
                    await client.Stream.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), _lifetime.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { client.Dispose(); }
    }

    public void Send(object message, string? replaceable = null)
    {
        var json = JsonSerializer.Serialize(message);
        foreach (var client in _clients.ToArray()) client.Enqueue(json, replaceable);
    }

    public void Dispose()
    {
        _lifetime.Cancel(); _listener.Dispose();
        foreach (var client in _clients.ToArray()) client.Dispose();
        _clients.Clear();
        File.Delete(_path);
    }

    private sealed class Client(Socket socket) : IDisposable
    {
        public NetworkStream Stream { get; } = new(socket, ownsSocket: true);
        // Coalesce video and snapshots independently. They must never evict quit,
        // hide or capture messages, even when a camera client reads slowly.
        public Channel<bool> Wake { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly Queue<string> _controls = [];
        private string? _state, _frame;
        public void Enqueue(string message, string? replaceable)
        {
            if (replaceable == "state") _state = message;
            else if (replaceable == "camera") _frame = message;
            else if (_controls.Count < 16) _controls.Enqueue(message);
            else { Dispose(); return; }
            Wake.Writer.TryWrite(true);
        }
        public string? Take()
        {
            if (_controls.TryDequeue(out var control)) return control;
            if (_state is {} state) { _state = null; return state; }
            if (_frame is {} frame) { _frame = null; return frame; }
            return null;
        }
        public void Dispose() { Wake.Writer.TryComplete(); Stream.Dispose(); }
    }
}
