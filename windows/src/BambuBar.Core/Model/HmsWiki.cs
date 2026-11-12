namespace BambuBar.Core.Model;

/// <summary>
/// Finds an issue's page on Bambu Lab's wiki. Its redirect (see <see cref="HmsCatalog.WikiUrl"/>) sends some codes to
/// the HMS index although the code has a page: filed for every AMS slot at once, or under another model. So each of
/// <see cref="HmsCatalog.WikiCandidates"/> is asked where it leads, and the first real page wins, the index only when
/// all miss. The questions are HEAD requests to the same redirect the browser would follow, carrying nothing the link
/// doesn't. The app opens the result in the browser.
/// </summary>
public sealed class HmsWiki(HttpMessageHandler? handler = null)
{
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    /// <summary>The page the link should land on, or null to open it as it is (not a redirect link, or Bambu didn't
    /// answer).</summary>
    public async Task<Uri?> ResolveAsync(Uri url, CancellationToken cancellation = default)
    {
        if (url.Host != "e.bambulab.com") return null;
        Uri? index = null;
        foreach (var candidate in HmsCatalog.WikiCandidates(url))
        {
            if (await DestinationAsync(candidate, cancellation).ConfigureAwait(false) is not { } page) continue;
            if (!IsIndex(page)) return page;
            index ??= page;
        }
        return index;
    }

    private static bool IsIndex(Uri url) => url.AbsolutePath.EndsWith("/hms/home", StringComparison.Ordinal);

    /// <summary>Where the redirect points, without following it.</summary>
    private async Task<Uri?> DestinationAsync(Uri url, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is < 300 or >= 400 || response.Headers.Location is not { } location) return null;
            return location.IsAbsoluteUri ? location : new Uri(url, location);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
