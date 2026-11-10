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

    [GeneratedRegex(@"Loc\.[TF]\(""(?<key>(?:[^""\\]|\\.)*)""")]
    private static partial Regex LocCall();
}
