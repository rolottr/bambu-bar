using BambuBar.Core.Camera;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests.Camera;

/// <summary>The camera's TLS rules against an in-process server, as <see cref="TlsAndMqttTests"/> checks them for MQTT:
/// the camera connection is trusted the same way, so it must follow the same rules. The Docker camera tests cover the
/// same against the mock camera.</summary>
public class CameraTlsTests
{
    private const string Serial = "01P00CLOOPBACK1";

    /// <summary>Waits for the client's first event, or <paramref name="until"/>, then stops it.</summary>
    private static async Task<CameraEvent?> RunAsync(int port, bool insecure, CameraTransport transport, Action<string?>? onCertificate = null,
        Task? until = null)
    {
        var done = new TaskCompletionSource<CameraEvent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CameraClient.Create(new CameraConfig
        {
            Host = "127.0.0.1", Port = port, Serial = Serial, AccessCode = "12345678", Transport = transport, Insecure = insecure,
            OnCertificate = leaf => onCertificate?.Invoke(BambuTrust.CommonName(leaf)),
        });
        client.OnEvent = e => done.TrySetResult(e);
        client.Start();
        var first = await Task.WhenAny(done.Task, until ?? Task.Delay(Timeout.Infinite, TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        client.OnEvent = null;
        client.Stop();
        return first == done.Task ? await done.Task : null;
    }

    /// <summary>Ledger §4.1: a resumed TLS session skips the certificate callback, and with it the serial check. Every
    /// RTSPS connection must do a full handshake and show the certificate.</summary>
    [Fact]
    public async Task EveryRtspsConnectionShowsTheCertificate()
    {
        var requests = 0;
        var describe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackTlsServer(Serial, async (tls, token) =>
        {
            // Wait for the DESCRIBE, then read until the client hangs up, so the session ends cleanly and could be resumed.
            var buffer = new byte[1024];
            while (await tls.ReadAsync(buffer, token) is > 0 and var n)
            {
                if (System.Text.Encoding.ASCII.GetString(buffer, 0, n).StartsWith("DESCRIBE ", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref requests);
                    describe.TrySetResult();
                }
            }
        });
        var names = new List<string?>();
        for (var i = 0; i < 3; i++)
        {
            describe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = await RunAsync(server.Port, insecure: true, CameraTransport.Rtsps, name => { lock (names) names.Add(name); },
                until: describe.Task);
            Assert.Null(failure);
        }
        Assert.Equal([Serial, Serial, Serial], names);
        Assert.Equal(3, server.Handshakes);
        Assert.Equal(3, requests);
    }

    /// <summary>A self-signed certificate isn't Bambu Lab's: refused before the access code leaves, on either port.</summary>
    [Theory]
    [InlineData(CameraTransport.Rtsps)]
    [InlineData(CameraTransport.Jpeg)]
    public async Task UntrustedCertificateIsRefusedBeforeTheCode(CameraTransport transport)
    {
        var received = 0;
        await using var server = new LoopbackTlsServer(Serial, async (tls, token) =>
        {
            var buffer = new byte[256];
            while (await tls.ReadAsync(buffer, token) is > 0 and var n) Interlocked.Add(ref received, n);
        });
        var failed = Assert.IsType<CameraEvent.Failed>(await RunAsync(server.Port, insecure: false, transport));
        var certificate = Assert.IsType<CameraFailure.Certificate>(failed.Failure);
        Assert.True(certificate.IsPermanent);
        Assert.StartsWith("The camera's certificate couldn't be verified: ", certificate.Message);
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task NothingListeningIsUnreachable()
    {
        using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
        probe.Dispose();
        var failed = Assert.IsType<CameraEvent.Failed>(await RunAsync(port, insecure: true, CameraTransport.Rtsps));
        var unreachable = Assert.IsType<CameraFailure.Unreachable>(failed.Failure);
        Assert.False(unreachable.IsPermanent);
    }

    [Theory]
    [InlineData("X1C", CameraTransport.Rtsps)]
    [InlineData("P2S", CameraTransport.Rtsps)]
    [InlineData("P1S", CameraTransport.Jpeg)]
    [InlineData("A1 mini", CameraTransport.Jpeg)]
    public void TransportForModel(string model, CameraTransport transport)
    {
        Assert.Equal(transport, CameraTransports.ForModel(model));
        Assert.Equal(transport == CameraTransport.Rtsps ? 322 : 6000, transport.Port());
    }
}
