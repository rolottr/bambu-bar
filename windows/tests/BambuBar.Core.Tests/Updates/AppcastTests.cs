using System.Text;
using BambuBar.Core.Updates;

namespace BambuBar.Core.Tests.Updates;

/// <summary>The shared appcast: today's real Mac-only feed, and a two-platform feed as P5.5 will write it.</summary>
public class AppcastTests
{
    internal static string Fixture(string name) =>
        Path.Combine(Repo.Root, "windows", "tests", "BambuBar.Core.Tests", "Updates", "Fixtures", name);

    internal static Appcast Load(string name)
    {
        using var file = File.OpenRead(Fixture(name));
        return Appcast.Parse(file);
    }

    private static readonly Version Windows11 = new(10, 0, 26100);

    private static Appcast Feed(string items) => Appcast.Parse(Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:sparkle="{Appcast.SparkleNamespace}"><channel><title>Bambu Bar</title>{items}</channel></rss>
        """));

    private static string Item(string version, string? os, string? minimum = null, string signature = "c2ln", string length = "10") => $"""
        <item><sparkle:shortVersionString>{version}</sparkle:shortVersionString>
        {(minimum is null ? "" : $"<sparkle:minimumSystemVersion>{minimum}</sparkle:minimumSystemVersion>")}
        <enclosure url="https://example.com/Bambu-Bar-{version}-{os ?? "macos"}.exe" {(os is null ? "" : $"sparkle:os=\"{os}\"")}
         sparkle:edSignature="{signature}" length="{length}" /></item>
        """;

    [Fact]
    public void RealMacFeedHasTheMacItemAndNoWindowsUpdate()
    {
        var appcast = Load("appcast-2.1.1.xml");
        var mac = Assert.Single(appcast.Items);
        Assert.True(mac.IsMac);
        Assert.Null(mac.Os); // appcast.sh writes no sparkle:os today.
        Assert.Equal("2.1.1", mac.DisplayVersion);
        Assert.Equal("202611101352", mac.Version);
        Assert.Equal("26.0", mac.MinimumSystemVersion);
        Assert.Equal(25416645, mac.Length);
        Assert.Equal("/qJmg7Xjir59NXPuXVeb2ivHil0VNThHy88xSwqvf7ooLYA0vmpzT0vv3TgOR4Np9jsSC2a9eKlxVc3jsiiLBw==", mac.EdSignature);
        Assert.Equal("https://github.com/rolottr/bambu-bar/releases/download/v2.1.1/Bambu-Bar-2.1.1-macos.zip", mac.Url?.ToString());
        Assert.Equal("https://github.com/rolottr/bambu-bar/releases", mac.FullReleaseNotesLink?.ToString());
        Assert.Contains("Bambu Bar 2.1.1 speaks your language everywhere.", mac.Description);
        Assert.Null(appcast.Latest(UpdatePlatform.WindowsX64, Windows11));
        Assert.Null(appcast.Latest(UpdatePlatform.WindowsArm64, Windows11));
    }

    [Fact]
    public void TwoPlatformFeedPicksTheItemForEachArchitectureAndKeepsTheMacItem()
    {
        var appcast = Load("appcast-two-platforms.xml");
        Assert.Equal(3, appcast.Items.Count);
        var x64 = appcast.Latest(UpdatePlatform.WindowsX64, Windows11);
        var arm64 = appcast.Latest(UpdatePlatform.WindowsArm64, Windows11);
        Assert.Equal("https://github.com/rolottr/bambu-bar/releases/download/v2.1.1/Bambu-Bar-2.1.1-windows-x64.exe", x64?.Url?.ToString());
        Assert.Equal("https://github.com/rolottr/bambu-bar/releases/download/v2.1.1/Bambu-Bar-2.1.1-windows-arm64.exe", arm64?.Url?.ToString());
        Assert.Equal(100000, x64?.Length);
        Assert.Equal("2.1.1", x64?.DisplayVersion);
        Assert.False(x64?.IsMac);
        // What Sparkle on the Mac sees (items without sparkle:os or with "macos") is exactly today's item.
        Assert.Equal(Load("appcast-2.1.1.xml").Items.Single(), Assert.Single(appcast.Items, i => i.IsMac));
        // An older Windows 10 can't take an item that needs 1903.
        Assert.Null(appcast.Latest(UpdatePlatform.WindowsX64, new Version(10, 0, 17763)));
    }

    [Fact]
    public void NewestRunnableItemWinsAndPlainWindowsIsIgnored()
    {
        var appcast = Feed(Item("2.1.1", null) + Item("3.0.0", "windows-x64") + Item("3.10.0", "windows-x64", "10.0.99999")
            + Item("3.2.0", "windows-x64", "10.0.18362") + Item("3.1.0", "windows-x64") + Item("9.0.0", "windows")
            + Item("9.0.0", "macos") + Item("4.0.0", "windows-x64", "not a version"));
        Assert.Equal("3.2.0", appcast.Latest(UpdatePlatform.WindowsX64, Windows11)?.DisplayVersion);
        Assert.Null(appcast.Latest(UpdatePlatform.WindowsArm64, Windows11));
        Assert.Equal(2, appcast.Items.Count(i => i.IsMac));
    }

    [Fact]
    public void BrokenWindowsItemsAndFeedsAreErrors()
    {
        Assert.Equal(UpdateFailure.InvalidVersion, Assert.Throws<UpdateException>(
            () => Feed(Item("3.0", "windows-x64")).Latest(UpdatePlatform.WindowsX64, Windows11)).Failure);
        Assert.Equal(UpdateFailure.InvalidFeed, Assert.Throws<UpdateException>(
            () => Feed(Item("3.0.0", "windows-x64", length: "")).Latest(UpdatePlatform.WindowsX64, Windows11)).Failure);
        Assert.Equal(UpdateFailure.InvalidFeed, Assert.Throws<UpdateException>(
            () => Feed(Item("3.0.0", "windows-x64", signature: "")).Latest(UpdatePlatform.WindowsX64, Windows11)).Failure);
        // A broken Mac item is the Mac's business.
        Assert.Null(Feed(Item("3.0", null, length: "")).Latest(UpdatePlatform.WindowsX64, Windows11));
        Assert.Equal(UpdateFailure.InvalidFeed, Assert.Throws<UpdateException>(() => Appcast.Parse("<rss><channel>"u8)).Failure);
        Assert.Equal(UpdateFailure.InvalidFeed, Assert.Throws<UpdateException>(() => Appcast.Parse("<html/>"u8)).Failure);
        // No DTDs, so no entity expansion or external entities.
        Assert.Equal(UpdateFailure.InvalidFeed, Assert.Throws<UpdateException>(() => Appcast.Parse(
            """<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY x "x">]><rss><channel>&x;</channel></rss>"""u8)).Failure);
    }

    [Fact]
    public void VersionsCompareNumerically()
    {
        Assert.True(ReleaseVersion.TryParse("1.10.0", out var ten));
        Assert.True(ReleaseVersion.TryParse("1.9.0", out var nine));
        Assert.True(ten > nine);
        Assert.Equal("1.10.0", ten.ToString());
        foreach (var bad in new[] { "", "1.2", "1.2.3.4", "v1.2.3", "1..3", "1.2.-3", "1.99999999999.0", " 1.2.3", "１.2.3" })
        {
            Assert.False(ReleaseVersion.TryParse(bad, out _), bad);
        }
    }
}
