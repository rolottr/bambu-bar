using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using BambuBar.Core.App;

namespace BambuBar.Core.Updates;

/// <summary>An installer that was downloaded completely and carries the update key's signature, ready to run.</summary>
public sealed record VerifiedInstaller(string Path, AppcastItem Item);

public enum UpdateFailure { Unavailable, InvalidVersion, InvalidFeed, InvalidSignature, Storage, LicenseRequired }

/// <summary>A failed update step; <see cref="Message"/> is the text the app shows.</summary>
public sealed class UpdateException(UpdateFailure failure, Exception? inner = null) : Exception(null, inner)
{
    public UpdateFailure Failure { get; } = failure;

    public override string Message => Failure switch
    {
        UpdateFailure.Unavailable => Loc.T("Couldn’t check for updates. Check your connection and try again."),
        UpdateFailure.InvalidVersion => Loc.T("The update feed couldn’t be read. Please try again later."),
        UpdateFailure.InvalidFeed => Loc.T("The update feed couldn’t be read. Please try again later."),
        UpdateFailure.InvalidSignature => Loc.T("The downloaded update couldn’t be verified, so it won’t be installed. Please try again later."),
        UpdateFailure.Storage => Loc.T("The update couldn’t be saved. Check that your disk has free space and try again."),
        _ => Loc.T("Activate your license to download updates."),
    };
}

/// <summary>
/// The shared appcast at <c>https://bambubar.app/appcast.xml</c> and the installers it points to. Every failure is an
/// <see cref="UpdateException"/>.
/// <para>Feed fetches (<c>GET /appcast.xml</c>) are always anonymous and carry zero <c>X-BambuBar-*</c> headers, only
/// the app's version in the User-Agent. Installer downloads attach the <c>X-BambuBar-*</c> license headers
/// (<see cref="UpdateDownload"/>) ONLY on the initial request to <c>https://bambubar.app/api/updates/download</c> (or,
/// in Debug builds only, the same path on a validated <c>UpdateFeedURL</c> override). Redirects are handled manually
/// with <c>AllowAutoRedirect = false</c> so every <c>301</c>/<c>302</c>/<c>303</c>/<c>307</c>/<c>308</c> strips all
/// <c>X-BambuBar-*</c> headers before following the redirect and rejects HTTPS-to-HTTP downgrades or non-HTTP schemes.</para>
/// </summary>
public sealed partial class UpdateFeed
{
    public static readonly Uri DefaultUrl = new("https://bambubar.app/appcast.xml");

    /// <summary>Settings key and CLI flag (<c>-UpdateFeedURL &lt;url&gt;</c>) for local feed overrides.</summary>
    public const string FeedOverrideKey = "UpdateFeedURL";

    /// <summary>Optional environment variable for local feed overrides.</summary>
    public const string FeedOverrideEnvVar = "BAMBUBAR_UPDATE_FEED_URL";

    /// <summary>Today's feed is a few kilobytes; anything near this is not an appcast.</summary>
    public const int MaximumFeedBytes = 4 << 20;

    /// <summary>The largest installer the updater accepts (the enclosure's length).</summary>
    public const long MaximumDownloadBytes = 1L << 30;

    /// <summary>Bounded redirect hop limit for feed and installer downloads.</summary>
    public const int MaximumRedirects = 5;

    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A download stalled this long without a byte is given up.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Uri? _override;
    private readonly Func<IReadOnlyDictionary<string, string>?>? _licenseHeaders;
    private readonly bool _allowAnyFeedOverrideHost;

    public Uri Url { get; }

    /// <param name="handler">The HTTP stack (tests pass a fake).</param>
    /// <param name="url">An optional override feed URL (<see cref="FeedOverride"/>), validated against
    /// <see cref="UpdateDownload.ValidatedFeedOverride"/>; invalid overrides fall back to <see cref="DefaultUrl"/>.</param>
    /// <param name="licenseHeaders">The download headers while the supporter license grants access, else null
    /// (<see cref="UpdateDownload.HeadersIfActivated"/>).</param>
    /// <param name="allowAnyFeedOverrideHost">Override host policy; defaults to
    /// <see cref="UpdateDownload.DefaultAllowsAnyFeedOverrideHost"/> (true in Debug, false in Release).</param>
    public UpdateFeed(
        HttpMessageHandler? handler = null,
        Uri? url = null,
        Func<IReadOnlyDictionary<string, string>?>? licenseHeaders = null,
        bool allowAnyFeedOverrideHost = UpdateDownload.DefaultAllowsAnyFeedOverrideHost)
    {
        _allowAnyFeedOverrideHost = allowAnyFeedOverrideHost;
        if (url is not null && url != DefaultUrl)
        {
            _override = UpdateDownload.ValidatedFeedOverride(url.AbsoluteUri, allowAnyFeedOverrideHost);
        }
        Url = _override ?? DefaultUrl;
        _licenseHeaders = licenseHeaders;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BambuBar/{AppVersion.Marketing} (Windows)");
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    /// <summary>
    /// Resolves an optional feed override from CLI arguments (<c>-UpdateFeedURL &lt;url&gt;</c> or
    /// <c>--update-feed-url &lt;url&gt;</c>), <see cref="FeedOverrideEnvVar"/>, or <see cref="FeedOverrideKey"/> in
    /// settings, validated through <see cref="UpdateDownload.ValidatedFeedOverride"/>.
    /// </summary>
    public static Uri? FeedOverride(
        AppSettings? settings,
        IReadOnlyList<string>? args = null,
        Func<string, string?>? getEnv = null,
        bool allowAnyHost = UpdateDownload.DefaultAllowsAnyFeedOverrideHost)
    {
        if (TryReadArg(args, out var fromArg))
        {
            return UpdateDownload.ValidatedFeedOverride(fromArg, allowAnyHost);
        }
        var env = (getEnv ?? Environment.GetEnvironmentVariable)(FeedOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return UpdateDownload.ValidatedFeedOverride(env, allowAnyHost);
        }
        var fromSettings = settings?.Get<string>(FeedOverrideKey);
        return UpdateDownload.ValidatedFeedOverride(fromSettings, allowAnyHost);
    }

    private static bool TryReadArg(IReadOnlyList<string>? args, out string? value)
    {
        value = null;
        if (args is null) return false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if ((arg.Equals($"-{FeedOverrideKey}", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("--update-feed-url", StringComparison.OrdinalIgnoreCase))
                && i + 1 < args.Count)
            {
                value = args[i + 1];
                return true;
            }
            if (arg.StartsWith("--update-feed-url=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg["--update-feed-url=".Length..];
                return true;
            }
            if (arg.StartsWith($"-{FeedOverrideKey}=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(FeedOverrideKey.Length + 2)..];
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="url"/> is eligible to receive <c>X-BambuBar-*</c> license headers.</summary>
    public bool IsLicensedDownload(Uri url) => UpdateDownload.Accepts(url, _override, _allowAnyFeedOverrideHost);

    public async Task<Appcast> FetchAsync(CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(FeedTimeout);
        try
        {
            // Feed discovery is ALWAYS anonymous: zero X-BambuBar-* headers.
            using var response = await SendWithSafeRedirectsAsync(Url, initialHeaders: null, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new UpdateException(UpdateFailure.Unavailable);
            if (response.Content.Headers.ContentLength > MaximumFeedBytes) throw new UpdateException(UpdateFailure.InvalidFeed);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var data = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (data.Length + read > MaximumFeedBytes) throw new UpdateException(UpdateFailure.InvalidFeed);
                data.Write(buffer, 0, read);
            }
            data.Position = 0;
            return Appcast.Parse(data);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new UpdateException(UpdateFailure.Unavailable, e);
        }
    }

    /// <summary>
    /// Downloads <paramref name="item"/>'s installer into <paramref name="folder"/> and returns it once its length and
    /// signature check out; a verified copy already there is reused. The bytes go to a ".partial" file first, which is
    /// deleted when anything fails, so the installer's own name only ever holds a verified file.
    /// </summary>
    public async Task<VerifiedInstaller> DownloadAsync(AppcastItem item, string folder, UpdateSignature signature,
        CancellationToken cancellation = default)
    {
        if (item is not { Url: { } url, Length: { } length and > 0 and <= MaximumDownloadBytes, EdSignature: { } expected }
            || FileName(url, item) is not { } name)
        {
            throw new UpdateException(UpdateFailure.InvalidFeed);
        }
        var path = Path.Combine(folder, name);
        var partial = path + ".partial";
        try
        {
            Directory.CreateDirectory(folder);
            if (await signature.VerifyFileAsync(path, length, expected, cancellation).ConfigureAwait(false)) return new(path, item);
            await FetchFileAsync(url, length, partial, cancellation).ConfigureAwait(false);
            if (!await signature.VerifyFileAsync(partial, length, expected, cancellation).ConfigureAwait(false))
            {
                throw new UpdateException(UpdateFailure.InvalidSignature);
            }
            File.Move(partial, path, overwrite: true);
            return new(path, item);
        }
        catch (Exception e) when (e is not UpdateException and not OperationCanceledException)
        {
            Delete(partial);
            throw new UpdateException(e is IOException or UnauthorizedAccessException ? UpdateFailure.Storage : UpdateFailure.Unavailable, e);
        }
        catch
        {
            Delete(partial);
            throw;
        }
    }

    private async Task FetchFileAsync(Uri url, long length, string partial, CancellationToken cancellation)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stall.CancelAfter(StallTimeout);
        try
        {
            IReadOnlyDictionary<string, string>? headers = null;
            if (IsLicensedDownload(url))
            {
                headers = _licenseHeaders?.Invoke() ?? throw new UpdateException(UpdateFailure.LicenseRequired);
            }
            using var response = await SendWithSafeRedirectsAsync(url, headers, stall.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new UpdateException(UpdateFailure.LicenseRequired);
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new UpdateException(UpdateFailure.Unavailable);
            // A size other than the signed one can't verify: don't fetch it.
            if (response.Content.Headers.ContentLength is { } announced && announced != length)
            {
                throw new UpdateException(UpdateFailure.InvalidSignature);
            }
            await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                int read;
                try { read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (IOException e) { throw new UpdateException(UpdateFailure.Unavailable, e); }
                if (read == 0) break;
                stall.CancelAfter(StallTimeout);
                total += read;
                if (total > length) throw new UpdateException(UpdateFailure.InvalidSignature);
                await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
            }
            if (total != length) throw new UpdateException(UpdateFailure.InvalidSignature);
        }
        catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Unavailable, e);
        }
        catch (HttpRequestException e) { throw new UpdateException(UpdateFailure.Unavailable, e); }
    }

    /// <summary>
    /// Sends a GET request and manually follows up to <see cref="MaximumRedirects"/> redirects.
    /// <paramref name="initialHeaders"/> (the <c>X-BambuBar-*</c> license headers) are attached ONLY to hop 0 and are
    /// unconditionally stripped on every redirect hop. Rejects non-HTTP(S) redirect schemes, URL userinfo on redirect
    /// targets, and any HTTPS-to-HTTP downgrade (or non-loopback HTTP redirects).
    /// </summary>
    private async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        Uri initialUrl,
        IReadOnlyDictionary<string, string>? initialHeaders,
        CancellationToken cancellation)
    {
        var current = initialUrl;
        for (var hop = 0; hop <= MaximumRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (hop == 0 && initialHeaders is not null)
            {
                foreach (var (name, value) in initialHeaders)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();

            if (hop == MaximumRedirects || location is null)
            {
                throw new UpdateException(UpdateFailure.Unavailable);
            }
            var next = ResolveSafeRedirect(current, location) ?? throw new UpdateException(UpdateFailure.Unavailable);
            current = next;
        }
        throw new UpdateException(UpdateFailure.Unavailable);
    }

    internal static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    internal static Uri? ResolveSafeRedirect(Uri current, Uri location)
    {
        Uri next;
        try { next = location.IsAbsoluteUri ? location : new Uri(current, location); }
        catch (UriFormatException) { return null; }

        if (!next.IsAbsoluteUri || UpdateDownload.HasUserInfo(next) || string.IsNullOrEmpty(next.Host)) return null;
        var nextScheme = next.Scheme.ToLowerInvariant();
        if (nextScheme == "https") return next;
        if (nextScheme != "http") return null;

        // Never downgrade HTTPS -> HTTP, and only permit HTTP -> HTTP when staying on strict loopback (local testing).
        if (current.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return null;
        return UpdateDownload.IsLoopbackHost(current.Host) && UpdateDownload.IsLoopbackHost(next.Host) ? next : null;
    }

    /// <summary>
    /// The installer's file name: the URL's last segment when it's a plain <c>.exe</c> name (GitHub), else
    /// <c>Bambu-Bar-&lt;version&gt;-&lt;os&gt;.exe</c> (the website's <c>/api/updates/download?version=…</c>).
    /// Rejects path-traversal segments.
    /// </summary>
    internal static string? FileName(Uri url, AppcastItem? item = null)
    {
        var name = Uri.UnescapeDataString(url.Segments[^1]);
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\')) return null;
        if (SafeName().IsMatch(name) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return name;
        if (item is null || string.IsNullOrEmpty(item.DisplayVersion) || string.IsNullOrEmpty(item.Os)) return null;
        var fallback = $"Bambu-Bar-{item.DisplayVersion}-{item.Os}.exe";
        return SafeName().IsMatch(fallback) ? fallback : null;
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeName();
}
