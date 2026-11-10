using System.Text.Json;

namespace BambuBar.Core;

/// <summary>
/// The Mac's string catalog (shared/Localizable.xcstrings, copied to Assets\), read at run time: one file of
/// translations for both apps, no generated resources. <see cref="Use"/> points <see cref="Loc"/> at a language.
/// </summary>
public sealed class Strings
{
    /// <summary>The catalog's languages, as the Mac names them.</summary>
    public IReadOnlyList<string> Languages { get; }

    private readonly Dictionary<string, Entry> _entries;

    private sealed record Entry(Dictionary<string, string> Plain, Dictionary<string, Dictionary<string, string>> Plurals);

    private Strings(Dictionary<string, Entry> entries, IReadOnlyList<string> languages)
    {
        _entries = entries;
        Languages = languages;
    }

    public static Strings Load(string? path = null)
    {
        path ??= Assets.PathOf("Localizable.xcstrings");
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var languages = new SortedSet<string>(StringComparer.Ordinal) { "en" };
        if (!File.Exists(path)) return new(entries, [.. languages]);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var key in doc.RootElement.GetProperty("strings").EnumerateObject())
        {
            var plain = new Dictionary<string, string>(StringComparer.Ordinal);
            var plurals = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (key.Value.TryGetProperty("localizations", out var localizations))
            {
                foreach (var loc in localizations.EnumerateObject())
                {
                    languages.Add(loc.Name);
                    if (loc.Value.TryGetProperty("stringUnit", out var unit) && unit.TryGetProperty("value", out var value))
                        plain[loc.Name] = value.GetString() ?? "";
                    if (loc.Value.TryGetProperty("variations", out var variations) && variations.TryGetProperty("plural", out var plural))
                    {
                        plurals[loc.Name] = plural.EnumerateObject()
                            .Where(c => c.Value.TryGetProperty("stringUnit", out _))
                            .ToDictionary(c => c.Name, c => c.Value.GetProperty("stringUnit").GetProperty("value").GetString() ?? "");
                    }
                }
            }
            entries[key.Name] = new(plain, plurals);
        }
        return new(entries, [.. languages]);
    }

    /// <summary>The text for <paramref name="key"/> in <paramref name="language"/>, else English, else null (the key).
    /// Plural keys answer with their "other" form; <see cref="Plural"/> picks the right one.</summary>
    public string? Text(string key, string language)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        foreach (var lang in new[] { language, "en" })
        {
            if (entry.Plain.TryGetValue(lang, out var text)) return text;
            if (entry.Plurals.TryGetValue(lang, out var forms) && forms.TryGetValue("other", out var other)) return other;
        }
        return null;
    }

    /// <summary>The plural form of <paramref name="key"/> for <paramref name="count"/>, as Apple's catalogs pick it (CLDR).</summary>
    public string? Plural(string key, long count, string language)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        foreach (var lang in new[] { language, "en" })
        {
            if (entry.Plurals.TryGetValue(lang, out var forms))
            {
                var category = PluralCategory(lang, count);
                return forms.GetValueOrDefault(category) ?? forms.GetValueOrDefault("other");
            }
            if (entry.Plain.TryGetValue(lang, out var text)) return text;
        }
        return null;
    }

    /// <summary>CLDR's cardinal category for a whole number in the app's languages.</summary>
    public static string PluralCategory(string language, long n)
    {
        var i = Math.Abs(n);
        long mod10 = i % 10, mod100 = i % 100;
        switch (language.Split('-')[0])
        {
            case "ja" or "ko" or "zh":
                return "other";
            case "fr" or "pt":
                return i is 0 or 1 ? "one" : "other";
            case "cs":
                return i == 1 ? "one" : i is >= 2 and <= 4 ? "few" : "other";
            case "pl":
                if (i == 1) return "one";
                return mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? "few" : "many";
            case "ru" or "uk":
                if (mod10 == 1 && mod100 != 11) return "one";
                return mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? "few" : "many";
            default: // en, de, es, hu, it, nl, sv, tr
                return i == 1 ? "one" : "other";
        }
    }

    /// <summary>Shows the app in <paramref name="language"/> from now on (a catalog code; "en" when it has none).</summary>
    public void Use(string language)
    {
        Loc.Language = Languages.Contains(language) ? language : "en";
        Loc.Lookup = key => Text(key, Loc.Language);
        Loc.PluralLookup = (key, count) => Plural(key, count, Loc.Language);
    }

    /// <summary>The catalog language for a Windows UI culture: "de-AT" → "de", "pt-PT" → "pt-BR", "zh-TW" → "zh-Hant".</summary>
    public string Match(System.Globalization.CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (Languages.Contains(c.Name)) return c.Name;
            if (c.Name is "zh-TW" or "zh-HK" or "zh-MO" or "zh-Hant") return "zh-Hant";
            if (c.Name is "zh-CN" or "zh-SG" or "zh-Hans") return "zh-Hans";
            if (c.TwoLetterISOLanguageName == "pt" && Languages.Contains("pt-BR")) return "pt-BR";
        }
        return "en";
    }
}
