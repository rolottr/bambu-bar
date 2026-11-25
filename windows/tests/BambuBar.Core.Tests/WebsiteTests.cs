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

    [Theory]
    [InlineData(PolarConfiguration.SupportPlacement.MainWindow, "main_window")]
    [InlineData(PolarConfiguration.SupportPlacement.MenuPanel, "menu_panel")]
    [InlineData(PolarConfiguration.SupportPlacement.AppMenu, "app_menu")]
    [InlineData(PolarConfiguration.SupportPlacement.License, "license")]
    public void LinuxSupportLinksIdentifyTheDesktopAndPlacement(PolarConfiguration.SupportPlacement placement, string content) =>
        Assert.Equal($"https://bambubar.app/download?utm_source=bambu_bar&utm_medium=app_linux&utm_campaign=support_project&utm_content={content}",
            PolarConfiguration.SupportUrl(placement, "app_linux").AbsoluteUri);

    [Fact]
    public void SupportLinkKeepsItsPlacement() =>
        Assert.Equal("https://bambubar.app/download?utm_source=bambu_bar&utm_medium=app_windows&utm_campaign=support_project&utm_content=menu_panel",
            PolarConfiguration.SupportUrl(PolarConfiguration.SupportPlacement.MenuPanel).ToString());

    [Fact]
    public void ManualUpdateDownloadLinkIsTagged() =>
        Assert.Equal("https://bambubar.app/download?utm_source=bambu_bar&utm_medium=app_windows&utm_campaign=update&utm_content=manual_download",
            Website.Download.ToString());
}
