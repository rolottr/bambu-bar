using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BambuBar.Core.Licensing;

// Ported from the Mac's Sources/BambuBar/Licensing/PolarClient.swift. One Polar key works on both platforms (ledger
// D7, D14): endpoints, ids, request bodies, checks and status mapping are the Mac's; only the activation label differs.

/// <summary>Public customer endpoints only. An organization access token must never ship in the app.</summary>
public static class PolarConfiguration
{
    public const string OrganizationId = "2925c2bc-549b-4b87-96ca-83d2268817a6";
    public const string BenefitId = "3560f84c-991a-4d50-9b5d-1009584204dd";
    public static readonly Uri PortalUrl = new("https://polar.sh/nina-labs-ai-llc/portal");

    /// <summary>Where a "Support this project" link sits, reported as the link's utm_content.</summary>
    public enum SupportPlacement { MainWindow, AppMenu, MenuPanel, License }

    /// <summary>The website's download page, tagged like the Mac's <c>Website.url("download", …)</c>.</summary>
    public static Uri SupportUrl(SupportPlacement placement)
    {
        var content = placement switch
        {
            SupportPlacement.MainWindow => "main_window",
            SupportPlacement.AppMenu => "app_menu",
            SupportPlacement.MenuPanel => "menu_panel",
            _ => "license",
        };
        return Website.Url("download", "support_project", content);
    }
}

/// <summary>Why a licensing step failed (the Mac's <c>LicenseError</c>).</summary>
public enum LicenseFailure { Rejected, ActivationLimit, Unavailable, InvalidResponse, Storage }

/// <summary>A licensing failure; <see cref="Message"/> is the text the app shows.</summary>
public sealed class LicenseException(LicenseFailure failure, Exception? inner = null) : Exception(null, inner)
{
    public LicenseFailure Failure { get; } = failure;

    public override string Message => Failure switch
    {
        LicenseFailure.Rejected => Loc.T("This license or activation is no longer valid. Check your license in your account, then deactivate this installation before activating again."),
        LicenseFailure.ActivationLimit => Loc.T("This license could not be activated. If all installations are in use, deactivate one in your account, then try again."),
        LicenseFailure.Unavailable => Loc.T("Cannot reach the licensing service right now. Check your internet connection and try again."),
        LicenseFailure.InvalidResponse => Loc.T("The licensing service returned an unexpected response. Please try again later."),
        // The catalog has only the Mac's wording; a Windows one ("Credential Manager") waits for P4.2.
        _ => Loc.T("The license could not be saved in Windows Credential Manager. Try again, or restart Windows if it keeps failing."),
    };
}

/// <summary>A license key as Polar describes it, with the activation the request named (if any).</summary>
public sealed record PolarLicense(string OrganizationId, string BenefitId, string Status, DateTimeOffset? ExpiresAt, string? ActivationId)
{
    /// <summary>Throws <see cref="LicenseFailure.Rejected"/> unless the key is this product's, granted, unexpired and
    /// (when <paramref name="activationId"/> is given) bound to that activation.</summary>
    public void Verify(string? activationId, DateTimeOffset now)
    {
        if (OrganizationId != PolarConfiguration.OrganizationId || BenefitId != PolarConfiguration.BenefitId
            || Status != "granted" || ExpiresAt is { } expiry && expiry <= now
            || activationId is not null && ActivationId != activationId)
        {
            throw new LicenseException(LicenseFailure.Rejected);
        }
    }

    /// <summary>Decodes Polar's validate response; anything malformed is <see cref="LicenseFailure.InvalidResponse"/>.</summary>
    public static PolarLicense FromJson(ReadOnlyMemory<byte> utf8) => PolarJson.Decode(utf8, Read);

    internal static PolarLicense Read(JsonElement e) => new(
        PolarJson.String(e, "organization_id"), PolarJson.String(e, "benefit_id"), PolarJson.String(e, "status"),
        PolarJson.Date(e, "expires_at"),
        PolarJson.Optional(e, "activation") is { } activation ? PolarJson.String(activation, "id") : null);
}

/// <summary>A new activation and the license key it belongs to.</summary>
public sealed record PolarActivation(string Id, PolarLicense LicenseKey)
{
    public static PolarActivation FromJson(ReadOnlyMemory<byte> utf8) => PolarJson.Decode(utf8, e => new PolarActivation(
        PolarJson.String(e, "id"),
        PolarLicense.Read(PolarJson.Optional(e, "license_key") ?? throw new LicenseException(LicenseFailure.InvalidResponse))));
}

/// <summary>The license service. Every failure is a <see cref="LicenseException"/>.</summary>
public interface ILicenseApi
{
    Task<PolarActivation> ActivateAsync(string key, string installationId, CancellationToken cancellation = default);
    Task<PolarLicense> ValidateAsync(LicenseRecord record, CancellationToken cancellation = default);
    Task DeactivateAsync(LicenseRecord record, CancellationToken cancellation = default);
}

/// <summary>Polar's public customer-portal license-key endpoints, without any credentials.</summary>
public sealed class PolarClient(HttpMessageHandler? handler = null) : ILicenseApi
{
    public static readonly Uri Endpoint = new("https://api.polar.sh/v1/customer-portal/license-keys/");

    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>The activation's name in the customer's Polar account (the Mac's reads "Mac · …", ledger D14).</summary>
    public static string ActivationLabel(string installationId) =>
        $"Windows · {(installationId.Length > 8 ? installationId[..8] : installationId)}";

    public async Task<PolarActivation> ActivateAsync(string key, string installationId, CancellationToken cancellation = default)
    {
        var data = await RequestAsync("activate", new JsonObject
        {
            ["key"] = key,
            ["label"] = ActivationLabel(installationId),
            ["conditions"] = new JsonObject { ["installation_id"] = installationId },
        }, cancellation).ConfigureAwait(false);
        return PolarActivation.FromJson(data);
    }

    public async Task<PolarLicense> ValidateAsync(LicenseRecord record, CancellationToken cancellation = default)
    {
        var data = await RequestAsync("validate", new JsonObject
        {
            ["key"] = record.Key,
            ["activation_id"] = record.ActivationId,
            ["benefit_id"] = PolarConfiguration.BenefitId,
            ["conditions"] = new JsonObject { ["installation_id"] = record.InstallationId },
        }, cancellation).ConfigureAwait(false);
        return PolarLicense.FromJson(data);
    }

    public async Task DeactivateAsync(LicenseRecord record, CancellationToken cancellation = default) =>
        await RequestAsync("deactivate", new JsonObject
        {
            ["key"] = record.Key,
            ["activation_id"] = record.ActivationId,
        }, cancellation).ConfigureAwait(false);

    private async Task<byte[]> RequestAsync(string operation, JsonObject fields, CancellationToken cancellation)
    {
        fields["organization_id"] = PolarConfiguration.OrganizationId;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, operation));
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(fields.ToJsonString()));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        int status;
        byte[] data;
        try
        {
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            status = (int)response.StatusCode;
            data = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new LicenseException(LicenseFailure.Unavailable, e);
        }
        return status switch
        {
            >= 200 and < 300 => data,
            404 when operation == "deactivate" => [], // Already removed in Polar.
            404 => throw new LicenseException(LicenseFailure.Rejected),
            403 when operation == "activate" => throw new LicenseException(LicenseFailure.ActivationLimit),
            400 or 401 or 403 or 422 => throw new LicenseException(LicenseFailure.Rejected),
            429 or (>= 500 and <= 599) => throw new LicenseException(LicenseFailure.Unavailable),
            _ => throw new LicenseException(LicenseFailure.InvalidResponse),
        };
    }
}

/// <summary>Reads Polar's snake_case JSON as strictly as the Mac's decoder: required strings, optional dates in
/// ISO 8601 with a time zone, with or without fractional seconds.</summary>
internal static partial class PolarJson
{
    public static T Decode<T>(ReadOnlyMemory<byte> utf8, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new LicenseException(LicenseFailure.InvalidResponse);
            return read(document.RootElement);
        }
        catch (JsonException e) { throw new LicenseException(LicenseFailure.InvalidResponse, e); }
    }

    /// <summary>The member, or null when it's missing or JSON null; a non-object is malformed.</summary>
    public static JsonElement? Optional(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.Object ? value
        : throw new LicenseException(LicenseFailure.InvalidResponse);

    public static string String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!
        : throw new LicenseException(LicenseFailure.InvalidResponse);

    public static DateTimeOffset? Date(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !IsoDate().IsMatch(value.GetString()!))
        {
            throw new LicenseException(LicenseFailure.InvalidResponse);
        }
        // .NET reads at most 7 fractional digits (100 ns); finer ones can't matter here.
        var text = LongFraction().Replace(value.GetString()!, "$1");
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new LicenseException(LicenseFailure.InvalidResponse);
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(\.[0-9]{7})[0-9]+")]
    private static partial Regex LongFraction();
}
