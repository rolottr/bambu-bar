namespace BambuBar.Core;

/// <summary>Links to bambubar.app, tagged so the site's analytics can tell visits from the app apart (an app opening
/// the browser sends no Referer). The medium names the channel: <c>app_windows</c> here, <c>app</c> and
/// <c>app_store</c> on the Mac; the campaign names the feature and the content the button. Port of Website.swift.</summary>
public static class Website
{
    public const string Host = "https://bambubar.app";
    public const string Medium = "app_windows";

    public static Uri Url(string path, string campaign, string content) =>
        new($"{Host}/{path}?utm_source=bambu_bar&utm_medium={Medium}&utm_campaign={Uri.EscapeDataString(campaign)}&utm_content={Uri.EscapeDataString(content)}");

    /// <summary>The About page's links.</summary>
    public static Uri Home => Url("", "about", "website");
    public static Uri Changelog => Url("changelog", "about", "release_notes");
}
