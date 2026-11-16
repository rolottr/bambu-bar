using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BambuBar.Core.Model;

/// <summary>
/// Bambu Lab's texts for HMS codes and print errors in the app's language, in each printer model's own wording;
/// English for languages Bambu's HMS service doesn't offer. Generated from Bambu's catalogues by
/// shared/scripts/make-hms.py before a release (nothing is downloaded at run time), stored as raw DEFLATE (not zlib
/// or gzip). A code Bambu lists with no text maps to "": its apps don't show those, so
/// <see cref="Protocol.ReportParser"/> skips them.
/// </summary>
public sealed class HmsCatalog
{
    private sealed class Table
    {
        [JsonPropertyName("hms")] public Dictionary<string, string> Hms { get; set; } = [];
        [JsonPropertyName("error")] public Dictionary<string, string> Errors { get; set; } = [];
    }

    /// <summary>The text most models share, and per serial prefix the texts where a model differs.</summary>
    private sealed class Tables
    {
        [JsonPropertyName("hms")] public Dictionary<string, string> Hms { get; set; } = [];
        [JsonPropertyName("error")] public Dictionary<string, string> Errors { get; set; } = [];
        [JsonPropertyName("models")] public Dictionary<string, Table> Models { get; set; } = [];
        public static readonly Tables Empty = new();
    }

    private readonly Lazy<Tables> _localized;
    private readonly Lazy<Tables> _english;

    /// <summary>The catalog for <see cref="Loc.Language"/> at first use.</summary>
    public static HmsCatalog Shared => SharedInstance.Value;
    private static readonly Lazy<HmsCatalog> SharedInstance = new(() => new HmsCatalog(Loc.Language));

    /// <summary>The language whose texts this catalog shows ("en" when there's no file for the app's language).</summary>
    public string Language { get; }

    public HmsCatalog(string appLanguage)
    {
        Language = FileLanguage(appLanguage);
        _localized = new(() => Load(Language));
        _english = new(() => Language == "en" ? _localized.Value : Load("en"));
    }

    /// <summary><paramref name="model"/> is the serial's three-character prefix; null gives the text most models
    /// share.</summary>
    public string? HmsText(string key, string? model = null) => Text(key, model, hms: true);

    public string? ErrorText(string key, string? model = null) => Text(key, model, hms: false);

    /// <summary>The text for an issue's code as <see cref="PrinterIssue.Id"/> shows it: an HMS code (<c>HMS_…</c>) or
    /// a print error.</summary>
    public string? IssueText(string id, string? model = null)
    {
        var upper = id.ToUpperInvariant();
        var hex = upper.Replace("HMS_", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return upper.StartsWith("HMS_", StringComparison.Ordinal) ? HmsText(hex, model) : ErrorText(hex, model);
    }

    private string? Text(string key, string? model, bool hms)
    {
        key = key.ToUpperInvariant();
        model = model?.ToUpperInvariant();
        string? Find(Tables tables)
        {
            if (model is not null && tables.Models.TryGetValue(model, out var own)
                && (hms ? own.Hms : own.Errors).TryGetValue(key, out var modelText)) return modelText;
            return (hms ? tables.Hms : tables.Errors).GetValueOrDefault(key);
        }
        // English only when the translation lacks the code, so it's loaded only then.
        return Find(_localized.Value) ?? (Language == "en" ? null : Find(_english.Value));
    }

    private static string FilePath(string language) => Assets.PathOf("HMS", $"hms_{language}.json.deflate");

    /// <summary>The app's language if there's a catalogue for it, else English.</summary>
    private static string FileLanguage(string appLanguage) => File.Exists(FilePath(appLanguage)) ? appLanguage : "en";

    private static Tables Load(string language)
    {
        try
        {
            using var file = File.OpenRead(FilePath(language));
            using var inflate = new DeflateStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<Tables>(inflate) ?? Tables.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Tables.Empty;
        }
    }

    // MARK: Wiki links

    /// <summary>Bambu Lab's wiki page for a code on this printer model, through the redirect Bambu Studio uses: it picks
    /// the model's page and falls back to the HMS index when there isn't one. Only the serial's three-character model
    /// prefix goes along, never the serial itself. <paramref name="code"/> is the HMS code's 16 hex digits; print
    /// errors (8) have no pages of their own and get the index.</summary>
    public static Uri WikiUrl(string code, string? serial, string? language = null)
    {
        var chinese = (language ?? Loc.Language).StartsWith("zh", StringComparison.Ordinal);
        var home = new Uri($"https://wiki.bambulab.com/{(chinese ? "zh" : "en")}/hms/home");
        if (code.Length != 16) return home;
        var query = $"e={code.ToUpperInvariant()}";
        if (serial is { Length: >= 3 }) query += $"&d={Uri.EscapeDataString(serial[..3])}";
        query += $"&s=device_hms&lang={(chinese ? "zh-cn" : "en")}";
        return new Uri($"https://e.bambulab.com/index.php?{query}");
    }

    /// <summary>The links to try for a <see cref="WikiUrl"/>, best first (HmsWiki opens the first that isn't the index).
    /// The wiki files AMS codes once for every unit and slot (0700_2100 and 0701_2300 under 0700_2000), and Bambu's
    /// lookup doesn't always see that, nor a page filed under another model. So: the code for this model, then with
    /// the AMS unit and slot zeroed, then both for any model.</summary>
    public static IReadOnlyList<Uri> WikiCandidates(Uri url)
    {
        var items = QueryItems(url);
        var code = items.FirstOrDefault(i => i.Name == "e").Value;
        if (code is null) return [url];
        string[] codes = [code, AmsGeneric(code)];
        var out_ = new List<Uri>();
        foreach (var keepModel in new[] { true, false })
        {
            foreach (var c in codes)
            {
                var kept = items.Where(i => i.Name != "d" || keepModel)
                    .Select(i => i.Name == "e" ? (i.Name, Value: c) : i)
                    .Select(i => i.Value is null ? Uri.EscapeDataString(i.Name) : $"{Uri.EscapeDataString(i.Name)}={Uri.EscapeDataString(i.Value)}");
                var link = new UriBuilder(url) { Query = string.Join("&", kept) }.Uri;
                if (!out_.Contains(link)) out_.Add(link);
            }
        }
        return out_;
    }

    /// <summary>An AMS code (modules 07, 12, 18) with the unit (digits 3–4) and the slot (digit 6) zeroed.</summary>
    public static string AmsGeneric(string code)
    {
        var c = code.ToUpperInvariant().ToCharArray();
        if (c.Length != 16 || new string(c, 0, 2) is not ("07" or "12" or "18")) return code.ToUpperInvariant();
        c[2] = '0'; c[3] = '0'; c[5] = '0';
        return new string(c);
    }

    private static List<(string Name, string? Value)> QueryItems(Uri url)
    {
        var query = url.Query.TrimStart('?');
        if (query.Length == 0) return [];
        return query.Split('&').Select(part =>
        {
            var eq = part.IndexOf('=');
            return eq < 0
                ? (Uri.UnescapeDataString(part), (string?)null)
                : (Uri.UnescapeDataString(part[..eq]), Uri.UnescapeDataString(part[(eq + 1)..]));
        }).ToList();
    }
}
