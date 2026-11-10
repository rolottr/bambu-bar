using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace BambuBar.Core.Updates;

/// <summary>An installer that was downloaded completely and carries the update key's signature, ready to run.</summary>
public sealed record VerifiedInstaller(string Path, AppcastItem Item);

public enum UpdateFailure { Unavailable, InvalidVersion, InvalidFeed, InvalidSignature, Storage, SupporterRequired }

/// <summary>A failed update step; <see cref="Message"/> is the text the app shows.</summary>
public sealed class UpdateException(UpdateFailure failure, Exception? inner = null) : Exception(null, inner)
{
    public UpdateFailure Failure { get; } = failure;

    public override string Message => Failure switch
    {
        UpdateFailure.Unavailable => Loc.T("Couldn’t reach GitHub. Check your connection and try again."),
        UpdateFailure.InvalidVersion => Loc.T("GitHub returned an unrecognized release version."),
        UpdateFailure.InvalidFeed => Loc.T("The update feed couldn’t be read. Please try again later."),
        UpdateFailure.InvalidSignature => Loc.T("The downloaded update couldn’t be verified, so it won’t be installed. Please try again later."),
        UpdateFailure.Storage => Loc.T("The update couldn’t be saved. Check that your disk has free space and try again."),
        _ => Loc.T("In-app update checks require an active website supporter license."),
    };
}

/// <summary>
/// The shared appcast on GitHub and the downloads it points to. Every failure is an <see cref="UpdateException"/>.
/// Requests carry no identifiers, only the app's version in the User-Agent (as the Mac's GitHub check does).
/// </summary>
public sealed partial class UpdateFeed
{
    public static readonly Uri DefaultUrl = new("https://github.com/rolottr/bambu-bar/releases/latest/download/appcast.xml");

    /// <summary>Today's feed is a few kilobytes; anything near this is not an appcast.</summary>
    public const int MaximumFeedBytes = 4 << 20;

    /// <summary>The largest installer the updater accepts (the enclosure's length).</summary>
    public const long MaximumDownloadBytes = 1L << 30;

    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A download stalled this long without a byte is given up.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    public Uri Url { get; }

    /// <param name="handler">The HTTP stack (tests pass a fake).</param>
    /// <param name="url">Another appcast, to try an update end to end (the Mac's <c>-UpdateFeedURL</c>); its items
    /// still need this app's signature.</param>
    public UpdateFeed(HttpMessageHandler? handler = null, Uri? url = null)
    {
        Url = url ?? DefaultUrl;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BambuBar/{AppVersion.Marketing}");
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    public async Task<Appcast> FetchAsync(CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(FeedTimeout);
        try
        {
            using var response = await _http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new UpdateException(UpdateFailure.Unavailable);
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
            || FileName(url) is not { } name)
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
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new UpdateException(UpdateFailure.Unavailable);
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

    /// <summary>The download's file name ("Bambu-Bar-3.0.0-windows-x64.exe"), or null when the URL's last segment
    /// isn't a plain one.</summary>
    internal static string? FileName(Uri url)
    {
        var name = Uri.UnescapeDataString(url.Segments[^1]);
        return SafeName().IsMatch(name) ? name : null;
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeName();
}
