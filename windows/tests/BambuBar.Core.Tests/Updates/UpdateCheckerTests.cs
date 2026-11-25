using System.Net;
using System.Text;
using BambuBar.Core.App;
using BambuBar.Core.Licensing;
using BambuBar.Core.Updates;

namespace BambuBar.Core.Tests.Updates;

// The Mac's UpdateChecker behaviour without Sparkle: anonymous daily check on https://bambubar.app/appcast.xml,
// unlicensed available-version notice with strictly blocked downloads, gated https://bambubar.app/api/updates/download
// with X-BambuBar-* headers stripped on 302 redirects, skip, later, and automatic download.

public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private const string LegacyX64Url = "https://github.com/rolottr/bambu-bar/releases/download/v2.1.1/Bambu-Bar-2.1.1-windows-x64.exe";
    private const string GatedX64Url = "https://bambubar.app/api/updates/download?version=3.1.1&os=windows-x64";
    private const string RedirectArchiveUrl = "https://objects.githubusercontent.com/bambubar/Bambu-Bar-3.1.1-windows-x64.exe";

    private static readonly LicenseRecord TestLicense = new("BB-SUPPORTER-KEY", "act-1234", "inst-5678", FixedNow);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"bambubar-updates-{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();
    private readonly AppSettings _settings = new(null);
    private readonly List<(TimeSpan Span, TaskCompletionSource Wait, CancellationToken Token)> _waits = [];
    private DateTimeOffset _now = FixedNow;
    private bool _access = true;

    public UpdateCheckerTests() => _server.Serve(UpdateFeed.DefaultUrl.ToString(), File.ReadAllBytes(AppcastTests.Fixture("appcast-two-platforms.xml")));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed record RecordedRequest(Uri Url, Dictionary<string, string[]> Headers);

    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];
        public readonly List<RecordedRequest> Requests = [];

        public void Serve(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
            _routes[url] = _ => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };

        public void Serve(string url, Func<HttpRequestMessage, HttpResponseMessage> handler) => _routes[url] = handler;

        public void Redirect(string url, string location, HttpStatusCode status = HttpStatusCode.Found, bool requireLicense = false) =>
            _routes[url] = request =>
            {
                if (requireLicense && !request.Headers.Contains(UpdateDownload.LicenseKeyHeader))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                var response = new HttpResponseMessage(status);
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return response;
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new(request.RequestUri!, headers));
            var url = request.RequestUri!.ToString();
            var response = _routes.TryGetValue(url, out var handler)
                ? handler(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }

    private static byte[] WebsiteFeed(string version, string x64Url, byte[] payload, string? signature = null) =>
        Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:sparkle="{Appcast.SparkleNamespace}">
              <channel>
                <title>Bambu Bar</title>
                <link>https://bambubar.app</link>
                <item>
                  <title>Bambu Bar {version}</title>
                  <sparkle:version>202612010000</sparkle:version>
                  <sparkle:shortVersionString>{version}</sparkle:shortVersionString>
                  <sparkle:minimumSystemVersion>26.0</sparkle:minimumSystemVersion>
                  <enclosure url="https://bambubar.app/api/updates/download?version={version}" type="application/octet-stream" length="1" sparkle:edSignature="A" />
                </item>
                <item>
                  <title>Bambu Bar {version}</title>
                  <sparkle:version>{version}</sparkle:version>
                  <sparkle:shortVersionString>{version}</sparkle:shortVersionString>
                  <sparkle:minimumSystemVersion>10.0.18362</sparkle:minimumSystemVersion>
                  <enclosure url="{System.Security.SecurityElement.Escape(x64Url)}" sparkle:os="windows-x64" type="application/octet-stream" length="{payload.Length}" sparkle:edSignature="{signature ?? UpdateSignatureTests.PayloadSignature}" />
                </item>
              </channel>
            </rss>
            """);

    private UpdateChecker Checker(
        string current = "2.1.0",
        string platform = UpdatePlatform.WindowsX64,
        Uri? feedUrl = null,
        bool allowAnyFeedOverrideHost = UpdateDownload.DefaultAllowsAnyFeedOverrideHost,
        Func<IReadOnlyDictionary<string, string>?>? licenseHeaders = null) =>
        new(() => _access, _settings,
            new UpdateFeed(_server, feedUrl, licenseHeaders: null, allowAnyFeedOverrideHost),
            _folder, current, platform, new Version(10, 0, 26100),
            new UpdateSignature(UpdateSignatureTests.TestPublicKey), () => _now, (span, token) =>
            {
                var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => wait.TrySetCanceled(token));
                lock (_waits) _waits.Add((span, wait, token));
                return wait.Task;
            },
            licenseHeaders ?? (() => _access ? UpdateDownload.Headers(TestLicense) : null));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    private static void AssertNoLicenseHeaders(RecordedRequest request) =>
        Assert.DoesNotContain(request.Headers.Keys, k => k.StartsWith("X-BambuBar-", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void DefaultFeedUrlIsBambuBarApp() =>
        Assert.Equal(new Uri("https://bambubar.app/appcast.xml"), UpdateFeed.DefaultUrl);

    [Fact]
    public async Task UnlicensedCopyChecksAnonymouslySurfacesNoticeAndBlocksDownloads()
    {
        var ct = TestContext.Current.CancellationToken;
        _access = false;
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), WebsiteFeed("3.1.1", GatedX64Url, UpdateSignatureTests.Payload()));
        _server.Redirect(GatedX64Url, RedirectArchiveUrl, requireLicense: true);
        _server.Serve(RedirectArchiveUrl, UpdateSignatureTests.Payload());

        var checker = Checker(current: "3.1.0");
        checker.DownloadsAutomatically = true; // Saved preference, but gated off while unlicensed.
        Assert.True(_settings.Get(UpdateChecker.Keys.AutomaticDownloads, false));
        Assert.False(checker.DownloadsAutomatically);

        checker.Start();
        Assert.True(checker.IsRunning);
        Assert.True(checker.CanCheckForUpdates);
        Assert.True(checker.ChecksAutomatically);
        Assert.False(checker.SupportsAutomaticUpdates);
        Assert.Single(_waits);

        // Scheduled check runs anonymously, surfaces available version notice, and does NOT download.
        _waits[0].Wait.SetResult();
        await Until(() => checker.State == UpdateState.Available);
        Assert.Equal("3.1.1", checker.Available?.DisplayVersion);
        Assert.Equal("3.1.1", checker.Offered?.DisplayVersion);
        Assert.Single(_server.Requests);
        Assert.Equal(UpdateFeed.DefaultUrl, _server.Requests[0].Url);
        AssertNoLicenseHeaders(_server.Requests[0]);
        Assert.False(Directory.Exists(_folder));

        // Direct download attempt while unlicensed is blocked locally with zero download requests,
        // keeping State at Available so the Settings "Download…" button stays visible.
        Assert.Null(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Null(checker.Error);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task LicensedDownloadSendsHeadersToEndpointAndStripsThemOn302Redirect()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), WebsiteFeed("3.1.1", GatedX64Url, payload));
        _server.Redirect(GatedX64Url, RedirectArchiveUrl, HttpStatusCode.Found, requireLicense: true);
        _server.Serve(RedirectArchiveUrl, payload);

        var checker = Checker(current: "3.1.0");
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Equal("3.1.1", checker.Offered?.DisplayVersion);

        var installer = await checker.DownloadAsync(ct);
        Assert.NotNull(installer);
        Assert.Equal(UpdateState.Ready, checker.State);
        Assert.Equal("Bambu-Bar-3.1.1-windows-x64.exe", Path.GetFileName(installer.Path));
        Assert.Equal(payload, File.ReadAllBytes(installer.Path));

        // Request 0: anonymous GET /appcast.xml (0 X-BambuBar-* headers)
        // Request 1: gated GET https://bambubar.app/api/updates/download?... (all 3 X-BambuBar-* headers)
        // Request 2: redirected GET https://objects.githubusercontent.com/... (0 X-BambuBar-* headers!)
        Assert.Equal(3, _server.Requests.Count);
        Assert.Equal(UpdateFeed.DefaultUrl, _server.Requests[0].Url);
        AssertNoLicenseHeaders(_server.Requests[0]);

        Assert.Equal(new Uri(GatedX64Url), _server.Requests[1].Url);
        Assert.Equal(["BB-SUPPORTER-KEY"], _server.Requests[1].Headers[UpdateDownload.LicenseKeyHeader]);
        Assert.Equal(["act-1234"], _server.Requests[1].Headers[UpdateDownload.ActivationHeader]);
        Assert.Equal(["inst-5678"], _server.Requests[1].Headers[UpdateDownload.InstallationHeader]);

        Assert.Equal(new Uri(RedirectArchiveUrl), _server.Requests[2].Url);
        AssertNoLicenseHeaders(_server.Requests[2]);
    }

    [Theory]
    [InlineData("http://objects.githubusercontent.com/Bambu-Bar-3.1.1-windows-x64.exe")]
    [InlineData("http://127.0.0.1:8080/Bambu-Bar-3.1.1-windows-x64.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://user:pass@objects.githubusercontent.com/Bambu-Bar-3.1.1-windows-x64.exe")]
    public async Task UnsafeRedirectsAreRejected(string badLocation)
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), WebsiteFeed("3.1.1", GatedX64Url, payload));
        _server.Redirect(GatedX64Url, badLocation, HttpStatusCode.Found, requireLicense: true);

        var checker = Checker(current: "3.1.0");
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Null(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal("Couldn’t check for updates. Check your connection and try again.", checker.Error);
        // Never followed the unsafe redirect target.
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task RedirectLoopExceedingMaximumHopsIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), WebsiteFeed("3.1.1", GatedX64Url, payload));
        _server.Redirect(GatedX64Url, "https://cdn.example/hop1", requireLicense: true);
        for (var i = 1; i <= UpdateFeed.MaximumRedirects + 1; i++)
        {
            _server.Redirect($"https://cdn.example/hop{i}", $"https://cdn.example/hop{i + 1}");
        }

        var checker = Checker(current: "3.1.0");
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Null(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Failed, checker.State);
        // 1 appcast + (1 initial download + MaximumRedirects hops)
        Assert.Equal(1 + UpdateFeed.MaximumRedirects + 1, _server.Requests.Count);
        foreach (var redirected in _server.Requests.Skip(2)) AssertNoLicenseHeaders(redirected);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedLicenseOnDownloadEndpointReportsLicenseRequired(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), WebsiteFeed("3.1.1", GatedX64Url, payload));
        _server.Serve(GatedX64Url, [], status);

        var checker = Checker(current: "3.1.0");
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Null(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal("Activate your license to download updates.", checker.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PostRedirectCdnAuthFailureReportsUnavailableNotLicenseRequired(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), WebsiteFeed("3.1.1", GatedX64Url, payload));
        _server.Redirect(GatedX64Url, RedirectArchiveUrl, HttpStatusCode.Found, requireLicense: true);
        _server.Serve(RedirectArchiveUrl, [], status);

        var checker = Checker(current: "3.1.0");
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Null(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal("Couldn’t check for updates. Check your connection and try again.", checker.Error);
        Assert.Equal(3, _server.Requests.Count);
        AssertNoLicenseHeaders(_server.Requests[2]);
    }

    [Fact]
    public async Task LicenseHeadersAreSnapshottedSynchronouslyOnCallingThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), WebsiteFeed("3.1.1", GatedX64Url, payload));
        _server.Redirect(GatedX64Url, RedirectArchiveUrl, HttpStatusCode.Found, requireLicense: true);
        _server.Serve(RedirectArchiveUrl, payload);

        var callerThread = Environment.CurrentManagedThreadId;
        int? delegateThread = null;
        var calls = 0;
        var checker = Checker(current: "3.1.0", licenseHeaders: () =>
        {
            calls++;
            delegateThread = Environment.CurrentManagedThreadId;
            return UpdateDownload.Headers(TestLicense);
        });
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Equal(0, calls);

        // Start DownloadAsync synchronously: the delegate must have already run on the calling (UI) thread
        // before DownloadAsync returns its task / yields to off-thread I/O.
        var downloadTask = checker.DownloadAsync(ct);
        Assert.Equal(1, calls);
        Assert.Equal(callerThread, delegateThread);
        var installer = await downloadTask;
        Assert.NotNull(installer);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TamperedCachedInstallerFailsReverificationBeforeLaunchAndOnQuit()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), WebsiteFeed("3.1.1", GatedX64Url, payload));
        _server.Redirect(GatedX64Url, RedirectArchiveUrl, HttpStatusCode.Found, requireLicense: true);
        _server.Serve(RedirectArchiveUrl, payload);

        var checker = Checker(current: "3.1.0");
        checker.DownloadsAutomatically = true;
        checker.Start();
        await checker.CheckAsync(ct);
        var installer = await checker.DownloadAsync(ct);
        Assert.NotNull(installer);
        Assert.Equal(UpdateState.Ready, checker.State);
        Assert.Equal(installer.Path, checker.VerifyInstallerForQuit());
        Assert.True(await checker.VerifyInstallerAsync(installer, ct));

        // Tamper with the cached installer on disk: install-on-quit must refuse to launch and clear it.
        var tampered = (byte[])payload.Clone();
        tampered[4096] ^= 0xFF;
        File.WriteAllBytes(installer.Path, tampered);
        Assert.Null(checker.VerifyInstallerForQuit());
        Assert.Null(checker.Installer);
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.False(File.Exists(installer.Path));

        // Re-download a clean copy, then tamper again: immediate InstallUpdate re-verification must fail and clear it.
        var redownloaded = await checker.DownloadAsync(ct);
        Assert.NotNull(redownloaded);
        Assert.Equal(UpdateState.Ready, checker.State);
        File.WriteAllBytes(redownloaded.Path, tampered);
        Assert.False(await checker.VerifyInstallerAsync(redownloaded, ct));
        Assert.Null(checker.Installer);
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal("The downloaded update couldn’t be verified, so it won’t be installed. Please try again later.", checker.Error);
        Assert.False(File.Exists(redownloaded.Path));

        // Tampering with a cached Ready installer before calling DownloadAsync forces a fresh verified download.
        var third = await checker.DownloadAsync(ct);
        Assert.NotNull(third);
        var requestsBefore = _server.Requests.Count;
        File.WriteAllBytes(third.Path, tampered);
        var refreshed = await checker.DownloadAsync(ct);
        Assert.NotNull(refreshed);
        Assert.True(_server.Requests.Count > requestsBefore);
        Assert.Equal(payload, File.ReadAllBytes(refreshed.Path));
    }

    [Fact]
    public async Task ReleasePolicyNeverSendsLicenseHeadersToLoopbackDownloadUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        var localFeed = new Uri("http://127.0.0.1:18080/appcast.xml");
        var localDownload = "http://127.0.0.1:18080/api/updates/download?version=3.1.1&os=windows-x64";
        _server.Serve(localFeed.ToString(), WebsiteFeed("3.1.1", localDownload, payload));
        _server.Serve(localDownload, req => req.Headers.Contains(UpdateDownload.LicenseKeyHeader)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var checker = Checker(current: "3.1.0", feedUrl: localFeed, allowAnyFeedOverrideHost: false);
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Equal(localFeed, _server.Requests[0].Url);
        AssertNoLicenseHeaders(_server.Requests[0]);

        Assert.Null(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal("Couldn’t check for updates. Check your connection and try again.", checker.Error);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(new Uri(localDownload), _server.Requests[1].Url);
        AssertNoLicenseHeaders(_server.Requests[1]);
    }

    [Fact]
    public async Task FindsThisPcsItemAndNeverADowngrade()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Checker();
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Equal(LegacyX64Url, checker.Offered?.Url?.ToString());
        Assert.Equal(FixedNow, checker.LastChecked);
        Assert.Equal(FixedNow, _settings.Get<DateTimeOffset?>(UpdateChecker.Keys.LastCheck));

        var arm = Checker(platform: UpdatePlatform.WindowsArm64);
        arm.Start();
        await arm.CheckAsync(ct);
        Assert.EndsWith("windows-arm64.exe", arm.Available?.Url?.ToString());

        foreach (var current in new[] { "2.1.1", "2.10.0" })
        {
            var upToDate = Checker(current);
            upToDate.Start();
            await upToDate.CheckAsync(ct);
            Assert.Equal(UpdateState.UpToDate, upToDate.State);
            Assert.Null(upToDate.Available);
        }
    }

    [Fact]
    public async Task MacOnlyFeedIsNoWindowsUpdate()
    {
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), File.ReadAllBytes(AppcastTests.Fixture("appcast-2.1.1.xml")));
        var checker = Checker("1.0.0");
        checker.Start();
        await checker.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.UpToDate, checker.State);
        Assert.Null(checker.Offered);
    }

    [Fact]
    public async Task FailuresShowConsistentMessagesAndKeepTheLastCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Checker();
        checker.Start();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), [], HttpStatusCode.ServiceUnavailable);
        await checker.CheckAsync(ct);
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal("Couldn’t check for updates. Check your connection and try again.", checker.Error);
        Assert.Null(checker.LastChecked);

        _server.Serve(UpdateFeed.DefaultUrl.ToString(), "<html>not a feed</html>"u8.ToArray());
        await checker.CheckAsync(ct);
        Assert.Equal("The update feed couldn’t be read. Please try again later.", checker.Error);

        var malformed = Checker("2.1");
        malformed.Start();
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), File.ReadAllBytes(AppcastTests.Fixture("appcast-two-platforms.xml")));
        await malformed.CheckAsync(ct);
        Assert.Equal("The update feed couldn’t be read. Please try again later.", malformed.Error);
    }

    [Fact]
    public async Task SkipPersistsUntilANewerVersionOrAManualCheckFindsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Checker();
        checker.Start();
        await checker.CheckAsync(ct);
        checker.SkipVersion();
        Assert.Null(checker.Offered);
        Assert.NotNull(checker.Available);
        Assert.Equal("2.1.1", _settings.Get<string>(UpdateChecker.Keys.SkippedVersion));

        // Another run remembers it; its scheduled check finds the update but neither offers nor downloads it.
        var next = Checker();
        next.DownloadsAutomatically = true;
        next.Start();
        _waits[^1].Wait.SetResult();
        await Until(() => next.State == UpdateState.Available);
        Assert.Null(next.Offered);
        Assert.False(Directory.Exists(_folder));

        // Asking explicitly shows it again, as Sparkle's own window does.
        await next.CheckAsync(ct);
        Assert.NotNull(next.Offered);
        Assert.Null(_settings.Get<string>(UpdateChecker.Keys.SkippedVersion));
    }

    [Fact]
    public async Task LaterHidesTheOfferUntilTheNextCheckFindsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Checker();
        checker.Start();
        await checker.CheckAsync(ct);
        checker.RemindLater();
        Assert.True(checker.OfferDismissed);
        Assert.Null(checker.Offered);
        Assert.NotNull(checker.Available);
        await checker.CheckAsync(ct);
        Assert.NotNull(checker.Offered);
    }

    [Fact]
    public async Task ChecksDailyFromTheLastCheck()
    {
        _settings.Set<DateTimeOffset?>(UpdateChecker.Keys.LastCheck, FixedNow - TimeSpan.FromHours(3));
        var checker = Checker();
        checker.Start();
        checker.Start();
        Assert.Equal([TimeSpan.FromHours(21)], _waits.Select(w => w.Span));

        _now = FixedNow + TimeSpan.FromHours(21);
        _waits[0].Wait.SetResult();
        await Until(() => _waits.Count == 2);
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Equal(_now, checker.LastChecked);
        Assert.Equal(TimeSpan.FromDays(1), _waits[1].Span);
        // Not a downloader unless asked.
        Assert.False(Directory.Exists(_folder));

        // A failed check waits a day too, not a second.
        _server.Serve(UpdateFeed.DefaultUrl.ToString(), [], HttpStatusCode.InternalServerError);
        _now += TimeSpan.FromDays(1);
        _waits[1].Wait.SetResult();
        await Until(() => _waits.Count == 3);
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal(TimeSpan.FromDays(1), _waits[2].Span);

        // Turning automatic checks off stops the schedule; on again, it resumes.
        checker.ChecksAutomatically = false;
        Assert.True(_waits[2].Token.IsCancellationRequested);
        checker.ChecksAutomatically = true;
        Assert.Equal(4, _waits.Count);
        checker.Stop();
        Assert.True(_waits[3].Token.IsCancellationRequested);
    }

    [Fact]
    public void FirstRunChecksSoonAndAClockSetBackWaitsNoMoreThanADay()
    {
        var checker = Checker();
        Assert.Equal(UpdateChecker.FirstCheckDelay, checker.NextCheckDelay());
        _settings.Set<DateTimeOffset?>(UpdateChecker.Keys.LastCheck, FixedNow + TimeSpan.FromDays(30));
        Assert.Equal(UpdateChecker.CheckInterval, Checker().NextCheckDelay());
        _settings.Set<DateTimeOffset?>(UpdateChecker.Keys.LastCheck, FixedNow - TimeSpan.FromDays(30));
        Assert.Equal(UpdateChecker.FirstCheckDelay, Checker().NextCheckDelay());
    }

    [Fact]
    public async Task AutomaticDownloadLeavesAVerifiedInstaller()
    {
        _server.Serve(LegacyX64Url, UpdateSignatureTests.Payload());
        var checker = Checker();
        checker.DownloadsAutomatically = true;
        Assert.True(_settings.Get(UpdateChecker.Keys.AutomaticDownloads, false));
        checker.Start();
        _waits[0].Wait.SetResult();
        await Until(() => checker.State == UpdateState.Ready);
        var installer = checker.Installer!;
        Assert.Equal(Path.Combine(_folder, "Bambu-Bar-2.1.1-windows-x64.exe"), installer.Path);
        Assert.Equal(UpdateSignatureTests.Payload(), File.ReadAllBytes(installer.Path));
        Assert.Equal(["Bambu-Bar-2.1.1-windows-x64.exe"], Directory.GetFiles(_folder).Select(Path.GetFileName));
        // Non-endpoint (GitHub bridge) downloads never receive X-BambuBar-* headers.
        Assert.All(_server.Requests, AssertNoLicenseHeaders);

        // The next check keeps it, and asking again doesn't download it again.
        var requests = _server.Requests.Count;
        await checker.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Ready, checker.State);
        Assert.Same(installer, await checker.DownloadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(requests + 1, _server.Requests.Count);

        // A new run finds the verified file on disk instead of downloading it again.
        var next = Checker();
        next.Start();
        await next.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(installer.Path, (await next.DownloadAsync(TestContext.Current.CancellationToken))?.Path);
        Assert.DoesNotContain(_server.Requests.Skip(requests + 1), r => r.Url.ToString() == LegacyX64Url);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task InstallersThatDontVerifyAreDeleted(string damage)
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = UpdateSignatureTests.Payload();
        if (damage == "tampered") payload[777] ^= 0x80;
        _server.Serve(LegacyX64Url, damage switch { "short" => payload[..^1], "long" => [.. payload, 0], _ => payload });
        var checker = Checker();
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Null(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal("The downloaded update couldn’t be verified, so it won’t be installed. Please try again later.", checker.Error);
        Assert.Null(checker.Installer);
        Assert.Empty(Directory.GetFiles(_folder));
        // The release stays on offer, to try again.
        Assert.NotNull(checker.Offered);
    }

    [Fact]
    public async Task LosingTheLicenseDisablesDownloadsWhileKeepingAnonymousChecks()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.Serve(LegacyX64Url, UpdateSignatureTests.Payload());
        var checker = Checker();
        checker.DownloadsAutomatically = true;
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.NotNull(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Ready, checker.State);

        _access = false;
        checker.SupporterStatusChanged();
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Null(checker.Installer);
        Assert.NotNull(checker.Available);
        Assert.NotNull(checker.Offered);
        Assert.True(checker.CanCheckForUpdates);
        Assert.False(checker.SupportsAutomaticUpdates);
        Assert.False(checker.DownloadsAutomatically);
        Assert.False(_waits[0].Token.IsCancellationRequested);
        Assert.Null(await checker.DownloadAsync(ct));
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Null(checker.Error);

        // Back again: automatic downloads are available again.
        _access = true;
        checker.SupporterStatusChanged();
        Assert.True(checker.SupportsAutomaticUpdates);
        Assert.True(checker.DownloadsAutomatically);
    }

    [Fact]
    public void FileNamesHandleGitHubUrlsWebsiteEndpointAndRejectTraversal()
    {
        var item = new AppcastItem { ShortVersion = "3.1.0", Os = UpdatePlatform.WindowsX64 };
        Assert.Equal("Bambu-Bar-3.0.0-windows-x64.exe",
            UpdateFeed.FileName(new Uri("https://x.test/v3.0.0/Bambu-Bar-3.0.0-windows-x64.exe"), item));
        Assert.Equal("Bambu-Bar-3.1.0-windows-x64.exe",
            UpdateFeed.FileName(new Uri("https://bambubar.app/api/updates/download?version=3.1.0&os=windows-x64"), item));
        Assert.Equal("Bambu-Bar-3.1.0-windows-x64.exe",
            UpdateFeed.FileName(new Uri("https://bambubar.app/api/updates/download?version=3.1.0&platform=windows-x64"), item));
        Assert.Null(UpdateFeed.FileName(new Uri("https://x.test/dir/..%5C..%5Cevil.exe"), item));
        Assert.Null(UpdateFeed.FileName(new Uri("https://x.test/dir/..%2Fevil.exe"), item));
        Assert.Null(UpdateFeed.FileName(new Uri("https://x.test/")));
        Assert.Null(UpdateFeed.FileName(new Uri("https://x.test/a%20b.exe")));
    }
}
