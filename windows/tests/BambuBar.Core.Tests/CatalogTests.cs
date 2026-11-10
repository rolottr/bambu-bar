using System.Text.Json;
using System.Text.RegularExpressions;
using BambuBar.Core.Model;

namespace BambuBar.Core.Tests;

/// <summary>The texts Core uses exist in the shared catalog, and the data catalogs load.</summary>
public partial class CatalogTests
{
    /// <summary>Every <c>Loc.T("…")</c> and <c>Loc.F("…")</c> key in Core is a key of shared/Localizable.xcstrings,
    /// so a typo can't ship an untranslated text.</summary>
    [Fact]
    public void EveryCoreTextIsInTheCatalog()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Repo.Shared("Localizable.xcstrings")));
        var keys = catalog.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name).ToHashSet();
        // Windows-only texts waiting to join the catalog with their translations (P4.2).
        keys.UnionWith(File.ReadAllLines(Path.Combine(Repo.Root, "windows", "tools", "windows-strings.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#')));
        var sources = Directory.GetFiles(Path.Combine(Repo.Root, "windows", "src", "BambuBar.Core"), "*.cs", SearchOption.AllDirectories);
        var missing = new List<string>();
        var count = 0;
        foreach (var file in sources)
        {
            foreach (Match m in LocCall().Matches(File.ReadAllText(file)))
            {
                count++;
                var key = Regex.Unescape(m.Groups["key"].Value);
                if (!keys.Contains(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.True(count > 40, $"only {count} texts found: is the pattern still right?");
        Assert.Empty(missing);
    }

    /// <summary>Keys inside arrays and dictionaries that Loc.T reads later (stage names, pause reasons).</summary>
    [Fact]
    public void StageNamesAndPauseReasonsAreInTheCatalog()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Repo.Shared("Localizable.xcstrings")));
        var keys = catalog.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(78, PrinterStatus.StageCount);
        for (var i = 0; i < PrinterStatus.StageCount; i++) Assert.Contains(PrinterStatus.StageName(i)!, keys);
        foreach (var stage in new[] { 6, 16, 17, 20, 21, 23, 26, 27, 28, 30, 32, 33, 34, 35 })
        {
            Assert.Contains(PrinterStatus.PauseReasonText(stage)!, keys);
        }
    }

    [Fact]
    public void HmsTextsInflateFromRawDeflate()
    {
        var english = new HmsCatalog("en");
        // A P2S's "AMS A slot 1 filament has run out" style code: any code works, as long as the tables load.
        Assert.NotNull(english.ErrorText("0300400D") ?? english.HmsText("0300010000010007"));
        var german = new HmsCatalog("de");
        Assert.Equal("de", german.Language);
        // Languages Bambu's HMS service doesn't offer fall back to English; Traditional Chinese reads Simplified.
        Assert.Equal("en", new HmsCatalog("cs").Language);
        Assert.Equal("zh-Hans", new HmsCatalog("zh-Hant").Language);
    }

    [Fact]
    public void FilamentPresetsAndColourNames()
    {
        Assert.Equal(("Bambu Lab", "PLA Basic"), FilamentCatalog.Preset("gfa00"));
        Assert.Equal("Black", FilamentCatalog.ColorName("", "GFA00", "000000FF"));
        Assert.Null(FilamentCatalog.Preset("nonexistent"));
    }

    /// <summary>The catalog read at run time: translations, English fallback, plural forms.</summary>
    [Fact]
    public void TranslationsAndPluralsComeFromTheSharedCatalog()
    {
        var strings = Strings.Load();
        Assert.Contains("de", strings.Languages);
        Assert.Equal(18, strings.Languages.Count);
        Assert.Equal("Schicht 170 von 305", Loc.Apply(strings.Text("Layer %lld of %lld", "de")!, 170, 305));
        // English values may be positional ("Layer %1$lld of %2$lld").
        Assert.Equal("Layer 170 of 305", Loc.Apply(strings.Text("Layer %lld of %lld", "en")!, 170, 305));
        Assert.Null(strings.Text("Not a key", "de"));
        Assert.Equal("%lld stupeň", strings.Plural("%lld degrees", 1, "cs"));
        Assert.Equal("%lld stupně", strings.Plural("%lld degrees", 3, "cs"));
        Assert.Equal("%lld stupňů", strings.Plural("%lld degrees", 5, "cs"));
        Assert.Equal("many", Strings.PluralCategory("ru", 11));
        Assert.Equal("one", Strings.PluralCategory("ru", 21));
        Assert.Equal("few", Strings.PluralCategory("pl", 22));
        Assert.Equal("one", Strings.PluralCategory("fr", 0));
        Assert.Equal("pt-BR", strings.Match(new System.Globalization.CultureInfo("pt-PT")));
        Assert.Equal("zh-Hant", strings.Match(new System.Globalization.CultureInfo("zh-TW")));
        Assert.Equal("de", strings.Match(new System.Globalization.CultureInfo("de-AT")));
        Assert.Equal("en", strings.Match(new System.Globalization.CultureInfo("fi-FI")));
    }

    /// <summary>Every language the catalog has, every key with an argument: the placeholders survive translation, so
    /// <see cref="Loc.Apply"/> fills them all (a translation that drops or mangles one would show "%@").</summary>
    [Fact]
    public void TranslationsKeepTheirPlaceholders()
    {
        var strings = Strings.Load();
        var bad = new List<string>();
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Repo.Shared("Localizable.xcstrings")));
        foreach (var key in catalog.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name))
        {
            var expected = Placeholder().Matches(key).Count;
            if (expected == 0) continue;
            foreach (var lang in strings.Languages)
            {
                var text = strings.Text(key, lang) ?? key;
                var filled = Loc.Apply(text, Enumerable.Repeat<object?>("X", expected).ToArray());
                if (Placeholder().IsMatch(filled)) bad.Add($"{lang}: {key} → {text}");
            }
        }
        Assert.Empty(bad);
    }

    [GeneratedRegex(@"%(?:[1-9]\$)?(?:lld|ld|d|@|lf|f)")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"Loc\.[TF]\(""(?<key>(?:[^""\\]|\\.)*)""")]
    private static partial Regex LocCall();
}
