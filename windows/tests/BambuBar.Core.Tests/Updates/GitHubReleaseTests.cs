using System.Text;
using System.Text.Json;
using BambuBar.Core.Updates;

namespace BambuBar.Core.Tests.Updates;

// Ported from the Mac's Tests/UpdatesTests/GitHubReleaseTests.swift.

public class GitHubReleaseTests
{
    private static GitHubRelease Release(string tag, bool draft = false, bool prerelease = false, bool feed = false) =>
        new(tag, draft, prerelease, feed ? [new GitHubRelease.Asset("appcast.xml")] : []);

    [Fact]
    public void OldReleaseWithoutFeedDoesNotOfferDowngrade()
    {
        var latest = Release("v1.3.1");
        Assert.Null(latest.NewerVersion("1.5.0"));
        Assert.False(latest.HasUpdateFeed);
    }

    [Fact]
    public void VersionsCompareNumerically()
    {
        Assert.Equal("1.10.0", Release("v1.10.0").NewerVersion("1.9.0"));
        Assert.Null(Release("1.5.0").NewerVersion("1.5.0"));
        Assert.Equal("2.0.0", Release("v2.0.0").NewerVersion("1.99.99"));
    }

    [Fact]
    public void DraftsPrereleasesAndMalformedVersionsAreNotOffered()
    {
        Assert.Null(Release("v2.0.0", draft: true).NewerVersion("1.5.0"));
        Assert.Null(Release("v2.0.0-beta", prerelease: true).NewerVersion("1.5.0"));
        Assert.Throws<GitHubReleaseException>(() => Release("latest").NewerVersion("1.5.0"));
        Assert.Throws<GitHubReleaseException>(() => Release("v2..0").NewerVersion("1.5.0"));
    }

    [Fact]
    public void DecodesGitHubWireFormatAndIdentifiesSignedFeed()
    {
        var data = Encoding.UTF8.GetBytes("""{"tag_name":"v1.6.0","draft":false,"prerelease":false,"assets":[{"name":"Bambu-Bar-1.6.0-macos.zip"},{"name":"appcast.xml"}]}""");
        var latest = GitHubRelease.FromJson(data);
        Assert.Equal("1.6.0", latest.NewerVersion("1.5.0"));
        Assert.True(latest.HasUpdateFeed);
    }

    // Beyond the Swift tests: the decoder is as strict as Swift's Decodable, and overflowing numbers are malformed.
    [Fact]
    public void MissingFieldsAndHugeNumbersAreErrors()
    {
        Assert.Throws<JsonException>(() => GitHubRelease.FromJson(Encoding.UTF8.GetBytes("""{"tag_name":"v1.6.0","draft":false,"prerelease":false}""")));
        Assert.Throws<JsonException>(() => GitHubRelease.FromJson(Encoding.UTF8.GetBytes("""{"tag_name":"v1.6.0","draft":"no","prerelease":false,"assets":[]}""")));
        var e = Assert.Throws<GitHubReleaseException>(() => Release("v1.99999999999.0").NewerVersion("1.5.0"));
        Assert.Equal(GitHubReleaseFailure.InvalidVersion, e.Failure);
        Assert.Throws<GitHubReleaseException>(() => Release("v1.6.0").NewerVersion("1.5"));
    }
}
