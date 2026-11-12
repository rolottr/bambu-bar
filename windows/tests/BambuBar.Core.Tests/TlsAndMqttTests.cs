using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests;

/// <summary>The TLS and MQTT rules against an in-process server, so they run on every CI platform (on Windows, against
/// SChannel itself). The Docker fleet tests cover the same against mosquitto.</summary>
public class TlsAndMqttTests
{
    private const string Serial = "22E00LOOPBACK01";

    /// <summary>Ledger §4.1: a resumed TLS session skips the certificate callback, and with it the serial check. Every
    /// connection must do a full handshake and show the certificate.</summary>
    [Fact]
    public async Task EveryConnectionShowsTheCertificate()
    {
        await using var server = new LoopbackTlsServer(Serial, async (tls, token) =>
        {
            // Read until the client hangs up, so the session ends cleanly and could be resumed.
            var buffer = new byte[64];
            while (await tls.ReadAsync(buffer, token) > 0) { }
        });
        var names = new List<string?>();
        for (var i = 0; i < 3; i++)
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync("127.0.0.1", server.Port, TestContext.Current.CancellationToken);
            await using var tls = new SslStream(new NetworkStream(socket, ownsSocket: false));
            await tls.AuthenticateAsClientAsync(MqttClient.TlsOptions("127.0.0.1", (leaf, _) =>
            {
                names.Add(leaf is null ? null : BambuTrust.CommonName(leaf));
                return true;
            }), TestContext.Current.CancellationToken);
            Assert.Equal(System.Security.Authentication.SslProtocols.Tls12, tls.SslProtocol);
            await tls.ShutdownAsync();
        }
        Assert.Equal([Serial, Serial, Serial], names);
    }

    /// <summary>A self-signed certificate isn't Bambu Lab's: refused before the access code leaves.</summary>
    [Fact]
    public async Task UntrustedCertificateIsRefusedBeforeTheCode()
    {
        var received = 0;
        await using var server = new LoopbackTlsServer(Serial, async (tls, token) =>
        {
            var buffer = new byte[256];
            while (await tls.ReadAsync(buffer, token) is > 0 and var n) Interlocked.Add(ref received, n);
        });
        var failure = await Connect(server.Port, insecure: false);
        Assert.IsType<MqttClient.Failure.Certificate>(failure.Failure);
        Assert.Equal(0, received);
    }

    /// <summary>A minimal broker: CONNACK with <paramref name="code"/>, then on SUBSCRIBE a QoS 1 PUBLISH of
    /// <paramref name="payload"/>; it records the packet types it receives.</summary>
    private static Func<SslStream, CancellationToken, Task> Broker(byte code, string payload, ConcurrentQueue<int> seen) => async (tls, token) =>
    {
        while (!token.IsCancellationRequested)
        {
            var (type, body) = await ReadPacket(tls, token);
            if (type < 0) return;
            seen.Enqueue(type);
            switch (type)
            {
                case 1: // CONNECT
                    Assert.Equal("MQTT", Encoding.UTF8.GetString(body, 2, 4));
                    Assert.Equal(0xC2, body[7]);
                    await tls.WriteAsync(new byte[] { 0x20, 2, 0, code }, token);
                    if (code != 0) return;
                    break;
                case 8: // SUBSCRIBE
                    var topic = Encoding.UTF8.GetBytes($"device/{Serial}/report");
                    var message = Encoding.UTF8.GetBytes(payload);
                    var publish = new List<byte> { (byte)(topic.Length >> 8), (byte)topic.Length };
                    publish.AddRange(topic);
                    publish.AddRange(new byte[] { 0, 7 }); // packet id: QoS 1 wants a PUBACK
                    publish.AddRange(message);
                    await tls.WriteAsync(Frame(0x32, [.. publish]), token);
                    break;
            }
        }
    };

    private static byte[] Frame(byte header, byte[] body)
    {
        var length = new List<byte>();
        var n = body.Length;
        do { var b = (byte)(n % 128); n /= 128; if (n > 0) b |= 0x80; length.Add(b); } while (n > 0);
        return [header, .. length, .. body];
    }

    private static async Task<(int Type, byte[] Body)> ReadPacket(SslStream tls, CancellationToken token)
    {
        var one = new byte[1];
        if (await tls.ReadAsync(one, token) == 0) return (-1, []);
        var type = one[0] >> 4;
        int length = 0, multiplier = 1;
        do
        {
            if (await tls.ReadAsync(one, token) == 0) return (-1, []);
            length += (one[0] & 0x7F) * multiplier;
            multiplier *= 128;
        } while ((one[0] & 0x80) != 0);
        var body = new byte[length];
        await tls.ReadExactlyAsync(body, token);
        return (type, body);
    }

    private static async Task<(MqttClient.Failure? Failure, List<MqttClient.Event> Events)> Connect(int port, bool insecure,
        Action<MqttClient>? onConnected = null, int waitForMessages = 0)
    {
        var events = new List<MqttClient.Event>();
        var done = new TaskCompletionSource<MqttClient.Failure?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MqttClient(new() { Host = "127.0.0.1", Port = port, Password = "12345678", Serial = Serial, Insecure = insecure });
        client.OnEvent = e =>
        {
            lock (events) events.Add(e);
            switch (e)
            {
                case MqttClient.Event.Connected:
                    onConnected?.Invoke(client);
                    if (waitForMessages == 0) done.TrySetResult(null);
                    break;
                case MqttClient.Event.Message when events.OfType<MqttClient.Event.Message>().Count() >= waitForMessages:
                    done.TrySetResult(null);
                    break;
                case MqttClient.Event.Disconnected d:
                    done.TrySetResult(d.Failure);
                    break;
            }
        };
        client.Connect();
        var failure = await done.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        client.OnEvent = null;
        client.Disconnect();
        lock (events) return (failure, events.ToList());
    }

    [Fact]
    public async Task ConnectsSubscribesAndAcknowledgesQos1()
    {
        var seen = new ConcurrentQueue<int>();
        await using var server = new LoopbackTlsServer(Serial, Broker(0, """{"print":{"gcode_state":"RUNNING","msg":0}}""", seen));
        var (failure, events) = await Connect(server.Port, insecure: true,
            onConnected: c => c.Subscribe($"device/{Serial}/report"), waitForMessages: 1);
        Assert.Null(failure);
        var message = Assert.Single(events.OfType<MqttClient.Event.Message>());
        Assert.Equal($"device/{Serial}/report", message.Topic);
        Assert.Equal("""{"print":{"gcode_state":"RUNNING","msg":0}}""", Encoding.UTF8.GetString(message.Payload));
        // CONNECT, SUBSCRIBE, then the PUBACK for the QoS 1 message.
        await WaitFor(() => seen.Count >= 3);
        Assert.Equal([1, 8, 4], seen.Take(3));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RefusedCodeIsAnAuthenticationFailure(byte code)
    {
        await using var server = new LoopbackTlsServer(Serial, Broker(code, "", new()));
        var (failure, _) = await Connect(server.Port, insecure: true);
        var refused = Assert.IsType<MqttClient.Failure.Refused>(failure);
        Assert.Equal(code, refused.Code);
        Assert.True(refused.IsAuthentication);
    }

    [Fact]
    public async Task NothingListeningIsUnreachable()
    {
        using var probe = new Socket(SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
        probe.Dispose();
        var (failure, _) = await Connect(port, insecure: true);
        Assert.IsType<MqttClient.Failure.Unreachable>(failure);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
    }
}
