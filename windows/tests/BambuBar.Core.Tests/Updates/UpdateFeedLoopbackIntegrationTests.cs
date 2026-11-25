using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BambuBar.Core.App;
using BambuBar.Core.Licensing;
using BambuBar.Core.Updates;

namespace BambuBar.Core.Tests.Updates;

/// <summary>
/// Live loopback TCP HTTP/1.1 tests exercising the real compiled <see cref="UpdateFeed"/> (with its real
/// <see cref="SocketsHttpHandler"/>, <c>AllowAutoRedirect = false</c>) and <see cref="UpdateChecker"/> over real
/// <c>127.0.0.1</c> sockets — zero mocked HTTP handlers.
/// </summary>
public sealed class UpdateFeedLoopbackIntegrationTests : IDisposable
{
    private static readonly LicenseRecord ActiveLicense = new("BB-LIVE-KEY-9999", "act-live-001", "inst-live-002", DateTimeOffset.UtcNow);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"bambubar-loopback-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed record CapturedHttpRequest(string Method, string Target, Dictionary<string, string> Headers);

    /// <summary>Minimal real HTTP/1.1 server over <see cref="TcpListener"/> on <c>127.0.0.1:0</c>.</summary>
    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<CapturedHttpRequest, (int Status, string Reason, Dictionary<string, string> Headers, byte[] Body)> _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public int Port { get; }
        public ConcurrentQueue<CapturedHttpRequest> Requests { get; } = new();

        public LoopbackHttpServer(Func<CapturedHttpRequest, (int Status, string Reason, Dictionary<string, string> Headers, byte[] Body)> handler)
        {
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                try
                {
                    var raw = new MemoryStream();
                    var buffer = new byte[4096];
                    int headerEnd = -1;
                    while (headerEnd < 0)
                    {
                        var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                        if (read == 0) return;
                        raw.Write(buffer, 0, read);
                        headerEnd = FindHeaderEnd(raw.GetBuffer(), (int)raw.Length);
                    }
                    var text = Encoding.ASCII.GetString(raw.GetBuffer(), 0, headerEnd);
                    var lines = text.Split("\r\n");
                    var parts = lines[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines.Skip(1))
                    {
                        var colon = line.IndexOf(':');
                        if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                    var captured = new CapturedHttpRequest(parts[0], parts[1], headers);
                    Requests.Enqueue(captured);

                    var (status, reason, respHeaders, body) = _handler(captured);
                    var sb = new StringBuilder();
                    sb.Append($"HTTP/1.1 {status} {reason}\r\n");
                    sb.Append($"Content-Length: {body.Length}\r\n");
                    sb.Append("Connection: close\r\n");
                    foreach (var (k, v) in respHeaders) sb.Append($"{k}: {v}\r\n");
                    sb.Append("\r\n");
                    var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
                    await stream.WriteAsync(headBytes, _cts.Token).ConfigureAwait(false);
                    if (body.Length > 0) await stream.WriteAsync(body, _cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_cts.IsCancellationRequested) { }
            }
        }

        private static int FindHeaderEnd(byte[] bytes, int length)
        {
            for (var i = 0; i <= length - 4; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }

    private static byte[] SignedAppcast(string version, string downloadUrl, byte[] payload) =>
        Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:sparkle="{Appcast.SparkleNamespace}">
              <channel>
                <title>Bambu Bar</title>
                <link>https://bambubar.app</link>
                <item>
                  <title>Bambu Bar {version}</title>
                  <sparkle:version>{version}</sparkle:version>
                  <sparkle:shortVersionString>{version}</sparkle:shortVersionString>
                  <sparkle:minimumSystemVersion>10.0.18362</sparkle:minimumSystemVersion>
                  <enclosure url="{System.Security.SecurityElement.Escape(downloadUrl)}" sparkle:os="windows-x64" type="application/octet-stream" length="{payload.Length}" sparkle:edSignature="{UpdateSignatureTests.PayloadSignature}" />
                </item>
              </channel>
            </rss>
            """);

    [Fact]
    public async Task LiveLoopbackUnlicensedFlowChecksAnonymouslyAndMakesZeroDownloadRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        await using var cdn = new LoopbackHttpServer(_ => (200, "OK", new(), payload));
        LoopbackHttpServer? api = null;
        api = new LoopbackHttpServer(req => req.Target switch
        {
            "/appcast.xml" => (200, "OK", new() { ["Content-Type"] = "application/xml" },
                SignedAppcast("3.1.1", $"http://127.0.0.1:{api!.Port}/api/updates/download?version=3.1.1&os=windows-x64", payload)),
            _ => (401, "Unauthorized", new(), []),
        });
        await using (api)
        {
            var feedUrl = new Uri($"http://127.0.0.1:{api.Port}/appcast.xml");
            var feed = new UpdateFeed(handler: null, url: feedUrl, licenseHeaders: () => null, allowAnyFeedOverrideHost: false);
            var checker = new UpdateChecker(() => false, new AppSettings(null), feed, _folder, "3.1.0",
                UpdatePlatform.WindowsX64, new Version(10, 0, 26100), new UpdateSignature(UpdateSignatureTests.TestPublicKey));

            checker.Start();
            await checker.CheckAsync(ct);

            Assert.Equal(UpdateState.Available, checker.State);
            Assert.Equal("3.1.1", checker.Available?.DisplayVersion);
            Assert.Equal("3.1.1", checker.Offered?.DisplayVersion);

            // Attempt download while unlicensed: blocked with zero HTTP download requests.
            Assert.Null(await checker.DownloadAsync(ct));
            Assert.Equal(UpdateState.Failed, checker.State);
            Assert.Equal("Activate your license to download updates.", checker.Error);

            var apiReq = Assert.Single(api.Requests);
            Assert.Equal("/appcast.xml", apiReq.Target);
            Assert.DoesNotContain(apiReq.Headers.Keys, k => k.StartsWith("X-BambuBar-", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(cdn.Requests);
        }
    }

    [Fact]
    public async Task LiveLoopbackLicensedFlowSendsHeadersToDownloadEndpointAndStripsThemAcross302Redirect()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        await using var cdn = new LoopbackHttpServer(req => req.Target == "/assets/Bambu-Bar-3.1.1-windows-x64.exe"
            ? (200, "OK", new() { ["Content-Type"] = "application/octet-stream" }, payload)
            : (404, "Not Found", new(), []));

        LoopbackHttpServer? api = null;
        api = new LoopbackHttpServer(req =>
        {
            if (req.Target == "/appcast.xml")
            {
                var dl = $"http://127.0.0.1:{api!.Port}/api/updates/download?version=3.1.1&os=windows-x64";
                return (200, "OK", new() { ["Content-Type"] = "application/xml" }, SignedAppcast("3.1.1", dl, payload));
            }
            if (req.Target.StartsWith("/api/updates/download?", StringComparison.Ordinal))
            {
                if (!req.Headers.ContainsKey(UpdateDownload.LicenseKeyHeader))
                {
                    return (401, "Unauthorized", new(), []);
                }
                return (302, "Found", new() { ["Location"] = $"http://127.0.0.1:{cdn.Port}/assets/Bambu-Bar-3.1.1-windows-x64.exe" }, []);
            }
            return (404, "Not Found", new(), []);
        });

        await using (api)
        {
            var feedUrl = new Uri($"http://127.0.0.1:{api.Port}/appcast.xml");
            // Debug policy allows local loopback origin for end-to-end testing.
            var feed = new UpdateFeed(handler: null, url: feedUrl, licenseHeaders: () => UpdateDownload.Headers(ActiveLicense), allowAnyFeedOverrideHost: true);
            var checker = new UpdateChecker(() => true, new AppSettings(null), feed, _folder, "3.1.0",
                UpdatePlatform.WindowsX64, new Version(10, 0, 26100), new UpdateSignature(UpdateSignatureTests.TestPublicKey));

            checker.Start();
            await checker.CheckAsync(ct);
            Assert.Equal(UpdateState.Available, checker.State);

            var installer = await checker.DownloadAsync(ct);
            Assert.NotNull(installer);
            Assert.Equal(UpdateState.Ready, checker.State);
            Assert.Equal("Bambu-Bar-3.1.1-windows-x64.exe", Path.GetFileName(installer.Path));
            Assert.Equal(payload, File.ReadAllBytes(installer.Path));

            var apiReqs = api.Requests.ToArray();
            Assert.Equal(2, apiReqs.Length);
            // Hop 1: GET /appcast.xml had ZERO X-BambuBar-* headers
            Assert.Equal("/appcast.xml", apiReqs[0].Target);
            Assert.DoesNotContain(apiReqs[0].Headers.Keys, k => k.StartsWith("X-BambuBar-", StringComparison.OrdinalIgnoreCase));

            // Hop 2: GET /api/updates/download?... had ALL 3 X-BambuBar-* headers
            Assert.Equal("/api/updates/download?version=3.1.1&os=windows-x64", apiReqs[1].Target);
            Assert.Equal("BB-LIVE-KEY-9999", apiReqs[1].Headers[UpdateDownload.LicenseKeyHeader]);
            Assert.Equal("act-live-001", apiReqs[1].Headers[UpdateDownload.ActivationHeader]);
            Assert.Equal("inst-live-002", apiReqs[1].Headers[UpdateDownload.InstallationHeader]);

            // Hop 3 (separate CDN server after 302): ZERO X-BambuBar-* headers!
            var cdnReq = Assert.Single(cdn.Requests);
            Assert.Equal("/assets/Bambu-Bar-3.1.1-windows-x64.exe", cdnReq.Target);
            Assert.DoesNotContain(cdnReq.Headers.Keys, k => k.StartsWith("X-BambuBar-", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task LiveLoopbackReleasePolicyRefusesToAttachHeadersToLoopbackDownloadEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        LoopbackHttpServer? api = null;
        api = new LoopbackHttpServer(req =>
        {
            if (req.Target == "/appcast.xml")
            {
                var dl = $"http://127.0.0.1:{api!.Port}/api/updates/download?version=3.1.1&os=windows-x64";
                return (200, "OK", new() { ["Content-Type"] = "application/xml" }, SignedAppcast("3.1.1", dl, payload));
            }
            if (req.Target.StartsWith("/api/updates/download?", StringComparison.Ordinal))
            {
                return req.Headers.ContainsKey(UpdateDownload.LicenseKeyHeader)
                    ? (200, "OK", new(), payload)
                    : (401, "Unauthorized", new(), []);
            }
            return (404, "Not Found", new(), []);
        });

        await using (api)
        {
            var feedUrl = new Uri($"http://127.0.0.1:{api.Port}/appcast.xml");
            // Release policy: loopback allowed for anonymous feed discovery, NEVER for X-BambuBar-* headers!
            var feed = new UpdateFeed(handler: null, url: feedUrl, licenseHeaders: () => UpdateDownload.Headers(ActiveLicense), allowAnyFeedOverrideHost: false);
            var checker = new UpdateChecker(() => true, new AppSettings(null), feed, _folder, "3.1.0",
                UpdatePlatform.WindowsX64, new Version(10, 0, 26100), new UpdateSignature(UpdateSignatureTests.TestPublicKey));

            checker.Start();
            await checker.CheckAsync(ct);
            Assert.Equal(UpdateState.Available, checker.State);

            Assert.Null(await checker.DownloadAsync(ct));
            Assert.Equal(UpdateState.Failed, checker.State);
            Assert.Equal("Activate your license to download updates.", checker.Error);

            // Both requests over real TCP arrived with ZERO X-BambuBar-* headers.
            var apiReqs = api.Requests.ToArray();
            Assert.Equal(2, apiReqs.Length);
            Assert.All(apiReqs, req =>
                Assert.DoesNotContain(req.Headers.Keys, k => k.StartsWith("X-BambuBar-", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
