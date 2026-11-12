using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BambuBar.Core.Tests;

/// <summary>A TLS 1.2 server on loopback with a self-signed certificate named like a printer. Each accepted connection
/// is handed to <c>serve</c>. The platform's TLS server keeps its session cache on, as a printer's does.</summary>
internal sealed class LoopbackTlsServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly Task _loop;

    public int Port { get; }
    public int Handshakes;

    public LoopbackTlsServer(string serial, Func<SslStream, CancellationToken, Task> serve)
    {
        _certificate = Create(serial);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    await using (var tls = new SslStream(client.GetStream()))
                    {
                        try
                        {
                            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                            {
                                ServerCertificate = _certificate,
                                EnabledSslProtocols = SslProtocols.Tls12,
                                ClientCertificateRequired = false,
                            }, _stop.Token);
                            Interlocked.Increment(ref Handshakes);
                            await serve(tls, _stop.Token);
                        }
                        catch (Exception) { /* The client hung up or refused the certificate. */ }
                    }
                });
            }
        });
    }

    private static X509Certificate2 Create(string serial)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={serial}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        // SChannel can't serve with an ephemeral key; a PKCS#12 round trip gives it a usable one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (Exception) { }
        _certificate.Dispose();
    }
}
