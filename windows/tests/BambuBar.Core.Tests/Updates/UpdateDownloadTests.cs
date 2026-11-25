using BambuBar.Core.App;
using BambuBar.Core.Licensing;
using BambuBar.Core.Updates;

namespace BambuBar.Core.Tests.Updates;

/// <summary>Port of the Mac's <c>Tests/UpdatesTests/UpdateDownloadTests.swift</c>, test for test.</summary>
public class UpdateDownloadTests
{
    [Fact]
    public void AcceptsProductionEndpointWithOrWithoutQuery()
    {
        Assert.True(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download")));
        Assert.True(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download?version=3.1.0")));
        Assert.True(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download?version=3.1.0&platform=windows-x64")));
        Assert.True(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download?version=3.1.0&os=windows-x64")));
        Assert.True(UpdateDownload.Accepts(new Uri("https://BAMBUBAR.APP:443/api/updates/download?version=3.1.0&os=windows-arm64")));
    }

    [Fact]
    public void RejectsTrailingSlashAndAlternatePaths()
    {
        Assert.False(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download/")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download/?version=3.1.0")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download/extra")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download%2F")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://bambubar.app/api/updates/download%2f")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://bambubar.app/appcast.xml")));
    }

    [Fact]
    public void RejectsUserinfoWrongSchemeHostAndPort()
    {
        Assert.False(UpdateDownload.Accepts(new Uri("https://user@bambubar.app/api/updates/download")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://user:pass@bambubar.app/api/updates/download")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://@bambubar.app/api/updates/download")));
        Assert.False(UpdateDownload.Accepts(new Uri("http://bambubar.app/api/updates/download")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://bambubar.app:8443/api/updates/download")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://evil.example/api/updates/download")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://bambubar.app.evil.example/api/updates/download")));
        Assert.False(UpdateDownload.Accepts(new Uri("https://www.bambubar.app/api/updates/download")));
        Assert.False(UpdateDownload.Accepts(new Uri("file:///api/updates/download")));
    }

    [Fact]
    public void DerivesEndpointOriginFromFeedOverrideInDebugPolicyOnly()
    {
        var localFeed = new Uri("http://localhost:3201/appcast.xml");
        Assert.True(UpdateDownload.Accepts(
            new Uri("http://localhost:3201/api/updates/download?version=3.1.0&os=windows-x64"),
            localFeed,
            allowAnyFeedOverrideHost: true));
        Assert.True(UpdateDownload.Accepts(
            new Uri("https://bambubar.app/api/updates/download?version=3.1.0"),
            localFeed,
            allowAnyFeedOverrideHost: true));
        Assert.False(UpdateDownload.Accepts(
            new Uri("http://localhost:3202/api/updates/download?version=3.1.0"),
            localFeed,
            allowAnyFeedOverrideHost: true));
        Assert.False(UpdateDownload.Accepts(
            new Uri("https://localhost:3201/api/updates/download?version=3.1.0"),
            localFeed,
            allowAnyFeedOverrideHost: true));
        Assert.False(UpdateDownload.Accepts(
            new Uri("http://localhost:3201/api/updates/download/"),
            localFeed,
            allowAnyFeedOverrideHost: true));
        Assert.False(UpdateDownload.Accepts(
            new Uri("http://user@localhost:3201/api/updates/download"),
            localFeed,
            allowAnyFeedOverrideHost: true));
    }

    [Fact]
    public void ReleasePolicyAllowsLoopbackFeedDiscoveryButRejectsAllOverrideHeaderDestinations()
    {
        Assert.Null(UpdateDownload.ValidatedFeedOverride("https://evil.example/appcast.xml", allowAnyHost: false));
        Assert.Null(UpdateDownload.ValidatedFeedOverride("http://127.0.0.1.evil.com/appcast.xml", allowAnyHost: false));
        Assert.Null(UpdateDownload.ValidatedFeedOverride("http://localhost.evil.com/appcast.xml", allowAnyHost: false));
        Assert.Null(UpdateDownload.ValidatedFeedOverride("http://192.168.1.50:8080/appcast.xml", allowAnyHost: false));
        Assert.Null(UpdateDownload.ValidatedFeedOverride("http://user:pass@127.0.0.1:8080/appcast.xml", allowAnyHost: false));
        Assert.Null(UpdateDownload.ValidatedFeedOverride("http://@127.0.0.1:8080/appcast.xml", allowAnyHost: false));
        Assert.Null(UpdateDownload.ValidatedFeedOverride("file:///tmp/appcast.xml", allowAnyHost: false));

        Assert.Equal("http://127.0.0.1:8080/appcast.xml",
            UpdateDownload.ValidatedFeedOverride("http://127.0.0.1:8080/appcast.xml", allowAnyHost: false)?.AbsoluteUri);
        Assert.Equal("http://localhost:8080/appcast.xml",
            UpdateDownload.ValidatedFeedOverride("http://localhost:8080/appcast.xml", allowAnyHost: false)?.AbsoluteUri);
        Assert.Equal("http://[::1]:8080/appcast.xml",
            UpdateDownload.ValidatedFeedOverride("http://[::1]:8080/appcast.xml", allowAnyHost: false)?.AbsoluteUri);

        // Production endpoint is always accepted in Release policy.
        Assert.True(UpdateDownload.Accepts(
            new Uri("https://bambubar.app/api/updates/download?version=3.1.0&os=windows-x64"),
            new Uri("http://127.0.0.1:8080/appcast.xml"),
            allowAnyFeedOverrideHost: false));

        // Remote override must NEVER cause Accepts() to attach license headers in Release policy.
        var evilFeed = new Uri("https://evil.example/appcast.xml");
        Assert.False(UpdateDownload.Accepts(
            new Uri("https://evil.example/api/updates/download?version=3.1.0"),
            evilFeed,
            allowAnyFeedOverrideHost: false));

        // Loopback overrides must ALSO NEVER receive X-BambuBar-* license headers in Release policy.
        var loopbackIPv4Feed = new Uri("http://127.0.0.1:8080/appcast.xml");
        Assert.False(UpdateDownload.Accepts(
            new Uri("http://127.0.0.1:8080/api/updates/download"),
            loopbackIPv4Feed,
            allowAnyFeedOverrideHost: false));
        Assert.False(UpdateDownload.Accepts(
            new Uri("http://127.0.0.1:8080/api/updates/download?version=3.1.0&os=windows-x64"),
            loopbackIPv4Feed,
            allowAnyFeedOverrideHost: false));
        Assert.False(UpdateDownload.Accepts(
            new Uri("http://localhost:8080/api/updates/download?version=3.1.0"),
            new Uri("http://localhost:8080/appcast.xml"),
            allowAnyFeedOverrideHost: false));
        Assert.False(UpdateDownload.Accepts(
            new Uri("http://[::1]:8080/api/updates/download?version=3.1.0"),
            new Uri("http://[::1]:8080/appcast.xml"),
            allowAnyFeedOverrideHost: false));
        Assert.False(UpdateDownload.Accepts(
            new Uri("https://127.0.0.1:8443/api/updates/download?version=3.1.0"),
            new Uri("https://127.0.0.1:8443/appcast.xml"),
            allowAnyFeedOverrideHost: false));

        // Debug policy still permits local override origin for end-to-end local testing.
        Assert.True(UpdateDownload.Accepts(
            new Uri("http://127.0.0.1:8080/api/updates/download?version=3.1.0&os=windows-x64"),
            loopbackIPv4Feed,
            allowAnyFeedOverrideHost: true));
    }

    [Fact]
    public void HeadersCarryExactBambuBarFieldsWhenRecordComplete()
    {
        Assert.Equal("X-BambuBar-License-Key", UpdateDownload.LicenseKeyHeader);
        Assert.Equal("X-BambuBar-Activation-Id", UpdateDownload.ActivationHeader);
        Assert.Equal("X-BambuBar-Installation-Id", UpdateDownload.InstallationHeader);
        Assert.Equal(new Uri("https://bambubar.app/api/updates/download"), UpdateDownload.Endpoint);

        var record = new LicenseRecord("BB-KEY-1234", "act-1", "inst-1");
        var headers = UpdateDownload.Headers(record);
        Assert.Equal(new Dictionary<string, string>
        {
            ["X-BambuBar-License-Key"] = "BB-KEY-1234",
            ["X-BambuBar-Activation-Id"] = "act-1",
            ["X-BambuBar-Installation-Id"] = "inst-1",
        }, headers);

        Assert.Null(UpdateDownload.Headers(null));
        Assert.Null(UpdateDownload.Headers(new LicenseRecord("", "act-1", "inst-1")));
        Assert.Null(UpdateDownload.Headers(new LicenseRecord("k", "", "inst-1")));
        Assert.Null(UpdateDownload.Headers(new LicenseRecord("k", "a", "")));
    }

    [Fact]
    public void CompileTimePolicyMatchesBuildConfiguration()
    {
#if DEBUG
        Assert.True(UpdateDownload.DefaultAllowsAnyFeedOverrideHost);
#else
        Assert.False(UpdateDownload.DefaultAllowsAnyFeedOverrideHost);
        // Under Release default policy, loopback feed override is NEVER accepted for license headers.
        Assert.False(UpdateDownload.Accepts(
            new Uri("http://127.0.0.1:8080/api/updates/download?version=3.1.0&os=windows-x64"),
            new Uri("http://127.0.0.1:8080/appcast.xml")));
#endif
    }

    [Fact]
    public void FeedOverrideReadsArgsEnvAndSettingsUnderSecurityPolicy()
    {
        var settings = new AppSettings(null);
        Assert.Null(UpdateFeed.FeedOverride(settings, getEnv: _ => null, allowAnyHost: false));

        settings.Set(UpdateFeed.FeedOverrideKey, "https://evil.example/appcast.xml");
        Assert.Null(UpdateFeed.FeedOverride(settings, getEnv: _ => null, allowAnyHost: false));
        Assert.Equal(new Uri("https://evil.example/appcast.xml"),
            UpdateFeed.FeedOverride(settings, getEnv: _ => null, allowAnyHost: true));

        settings.Set(UpdateFeed.FeedOverrideKey, "http://127.0.0.1:9000/appcast.xml");
        Assert.Equal(new Uri("http://127.0.0.1:9000/appcast.xml"),
            UpdateFeed.FeedOverride(settings, getEnv: _ => null, allowAnyHost: false));

        // Env var overrides settings; CLI flag overrides env var.
        Assert.Equal(new Uri("http://localhost:9001/appcast.xml"),
            UpdateFeed.FeedOverride(settings, getEnv: _ => "http://localhost:9001/appcast.xml", allowAnyHost: false));
        Assert.Equal(new Uri("http://[::1]:9002/appcast.xml"),
            UpdateFeed.FeedOverride(settings, ["-UpdateFeedURL", "http://[::1]:9002/appcast.xml"],
                getEnv: _ => "http://localhost:9001/appcast.xml", allowAnyHost: false));
        Assert.Equal(new Uri("http://127.0.0.1:9003/appcast.xml"),
            UpdateFeed.FeedOverride(settings, ["--update-feed-url=http://127.0.0.1:9003/appcast.xml"],
                getEnv: _ => null, allowAnyHost: false));
        // Invalid Release CLI flag is rejected (does not fall through to remote host).
        Assert.Null(UpdateFeed.FeedOverride(settings, ["-UpdateFeedURL", "https://evil.example/appcast.xml"],
            getEnv: _ => null, allowAnyHost: false));
    }
}
