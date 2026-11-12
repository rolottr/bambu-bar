using System.Globalization;

namespace BambuBar.Core;

/// <summary>Durations, finish times, weights and temperatures as the app shows them, in the current culture.</summary>
public static class Format
{
    /// <summary>"2h 05m", "45m", "&lt;1m", "1d 3h".</summary>
    public static string Duration(int minutes)
    {
        if (minutes <= 0) return Loc.T("<1m", "Less than a minute, short");
        int h = minutes / 60, m = minutes % 60;
        if (h == 0) return Loc.F("%lldm", m);
        if (h >= 24) return Loc.F("%lldd %lldh", h / 24, h % 24);
        return Loc.F("%lldh %@m", h, m.ToString("00", CultureInfo.InvariantCulture));
    }

    public static string Duration(TimeSpan span) =>
        Duration((int)Math.Round(span.TotalSeconds / 60, MidpointRounding.AwayFromZero));

    /// <summary>"14:32", "tomorrow 08:10", "Wed 14:32": when a job ends, in local time.</summary>
    public static string Finish(DateTimeOffset date, DateTimeOffset? now = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = date.ToLocalTime().DateTime;
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().DateTime.Date;
        var time = local.ToString("t", culture);
        if (local.Date == today) return time;
        if (local.Date == today.AddDays(1)) return Loc.F("tomorrow %@", time);
        return local.ToString("ddd ", culture) + time;
    }

    public static string Temperature(double value) =>
        $"{(long)Math.Round(value, MidpointRounding.AwayFromZero)}°";

    /// <summary>"≈ 810 g", "1.25 kg"; <paramref name="approximate"/> for measured amounts (the AMS estimates what's
    /// left).</summary>
    public static string Grams(int value, bool approximate = true, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var text = value >= 1000
            ? (value / 1000.0).ToString("0.##", culture) + " kg"
            : value.ToString(culture) + " g";
        return approximate ? "≈ " + text : text;
    }
}
