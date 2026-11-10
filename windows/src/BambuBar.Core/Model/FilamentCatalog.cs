using System.Text.Json;

namespace BambuBar.Core.Model;

/// <summary>
/// Bambu Studio's filament presets and Bambu Lab colour names, so a slot reads "SUNLU PLA Matte" or "Grass Green"
/// rather than a preset id or a hex code. Generated from Bambu Studio's profiles by shared/scripts/make-filaments.py.
/// </summary>
public static class FilamentCatalog
{
    private sealed class Tables
    {
        public Dictionary<string, string[]> Presets { get; init; } = [];
        public Dictionary<string, string> Colors { get; init; } = [];
    }

    private static readonly Lazy<Tables> Data = new(Load);

    private static Tables Load()
    {
        try
        {
            using var file = File.OpenRead(Assets.PathOf("filaments.json"));
            return JsonSerializer.Deserialize<Tables>(file, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>The maker and product of a preset id (<c>tray_info_idx</c>): ("SUNLU", "PLA Matte"). Null for user presets.</summary>
    public static (string Vendor, string Product)? Preset(string id) =>
        Data.Value.Presets.TryGetValue(id.ToUpperInvariant(), out var entry) && entry.Length == 2 ? (entry[0], entry[1]) : null;

    /// <summary>Bambu Lab's name for a spool's colour: from the tag's colour code ("A01-G1" → "Grass Green"), else from
    /// the preset and the exact colour a Bambu colour has (picked from the Bambu list in Bambu Studio).</summary>
    public static string? ColorName(string tagCode, string? preset, string hex)
    {
        var colors = Data.Value.Colors;
        var parts = tagCode.ToUpperInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            var letters = new string(parts[1].TakeWhile(char.IsLetter).ToArray());
            var number = Json.Cast.ParseSwiftInt(parts[1][letters.Length..]) is { } n ? n.ToString() : "";
            if (colors.TryGetValue($"GF{parts[0]}-{letters}{number}", out var name)) return name;
        }
        if (preset is null || hex.Length < 6) return null;
        return colors.GetValueOrDefault($"{preset.ToUpperInvariant()}-{hex[..6].ToUpperInvariant()}");
    }
}
