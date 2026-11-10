using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BambuBar.Core;

/// <summary>
/// The app's texts. Keys are the Mac catalog's keys (shared/Localizable.xcstrings), written the way Swift writes
/// them: <c>Loc.F("Layer %lld of %lld", layer, total)</c> for <c>String(localized: "Layer \(layer) of \(total)")</c>,
/// so the Windows code and the catalog can be checked against each other (P4). Until a translation source is set,
/// texts come out in English, which is the key itself.
/// </summary>
public static partial class Loc
{
    /// <summary>The language the app shows, as a catalog code ("en", "de", "pt-BR", "zh-Hans").</summary>
    public static string Language { get; set; } = "en";

    /// <summary>Finds a key's text in <see cref="Language"/>; null falls back to the key (English).</summary>
    public static Func<string, string?>? Lookup { get; set; }

    /// <summary>A text without arguments. <paramref name="comment"/> documents the key, as in the catalog.</summary>
    public static string T(string key, string? comment = null) => Lookup?.Invoke(key) ?? key;

    /// <summary>A text with arguments for its <c>%lld</c>, <c>%@</c>, <c>%1$@</c>… placeholders.</summary>
    public static string F(string key, params object?[] args) => Apply(T(key), args);

    /// <summary>Fills printf-style placeholders the catalog uses with <paramref name="args"/>, in order or by position.</summary>
    public static string Apply(string format, params object?[] args)
    {
        var next = 0;
        return Placeholder().Replace(format, m =>
        {
            if (m.Groups["spec"].Value == "%") return "%";
            var index = m.Groups["pos"].Success ? int.Parse(m.Groups["pos"].Value, CultureInfo.InvariantCulture) - 1 : next++;
            var arg = index >= 0 && index < args.Length ? args[index] : null;
            return arg switch
            {
                null => "",
                double d when m.Groups["prec"].Success =>
                    d.ToString("F" + m.Groups["prec"].Value, CultureInfo.CurrentCulture),
                IFormattable f => f.ToString(null, m.Groups["spec"].Value is "@" ? CultureInfo.CurrentCulture : CultureInfo.InvariantCulture),
                _ => arg.ToString() ?? "",
            };
        });
    }

    [GeneratedRegex(@"%(?:(?<pos>[1-9][0-9]*)\$)?(?:\.(?<prec>[0-9]+))?(?<spec>lld|ld|lu|llu|d|i|u|@|lf|f|%)")]
    private static partial Regex Placeholder();
}
