using System.Globalization;
using System.Text.Json;

namespace BambuBar.Core.Json;

/// <summary>
/// JSON as plain values, the way Foundation's JSONSerialization hands it to the Mac app: objects become
/// <c>Dictionary&lt;string, object?&gt;</c>, arrays <c>List&lt;object?&gt;</c>, numbers <c>long</c> (no fraction or
/// exponent, within range) or <c>double</c>, plus <c>string</c>, <c>bool</c> and <c>null</c>. The printer's reports
/// are loose (numbers as strings, ids as numbers or strings), so the parser reads them through <see cref="Cast"/>,
/// which follows Swift's <c>as?</c> rules, rather than through typed models.
/// </summary>
public static class PlainJson
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 256 };

    /// <summary>Parses UTF-8 JSON. Throws <see cref="JsonException"/> when it isn't JSON.</summary>
    public static object? Parse(ReadOnlyMemory<byte> utf8)
    {
        using var doc = JsonDocument.Parse(utf8, Options);
        return Convert(doc.RootElement);
    }

    public static object? Parse(string text)
    {
        using var doc = JsonDocument.Parse(text, Options);
        return Convert(doc.RootElement);
    }

    /// <summary>A JSON object, or null when the text isn't one.</summary>
    public static Dictionary<string, object?>? ParseObject(ReadOnlyMemory<byte> utf8)
    {
        try { return Parse(utf8) as Dictionary<string, object?>; }
        catch (JsonException) { return null; }
    }

    private static object? Convert(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) dict[p.Name] = Convert(p.Value);
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>(e.GetArrayLength());
                foreach (var item in e.EnumerateArray()) list.Add(Convert(item));
                return list;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                var raw = e.GetRawText();
                if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0 && e.TryGetInt64(out var l)) return l;
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }
}
