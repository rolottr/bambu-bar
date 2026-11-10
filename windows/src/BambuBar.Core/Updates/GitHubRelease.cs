using System.Globalization;
using System.Text.Json;

namespace BambuBar.Core.Updates;

// Ported from the Mac's Sources/BambuBar/Updates/GitHubRelease.swift.

/// <summary>Public release metadata (GitHub's releases/latest) is used for version discovery; only the updater
/// installs signed packages from the appcast.</summary>
public sealed record GitHubRelease(string TagName, bool Draft, bool Prerelease, IReadOnlyList<GitHubRelease.Asset> Assets)
{
    public sealed record Asset(string Name);

    public bool HasUpdateFeed => Assets.Any(a => a.Name == "appcast.xml");

    /// <summary>The release's version ("1.10.0", without the tag's "v") when it is newer than
    /// <paramref name="current"/>; null for older, equal, draft and prerelease releases. Throws
    /// <see cref="GitHubReleaseFailure.InvalidVersion"/> unless both are X.Y.Z.</summary>
    public string? NewerVersion(string current)
    {
        if (Draft || Prerelease) return null;
        var version = TagName.StartsWith('v') ? TagName[1..] : TagName;
        var latest = Components(version);
        var installed = Components(current);
        for (var i = 0; i < 3; i++)
        {
            if (installed[i] != latest[i]) return installed[i] < latest[i] ? version : null;
        }
        return null;
    }

    private static int[] Components(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 3 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
        {
            throw new GitHubReleaseException(GitHubReleaseFailure.InvalidVersion);
        }
        var values = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                throw new GitHubReleaseException(GitHubReleaseFailure.InvalidVersion);
            }
        }
        return values;
    }

    /// <summary>Decodes GitHub's release JSON: tag_name, draft, prerelease and the assets' names are required, as in
    /// the Mac's Decodable. Throws <see cref="JsonException"/> when they're missing or of the wrong type.</summary>
    public static GitHubRelease FromJson(ReadOnlyMemory<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
        var assets = Member(root, "assets", JsonValueKind.Array).EnumerateArray()
            .Select(a => a.ValueKind == JsonValueKind.Object ? new Asset(Member(a, "name", JsonValueKind.String).GetString()!)
                : throw new JsonException("asset is not an object"))
            .ToList();
        return new(Member(root, "tag_name", JsonValueKind.String).GetString()!, Bool(root, "draft"), Bool(root, "prerelease"), assets);
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new JsonException($"{name} is not a boolean");

    private static JsonElement Member(JsonElement e, string name, JsonValueKind kind) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == kind ? value : throw new JsonException($"{name} is not a {kind}");
}

public enum GitHubReleaseFailure { InvalidVersion, Unavailable, MissingUpdateFeed }

/// <summary>A failed release check; <see cref="Message"/> is the text the app shows.</summary>
public sealed class GitHubReleaseException(GitHubReleaseFailure failure) : Exception
{
    public GitHubReleaseFailure Failure { get; } = failure;

    public override string Message => Failure switch
    {
        GitHubReleaseFailure.InvalidVersion => Loc.T("GitHub returned an unrecognized release version."),
        GitHubReleaseFailure.Unavailable => Loc.T("Couldn’t reach GitHub. Check your connection and try again."),
        _ => Loc.T("A newer version is available, but its signed update feed is not ready yet. Please try again later."),
    };
}
