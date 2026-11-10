using System.Net;
using System.Text;
using BambuBar.Core.App;
using BambuBar.Core.Updates;

namespace BambuBar.Core.Tests.Updates;

// The Mac's UpdateChecker behaviour without Sparkle: license gate, daily schedule, skip, later, automatic download.
// Everything runs offline: GitHub is a fake handler serving the fixtures and the signed test payload.

public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private const string X64Url = "https://github.com/rolottr/bambu-bar/releases/download/v2.1.1/Bambu-Bar-2.1.1-windows-x64.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"bambubar-updates-{Guid.NewGuid():N}");
    private readonly FakeGitHub _github = new();
    private readonly AppSettings _settings = new(null);
    private readonly List<(TimeSpan Span, TaskCompletionSource Wait, CancellationToken Token)> _waits = [];
    private DateTimeOffset _now = FixedNow;
    private bool _access = true;

    public UpdateCheckerTests() => _github.Serve(UpdateFeed.DefaultUrl.ToString(), File.ReadAllBytes(AppcastTests.Fixture("appcast-two-platforms.xml")));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode, byte[])> _responses = [];
        public readonly List<string> Requests = [];

        public void Serve(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK) => _responses[url] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var (status, body) = _responses.TryGetValue(url, out var found) ? found : (HttpStatusCode.NotFound, []);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    private UpdateChecker Checker(string current = "2.1.0", string platform = UpdatePlatform.WindowsX64) =>
        new(() => _access, _settings, new UpdateFeed(_github), _folder, current, platform, new Version(10, 0, 26100),
            new UpdateSignature(UpdateSignatureTests.TestPublicKey), () => _now, (span, token) =>
            {
                var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => wait.TrySetCanceled(token));
                lock (_waits) _waits.Add((span, wait, token));
                return wait.Task;
            });

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task WithoutALicenseNothingStartsOrChecks()
    {
        _access = false;
        var checker = Checker();
        checker.Start();
        await checker.CheckAsync(TestContext.Current.CancellationToken);
        Assert.False(checker.IsRunning);
        Assert.False(checker.CanCheckForUpdates);
        Assert.False(checker.ChecksAutomatically);
        Assert.Equal(UpdateState.Idle, checker.State);
        Assert.Empty(_github.Requests);
        Assert.Empty(_waits);
        // Choices made meanwhile are kept for when the license comes back.
        checker.ChecksAutomatically = true;
        Assert.True(_settings.Get(UpdateChecker.Keys.AutomaticChecks, false));
        Assert.False(checker.ChecksAutomatically);
    }

    [Fact]
    public async Task FindsThisPcsItemAndNeverADowngrade()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Checker();
        checker.Start();
        await checker.CheckAsync(ct);
        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Equal(X64Url, checker.Offered?.Url?.ToString());
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
        _github.Serve(UpdateFeed.DefaultUrl.ToString(), File.ReadAllBytes(AppcastTests.Fixture("appcast-2.1.1.xml")));
        var checker = Checker("1.0.0");
        checker.Start();
        await checker.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.UpToDate, checker.State);
        Assert.Null(checker.Offered);
    }

    [Fact]
    public async Task FailuresShowTheMacsMessagesAndKeepTheLastCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Checker();
        checker.Start();
        _github.Serve(UpdateFeed.DefaultUrl.ToString(), [], HttpStatusCode.ServiceUnavailable);
        await checker.CheckAsync(ct);
        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Equal("Couldn’t reach GitHub. Check your connection and try again.", checker.Error);
        Assert.Null(checker.LastChecked);

        _github.Serve(UpdateFeed.DefaultUrl.ToString(), "<html>not a feed</html>"u8.ToArray());
        await checker.CheckAsync(ct);
        Assert.Equal("The update feed couldn’t be read. Please try again later.", checker.Error);

        var malformed = Checker("2.1");
        malformed.Start();
        _github.Serve(UpdateFeed.DefaultUrl.ToString(), File.ReadAllBytes(AppcastTests.Fixture("appcast-two-platforms.xml")));
        await malformed.CheckAsync(ct);
        Assert.Equal("GitHub returned an unrecognized release version.", malformed.Error);
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
        _github.Serve(UpdateFeed.DefaultUrl.ToString(), [], HttpStatusCode.InternalServerError);
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
        _github.Serve(X64Url, UpdateSignatureTests.Payload());
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

        // The next check keeps it, and asking again doesn't download it again.
        var requests = _github.Requests.Count;
        await checker.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Ready, checker.State);
        Assert.Same(installer, await checker.DownloadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(requests + 1, _github.Requests.Count);

        // A new run finds the verified file on disk instead of downloading it again.
        var next = Checker();
        next.Start();
        await next.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(installer.Path, (await next.DownloadAsync(TestContext.Current.CancellationToken))?.Path);
        Assert.DoesNotContain(X64Url, _github.Requests.Skip(requests + 1));
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
        _github.Serve(X64Url, damage switch { "short" => payload[..^1], "long" => [.. payload, 0], _ => payload });
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
    public async Task LosingTheLicenseStopsEverything()
    {
        var checker = Checker();
        checker.Start();
        await checker.CheckAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(checker.Offered);
        _access = false;
        checker.SupporterStatusChanged();
        Assert.Equal(UpdateState.Idle, checker.State);
        Assert.Null(checker.Available);
        Assert.Null(checker.Offered);
        Assert.False(checker.CanCheckForUpdates);
        Assert.True(_waits[0].Token.IsCancellationRequested);
        Assert.Null(await checker.DownloadAsync(TestContext.Current.CancellationToken));

        // Back again: the schedule restarts.
        _access = true;
        checker.SupporterStatusChanged();
        Assert.Equal(2, _waits.Count);
        Assert.True(checker.CanCheckForUpdates);
    }

    [Fact]
    public void FileNamesComeFromTheUrlOnlyWhenPlain()
    {
        Assert.Equal("Bambu-Bar-3.0.0-windows-x64.exe", UpdateFeed.FileName(new Uri("https://x.test/v3.0.0/Bambu-Bar-3.0.0-windows-x64.exe")));
        Assert.Null(UpdateFeed.FileName(new Uri("https://x.test/dir/..%5C..%5Cevil.exe")));
        Assert.Null(UpdateFeed.FileName(new Uri("https://x.test/")));
        Assert.Null(UpdateFeed.FileName(new Uri("https://x.test/a%20b.exe")));
    }
}
