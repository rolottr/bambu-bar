using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace BambuBar.Core.Updates;

/// <summary>
/// The update feed both platforms share (ledger D12, §8): Sparkle's appcast format, written by scripts/appcast.sh and
/// read from <c>releases/latest/download/appcast.xml</c>.
/// <para>Platform and architecture sit where Sparkle and WinSparkle look for them, in the enclosure's
/// <c>sparkle:os</c>: no attribute or <c>"macos"</c> is the Mac's item (Sparkle's default), and every Windows item
/// names its architecture, <c>"windows-x64"</c> or <c>"windows-arm64"</c> (WinSparkle's values). Sparkle on the Mac
/// keeps only items whose <c>sparkle:os</c> is missing or <c>"macos"</c>, so it never sees the Windows items. A plain
/// <c>"windows"</c> item has no architecture and is ignored here: an x64 build can't run on Windows 10 on ARM.</para>
/// </summary>
public sealed class Appcast
{
    public const string SparkleNamespace = "http://www.andymatuschak.org/xml-namespaces/sparkle";
    private static readonly XNamespace Sparkle = SparkleNamespace;

    public IReadOnlyList<AppcastItem> Items { get; }

    private Appcast(IReadOnlyList<AppcastItem> items) => Items = items;

    /// <summary>Reads an RSS appcast. DTDs and external entities are refused. Throws
    /// <see cref="UpdateFailure.InvalidFeed"/> when it isn't one.</summary>
    public static Appcast Parse(Stream xml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false };
            using var reader = XmlReader.Create(xml, settings);
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "rss" || root.Element("channel") is not { } channel) throw new UpdateException(UpdateFailure.InvalidFeed);
            return new(channel.Elements("item").Select(Item).ToList());
        }
        catch (XmlException e) { throw new UpdateException(UpdateFailure.InvalidFeed, e); }
    }

    public static Appcast Parse(ReadOnlySpan<byte> xml)
    {
        using var stream = new MemoryStream(xml.ToArray(), writable: false);
        return Parse(stream);
    }

    private static AppcastItem Item(XElement item)
    {
        var enclosure = item.Element("enclosure");
        // Sparkle reads the versions from the item and, in older feeds, from the enclosure.
        string? Value(string name) => Text(item.Element(Sparkle + name)) ?? Text(enclosure?.Attribute(Sparkle + name));
        return new AppcastItem
        {
            Title = Text(item.Element("title")),
            Version = Value("version"),
            ShortVersion = Value("shortVersionString"),
            Os = Text(enclosure?.Attribute(Sparkle + "os")),
            Url = Link(enclosure?.Attribute("url")),
            Length = long.TryParse(Text(enclosure?.Attribute("length")), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : null,
            EdSignature = Text(enclosure?.Attribute(Sparkle + "edSignature")),
            MinimumSystemVersion = Text(item.Element(Sparkle + "minimumSystemVersion")),
            ReleaseNotesLink = Link(item.Element(Sparkle + "releaseNotesLink")),
            FullReleaseNotesLink = Link(item.Element(Sparkle + "fullReleaseNotesLink")),
            Description = item.Element("description")?.Value.Trim() is { Length: > 0 } description ? description : null,
        };
    }

    private static string? Text(XElement? e) => e?.Value.Trim() is { Length: > 0 } text ? text : null;
    private static string? Text(XAttribute? a) => a?.Value.Trim() is { Length: > 0 } text ? text : null;
    private static Uri? Link(XElement? e) => Link(Text(e));
    private static Uri? Link(XAttribute? a) => Link(Text(a));

    private static Uri? Link(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri : null;

    /// <summary>
    /// The newest item for <paramref name="platform"/> that <paramref name="osVersion"/> can run (its
    /// <c>sparkle:minimumSystemVersion</c>, e.g. <c>10.0.18362</c>), or null when the feed has none: a feed with only the
    /// Mac's item means "no Windows update". Throws <see cref="UpdateFailure.InvalidVersion"/> when one of this
    /// platform's items has no X.Y.Z version, and <see cref="UpdateFailure.InvalidFeed"/> when the chosen one lacks its
    /// download, length or signature.
    /// </summary>
    public AppcastItem? Latest(string platform, Version osVersion)
    {
        AppcastItem? best = null;
        var bestVersion = default(ReleaseVersion);
        foreach (var item in Items.Where(i => i.Os == platform))
        {
            if (!ReleaseVersion.TryParse(item.DisplayVersion, out var version)) throw new UpdateException(UpdateFailure.InvalidVersion);
            if (item.MinimumSystemVersion is { } minimum && (!Version.TryParse(minimum, out var required) || osVersion < required)) continue;
            if (best is null || version > bestVersion) (best, bestVersion) = (item, version);
        }
        if (best is { Url: null } or { Length: null or <= 0 } or { EdSignature: null }) throw new UpdateException(UpdateFailure.InvalidFeed);
        return best;
    }
}

/// <summary>One release in the appcast, for one platform.</summary>
public sealed record AppcastItem
{
    public string? Title { get; init; }

    /// <summary><c>sparkle:version</c>: the Mac's build number (CFBundleVersion, a timestamp) on its item.</summary>
    public string? Version { get; init; }

    /// <summary><c>sparkle:shortVersionString</c>: the X.Y.Z both platforms share.</summary>
    public string? ShortVersion { get; init; }

    /// <summary>The enclosure's <c>sparkle:os</c>; null on today's Mac item.</summary>
    public string? Os { get; init; }

    public Uri? Url { get; init; }

    /// <summary>The enclosure's <c>length</c> in bytes, which the download must match exactly.</summary>
    public long? Length { get; init; }

    /// <summary>The enclosure's <c>sparkle:edSignature</c>: Ed25519 over the whole file, Base64.</summary>
    public string? EdSignature { get; init; }

    public string? MinimumSystemVersion { get; init; }
    public Uri? ReleaseNotesLink { get; init; }
    public Uri? FullReleaseNotesLink { get; init; }

    /// <summary>The release notes as HTML (the item's description).</summary>
    public string? Description { get; init; }

    /// <summary>What Sparkle shows and compares here: the short version, else <see cref="Version"/>.</summary>
    public string? DisplayVersion => ShortVersion ?? Version;

    /// <summary>The Mac's item, as Sparkle decides it (no <c>sparkle:os</c>, or <c>"macos"</c>).</summary>
    public bool IsMac => Os is null or "macos";
}

/// <summary>The <c>sparkle:os</c> values of the Windows items (one per architecture).</summary>
public static class UpdatePlatform
{
    public const string WindowsX64 = "windows-x64";
    public const string WindowsArm64 = "windows-arm64";

    /// <summary>The installer this process should update with: its own architecture (the app ships native ARM64).</summary>
    public static string Current => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? WindowsArm64 : WindowsX64;
}
