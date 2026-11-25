using BambuBar.Core.Licensing;

namespace BambuBar.Core.Updates;

/// <summary>
/// Port of the Mac's <c>Sources/BambuBar/Updates/UpdateDownload.swift</c>. The license headers sent ONLY to
/// <c>https://bambubar.app/api/updates/download</c> (or, in Debug builds only, the same path on a validated
/// <c>UpdateFeedURL</c> override) so the website can verify the supporter license with Polar before redirecting to
/// the signed archive. Never attached to feed checks (<c>GET /appcast.xml</c> is always anonymous), never forwarded
/// across redirects, and never sent to any override origin in Release.
/// </summary>
public static class UpdateDownload
{
    public const string LicenseKeyHeader = "X-BambuBar-License-Key";
    public const string ActivationHeader = "X-BambuBar-Activation-Id";
    public const string InstallationHeader = "X-BambuBar-Installation-Id";

    /// <summary>The canonical website download endpoint (<c>?version=X.Y.Z&amp;os=windows-x64</c>).</summary>
    public static Uri Endpoint { get; } = new("https://bambubar.app/api/updates/download");

#if DEBUG
    public const bool DefaultAllowsAnyFeedOverrideHost = true;
#else
    public const bool DefaultAllowsAnyFeedOverrideHost = false;
#endif

    /// <summary>True only for strict loopback hosts (<c>127.0.0.1</c>, <c>::1</c>, or <c>localhost</c>).</summary>
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var lowered = host.ToLowerInvariant();
        return lowered is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }

    /// <summary>
    /// Validates an <c>UpdateFeedURL</c> override for anonymous appcast feed discovery.
    /// In Release policy (<paramref name="allowAnyHost"/> = false), only strict loopback hosts are permitted for
    /// anonymous feed checks; <see cref="Accepts"/> independently refuses every override origin (including loopback)
    /// as a license-header destination in Release.
    /// </summary>
    public static Uri? ValidatedFeedOverride(string? raw, bool allowAnyHost = DefaultAllowsAnyFeedOverrideHost)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url)) return null;
        if (HasUserInfo(url)) return null;
        var scheme = url.Scheme.ToLowerInvariant();
        if (scheme is not ("https" or "http")) return null;
        if (string.IsNullOrEmpty(url.Host)) return null;
        if (!allowAnyHost && !IsLoopbackHost(url.Host)) return null;
        return url;
    }

    /// <summary>Returns null when no complete activation record exists.</summary>
    public static IReadOnlyDictionary<string, string>? Headers(LicenseRecord? record)
    {
        if (record is null || record.Key.Length == 0 || record.ActivationId.Length == 0 || record.InstallationId.Length == 0)
        {
            return null;
        }
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LicenseKeyHeader] = record.Key,
            [ActivationHeader] = record.ActivationId,
            [InstallationHeader] = record.InstallationId,
        };
    }

    /// <summary>The headers while this installation is activated and its license grants access (offline grace included).</summary>
    public static IReadOnlyDictionary<string, string>? HeadersIfActivated(LicenseStore? license) =>
        license is { HasAccess: true } ? Headers(license.Record) : null;

    /// <summary>
    /// True only for <c>https://bambubar.app/api/updates/download</c> — and, ONLY when
    /// <paramref name="allowAnyFeedOverrideHost"/> is <c>true</c> (<c>#if DEBUG</c>), <c>/api/updates/download</c>
    /// on the exact scheme/host/port of a validated <c>UpdateFeedURL</c> override — with no URL userinfo and no
    /// trailing slash or alternate path encoding.
    /// <para>In Release (<paramref name="allowAnyFeedOverrideHost"/> = false), NO override origin (not even loopback
    /// <c>127.0.0.1</c> / <c>localhost</c> / <c>::1</c>) is ever accepted as a header destination, preventing any
    /// same-user local process from intercepting <c>X-BambuBar-*</c> license headers over loopback HTTP.</para>
    /// </summary>
    public static bool Accepts(Uri url, Uri? feedOverride = null, bool allowAnyFeedOverrideHost = DefaultAllowsAnyFeedOverrideHost)
    {
        if (!url.IsAbsoluteUri || HasUserInfo(url)) return false;
        // Preserve percent-encoding and trailing slashes: `/api/updates/download/`, `/api/updates/download%2F`,
        // and `/api/updates/download/extra` are all rejected.
        var rawPath = url.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var endpointPath = Endpoint.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (!string.Equals(rawPath, endpointPath, StringComparison.Ordinal)) return false;
        if (!string.Equals(url.AbsolutePath, Endpoint.AbsolutePath, StringComparison.Ordinal)) return false;

        var target = Origin(url);
        if (target is null) return false;
        if (target == Origin(Endpoint)) return true;

        if (!allowAnyFeedOverrideHost || feedOverride is null) return false;
        var validOverride = ValidatedFeedOverride(feedOverride.AbsoluteUri, allowAnyHost: true);
        return validOverride is not null && Origin(validOverride) == target;
    }

    /// <summary>True if the URI carries any userinfo component (including an empty <c>@</c> prefix).</summary>
    internal static bool HasUserInfo(Uri url)
    {
        if (!url.IsAbsoluteUri) return false;
        if (url.UserInfo.Length != 0) return true;
        return url.GetComponents(UriComponents.UserInfo | UriComponents.KeepDelimiter, UriFormat.UriEscaped).Length != 0;
    }

    /// <summary><c>scheme://host:port</c>, lowercased, with the default port written out.</summary>
    private static string? Origin(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Host.Length == 0) return null;
        var scheme = url.Scheme.ToLowerInvariant();
        if (scheme is not ("https" or "http")) return null;
        var port = url.IsDefaultPort ? (scheme == "https" ? 443 : 80) : url.Port;
        if (port <= 0) return null;
        return $"{scheme}://{url.Host.ToLowerInvariant()}:{port}";
    }
}
