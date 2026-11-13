using System.Globalization;
using System.Text.RegularExpressions;

namespace BambuBar.Linux;

// Read only bounded TOML scalar data. Never load a theme's CSS, Lua or executable files.
internal sealed record OmarchyPalette(string Background, string Foreground, string Accent,
    string Selection, string Border, string Surface, string Secondary, string Red, string Yellow,
    string Green, bool Dark)
{
    internal static Dictionary<string, string> Scalars(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";
        foreach (var line in text.Split('\n'))
        {
            var heading = Regex.Match(line, @"^\s*\[([a-zA-Z0-9_-]+)\]\s*(?:#.*)?$");
            if (heading.Success) { section = heading.Groups[1].Value + "."; continue; }
            var match = Regex.Match(line, "^\\s*([a-zA-Z0-9_-]+)\\s*=\\s*[\"']([^\"']*)[\"']\\s*(?:#.*)?$");
            if (match.Success) values[section + match.Groups[1].Value] = match.Groups[2].Value;
        }
        return values;
    }
    public static OmarchyPalette? Parse(string colors, string shell, int appearance)
    {
        var c = Scalars(colors); var s = Scalars(shell);
        string? Color(Dictionary<string, string> source, string key) => source.TryGetValue(key, out var value) && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value : null;
        var bg = Color(c, "background"); var fg = Color(c, "foreground"); var accent = Color(c, "accent");
        if (bg is null || fg is null || accent is null || Contrast(bg, fg) < 3) return null;
        var dark = c.GetValueOrDefault("mode") switch { "dark" => true, "light" => false, _ => Luminance(bg) < .4 };
        var desiredDark = appearance == 2 ? dark : appearance == 0;
        // Opposite explicit mode is an adaptation of this palette, not another hard-coded theme.
        var adapted = desiredDark != dark;
        if (adapted) (bg, fg) = (fg, bg);
        fg = Readable(fg, bg);
        var surface = adapted ? Mix(bg, fg, .045) : Color(c, "lighter_background") ?? Mix(bg, fg, .045);
        if (Contrast(surface, fg) < 4.5) surface = Mix(bg, fg, .045);
        var selection = adapted ? Mix(bg, accent, .2) : Color(c, "selection") ?? Mix(bg, accent, .2);
        var border = Color(s, "controls.normal-border") ?? Color(c, "border") ?? Color(c, "muted") ?? Mix(bg, fg, .3);
        var borderAlpha = Regex.Match(shell, @"(?m)^\s*normal-border-alpha\s*=\s*(0(?:\.\d+)?|1(?:\.0+)?)\s*(?:#.*)?$");
        if (borderAlpha.Success && double.TryParse(borderAlpha.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)) border = Mix(bg, border, alpha);
        if (Contrast(border, bg) < 1.5) border = Mix(bg, fg, .35);
        return new(bg, fg, accent, selection, border, surface, Readable(Mix(bg, fg, .75), bg),
            Readable(Color(c, "red") ?? "#c01c28", bg), Readable(Color(c, "yellow") ?? "#ae7b03", bg),
            Readable(Color(c, "green") ?? "#26a269", bg), desiredDark);
    }
    public string Css => $$"""
        window.theme-omarchy {
          --window-bg-color: {{Background}}; --window-fg-color: {{Foreground}};
          --view-bg-color: {{Background}}; --view-fg-color: {{Foreground}};
          --headerbar-bg-color: {{Background}}; --headerbar-fg-color: {{Foreground}};
          --headerbar-backdrop-color: {{Background}};
          --sidebar-bg-color: {{Background}}; --sidebar-fg-color: {{Foreground}};
          --card-bg-color: {{Surface}}; --card-fg-color: {{Foreground}};
          --popover-bg-color: {{Surface}}; --popover-fg-color: {{Foreground}};
          --dialog-bg-color: {{Background}}; --dialog-fg-color: {{Foreground}};
          --accent-bg-color: {{Accent}}; --accent-fg-color: {{Ink(Accent)}}; --accent-color: {{Readable(Accent, Background)}};
          --success-color: {{Green}}; --warning-color: {{Yellow}}; --error-color: {{Red}};
          --omarchy-border: {{Border}}; --omarchy-selection: {{Selection}};
          --omarchy-selection-fg: {{Readable(Foreground, Selection)}}; --omarchy-secondary: {{Secondary}};
        }
        """;
    private static double Channel(string hex, int offset) => int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber) / 255.0;
    internal static double Luminance(string hex)
    {
        double Linear(double x) => x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4);
        return .2126 * Linear(Channel(hex, 1)) + .7152 * Linear(Channel(hex, 3)) + .0722 * Linear(Channel(hex, 5));
    }
    internal static double Contrast(string a, string b) => (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
    private static string Ink(string background) => Contrast("#ffffff", background) > Contrast("#000000", background) ? "#ffffff" : "#000000";
    private static string Readable(string foreground, string background)
    {
        var ink = Ink(background);
        for (var i = 0; i <= 20; i++) { var next = Mix(foreground, ink, i / 20.0); if (Contrast(next, background) >= 4.5) return next; }
        return ink;
    }
    private static string Mix(string a, string b, double amount) => "#" + string.Concat(new[] { 1, 3, 5 }.Select(offset => ((int)Math.Round(255 * (Channel(a, offset) * (1 - amount) + Channel(b, offset) * amount))).ToString("x2")));
}
