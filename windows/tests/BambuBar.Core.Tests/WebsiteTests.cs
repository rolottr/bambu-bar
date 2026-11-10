using BambuBar.Core.Licensing;

namespace BambuBar.Core.Tests;

/// <summary>Links to bambubar.app carry UTM tags with the Windows medium (ledger P5.7). Ported from WebsiteTests.swift.</summary>
public class WebsiteTests
{
    [Fact]
    public void AboutLinksAreTagged()
    {
        Assert.Equal("https://bambubar.app/?utm_source=bambu_bar&utm_medium=app_windows&utm_campaign=about&utm_content=website",
            Website.Home.ToString());
        Assert.Equal("/changelog", Website.Changelog.AbsolutePath);
        Assert.Equal("?utm_source=bambu_bar&utm_medium=app_windows&utm_campaign=about&utm_content=release_notes", Website.Changelog.Query);
    }

    [Fact]
    public void SupportLinkKeepsItsPlacement() =>
        Assert.Equal("https://bambubar.app/download?utm_source=bambu_bar&utm_medium=app_windows&utm_campaign=support_project&utm_content=menu_panel",
            PolarConfiguration.SupportUrl(PolarConfiguration.SupportPlacement.MenuPanel).ToString());
}
