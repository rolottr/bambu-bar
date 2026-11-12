using System.Globalization;
using System.Text.RegularExpressions;

namespace BambuBar.Core.Json;

/// <summary>
/// Swift's <c>as?</c> casts on <see cref="PlainJson"/> values, as the Mac's parser relies on them. Foundation hands
/// numbers over as NSNumber, so a JSON <c>true</c> casts to the integer 1, <c>25.0</c> casts to the integer 25 but
/// <c>25.5</c> doesn't, and a string never casts to a number.
/// </summary>
public static partial class Cast
{
    public static Dictionary<string, object?>? Dict(object? value) => value as Dictionary<string, object?>;

    /// <summary><c>as? [[String: Any]]</c>: every element must be an object, else nil (an empty array is fine).</summary>
    public static List<Dictionary<string, object?>>? DictArray(object? value)
    {
        if (value is not List<object?> list) return null;
        var out_ = new List<Dictionary<string, object?>>(list.Count);
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> d) return null;
            out_.Add(d);
        }
        return out_;
    }

    /// <summary><c>as? [String]</c>: every element must be a string.</summary>
    public static List<string>? StringArray(object? value)
    {
        if (value is not List<object?> list) return null;
        var out_ = new List<string>(list.Count);
        foreach (var item in list)
        {
            if (item is not string s) return null;
            out_.Add(s);
        }
        return out_;
    }

    /// <summary><c>as? Int</c>: an integer, a double with no fraction within range, or a bool (1 or 0).</summary>
    public static long? AsInt(object? value) => value switch
    {
        long l => l,
        double d when ExactLong(d) is { } l => l,
        bool b => b ? 1 : 0,
        _ => null,
    };

    /// <summary><c>as? Double</c>: any number, or a bool (1 or 0).</summary>
    public static double? AsDouble(object? value) => value switch
    {
        double d => d,
        long l => l,
        bool b => b ? 1 : 0,
        _ => null,
    };

    /// <summary><c>as? Bool</c>: a bool, or the numbers 0 and 1.</summary>
    public static bool? AsBool(object? value) => value switch
    {
        bool b => b,
        long l when l is 0 or 1 => l == 1,
        double d when d is 0 or 1 => d == 1,
        _ => null,
    };

    public static string? AsString(object? value) => value as string;

    /// <summary>NSNumber's <c>stringValue</c> for numbers and bools; strings as they are.</summary>
    public static string? Describe(object? value) => value switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => ExactLong(d) is { } l ? l.ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        _ => null,
    };

    /// <summary><c>Int(exactly:)</c>: the double as a long when it has no fraction and fits, else null.</summary>
    public static long? ExactLong(double d) =>
        double.IsFinite(d) && Math.Floor(d) == d && d >= -9.2233720368547758E18 && d < 9.2233720368547758E18 ? (long)d : null;

    /// <summary>Swift's <c>Int(String)</c>: an optional sign and decimal digits, nothing else, within range.</summary>
    public static long? ParseSwiftInt(string text) =>
        IntPattern().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Swift's <c>Double(String)</c>: decimal or hexadecimal numbers, "inf", "infinity" and "nan" in any case,
    /// with an optional sign and no spaces. Non-finite values are returned; callers that need finite ones check.</summary>
    public static double? ParseSwiftDouble(string text)
    {
        if (text.Length == 0) return null;
        var sign = 1.0;
        var body = text.AsSpan();
        if (body[0] is '+' or '-')
        {
            if (body[0] == '-') sign = -1;
            body = body[1..];
        }
        if (body.Equals("inf", StringComparison.OrdinalIgnoreCase) || body.Equals("infinity", StringComparison.OrdinalIgnoreCase))
            return sign * double.PositiveInfinity;
        if (body.Equals("nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;
        if (body.Length > 2 && body[0] == '0' && body[1] is 'x' or 'X')
            return ulong.TryParse(body[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? sign * hex : null;
        if (!DecimalPattern().IsMatch(body)) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Swift's <c>UInt64(String, radix: 16)</c>: hex digits with an optional "+", no "0x", within range.</summary>
    public static ulong? ParseHex(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var body = text[0] == '+' ? text.AsSpan(1) : text.AsSpan();
        if (body.Length == 0) return null;
        foreach (var c in body) if (!char.IsAsciiHexDigit(c)) return null;
        return ulong.TryParse(body, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"^[+-]?[0-9]+$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$")]
    private static partial Regex DecimalPattern();
}
