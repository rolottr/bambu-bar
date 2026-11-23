namespace BambuBar.Core.App;

/// <summary>Light or dark (Settings → Appearance → Mode): Windows' "Choose your app mode", or always one of them.
/// High contrast wins over all three.</summary>
public enum AppearanceMode { System, Light, Dark }

/// <summary>How the app draws its surfaces: like Windows 11's own apps (flat Fluent fills, Fluent corners), or the Mac
/// version's soft gradients and larger radii ("Bambu Glass").</summary>
public enum AppearanceStyle { Windows, Glass }

/// <summary>The colour of buttons, switches, links and selections. Printer states keep their own colours: a printing
/// printer is Bambu green whatever the accent.</summary>
public enum AccentChoice { Bambu, System, Blue, Purple, Pink, Orange, Graphite }

/// <summary>
/// An accent's shades, as <c>0xRRGGBB</c>: <see cref="Bright"/> for the dark theme, dots and tints; <see cref="Deep"/>
/// for deep fills; <see cref="LightFill"/> for accent fills in the light theme (the deep shade, except Bambu green's,
/// which has filled light-theme buttons with #007A3D since 2.0).
/// </summary>
public readonly record struct AccentShades(uint Bright, uint Deep, uint LightFill)
{
    public AccentShades(uint bright, uint deep) : this(bright, deep, deep) { }

    /// <summary>The fill of accent buttons, switches and radios in this theme (Fluent: AccentLight2 on dark, AccentDark1
    /// on light).</summary>
    public uint Fill(bool dark) => dark ? Bright : LightFill;

    /// <summary>Text on <see cref="Fill"/> is black (else white), whichever reads better (Fluent's TextOnAccent).</summary>
    public bool DarkTextOn(bool dark) => Appearance.PrefersDarkText(Fill(dark));
}

/// <summary>Settings → Appearance: the raw values saved (the Mac's keys and values, so the two read alike), their
/// titles, the accent presets, and reading Windows' own accent colour.</summary>
public static class Appearance
{
    public static string RawValue(this AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => "light", AppearanceMode.Dark => "dark", _ => "system",
    };

    /// <summary>Unknown or missing values follow Windows.</summary>
    public static AppearanceMode ParseMode(string? raw) => raw switch
    {
        "light" => AppearanceMode.Light, "dark" => AppearanceMode.Dark, _ => AppearanceMode.System,
    };

    public static string Title(this AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => Loc.T("Always Light"),
        AppearanceMode.Dark => Loc.T("Always Dark"),
        _ => Loc.T("Match System"),
    };

    public static string RawValue(this AppearanceStyle style) => style == AppearanceStyle.Glass ? "glass" : "windows";

    /// <summary>Unknown or missing values (the Mac's "classic", say) are the Windows look.</summary>
    public static AppearanceStyle ParseStyle(string? raw) => raw == "glass" ? AppearanceStyle.Glass : AppearanceStyle.Windows;

    public static string Title(this AppearanceStyle style) => style == AppearanceStyle.Glass
        ? Loc.T("Bambu Glass", "Appearance style: the Mac version's soft gradients")
        : Loc.T("Windows", "Appearance style: like the apps that come with Windows 11");

    public static string RawValue(this AccentChoice accent) => accent switch
    {
        AccentChoice.System => "system",
        AccentChoice.Blue => "blue",
        AccentChoice.Purple => "purple",
        AccentChoice.Pink => "pink",
        AccentChoice.Orange => "orange",
        AccentChoice.Graphite => "graphite",
        _ => "bambu",
    };

    /// <summary>Unknown or missing values are Bambu green.</summary>
    public static AccentChoice ParseAccent(string? raw) => raw switch
    {
        "system" => AccentChoice.System,
        "blue" => AccentChoice.Blue,
        "purple" => AccentChoice.Purple,
        "pink" => AccentChoice.Pink,
        "orange" => AccentChoice.Orange,
        "graphite" => AccentChoice.Graphite,
        _ => AccentChoice.Bambu,
    };

    public static string Title(this AccentChoice accent) => accent switch
    {
        AccentChoice.System => Loc.T("System Accent"),
        AccentChoice.Blue => Loc.T("Blue", "Accent colour"),
        AccentChoice.Purple => Loc.T("Purple", "Accent colour"),
        AccentChoice.Pink => Loc.T("Pink", "Accent colour"),
        AccentChoice.Orange => Loc.T("Orange", "Accent colour"),
        AccentChoice.Graphite => Loc.T("Graphite", "Accent colour"),
        _ => Loc.T("Bambu Green"),
    };

    /// <summary>Bambu Bar's green: the Mac's accent (bright) and accentDeep.</summary>
    public static readonly AccentShades Bambu = new(0x21CC6B, 0x00994A, 0x007A3D);

    /// <summary>The preset's shades (the Mac's, so both apps match); null for <see cref="AccentChoice.System"/>, which
    /// <see cref="FromWindows"/> reads.</summary>
    public static AccentShades? Preset(this AccentChoice accent) => accent switch
    {
        AccentChoice.Bambu => Bambu,
        AccentChoice.Blue => new AccentShades(0x0A84FF, 0x0062CC),
        AccentChoice.Purple => new AccentShades(0xBF5AF2, 0x8944AB),
        AccentChoice.Pink => new AccentShades(0xFF375F, 0xD12B4C),
        AccentChoice.Orange => new AccentShades(0xFF9F0A, 0xC76E00),
        AccentChoice.Graphite => new AccentShades(0x98989D, 0x5E5E63),
        _ => null,
    };

    /// <summary>
    /// Windows' accent: Explorer's <c>AccentPalette</c> (eight RGBA colours, lightest first: Light3, Light2, Light1, the
    /// accent, Dark1, Dark2, Dark3), whose Light2 and Dark1 are what Fluent fills with on dark and light; else DWM's
    /// <c>AccentColor</c> (ABGR), lightened and darkened about as much; else Bambu green.
    /// </summary>
    public static AccentShades FromWindows(byte[]? palette, int? dwmAbgr)
    {
        if (palette is { Length: >= 28 })
        {
            static uint At(byte[] p, int i) => (uint)(p[4 * i] << 16 | p[4 * i + 1] << 8 | p[4 * i + 2]);
            return new AccentShades(At(palette, 1), At(palette, 4));
        }
        if (dwmAbgr is { } abgr)
        {
            var v = unchecked((uint)abgr);
            var rgb = (v & 0xFF) << 16 | (v & 0xFF00) | (v >> 16 & 0xFF);
            return new AccentShades(Mix(rgb, 0xFFFFFF, 0.4), Mix(rgb, 0x000000, 0.2));
        }
        return Bambu;
    }

    /// <summary><paramref name="from"/> moved <paramref name="amount"/> (0–1) of the way to <paramref name="to"/>.</summary>
    public static uint Mix(uint from, uint to, double amount)
    {
        uint Channel(int shift)
        {
            double a = from >> shift & 0xFF, b = to >> shift & 0xFF;
            return (uint)Math.Round(a + (b - a) * Math.Clamp(amount, 0, 1)) << shift;
        }
        return Channel(16) | Channel(8) | Channel(0);
    }

    /// <summary>WCAG's relative luminance of an <c>0xRRGGBB</c> colour, 0 (black) to 1 (white).</summary>
    public static double Luminance(uint rgb)
    {
        static double Linear(uint c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(rgb >> 16 & 0xFF) + 0.7152 * Linear(rgb >> 8 & 0xFF) + 0.0722 * Linear(rgb & 0xFF);
    }

    /// <summary>WCAG's contrast ratio between two colours, 1 to 21.</summary>
    public static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Black text reads better than white on <paramref name="fill"/>.</summary>
    public static bool PrefersDarkText(uint fill) => Contrast(fill, 0x000000) >= Contrast(fill, 0xFFFFFF);
}
