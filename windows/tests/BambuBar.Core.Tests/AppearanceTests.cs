using BambuBar.Core.App;

namespace BambuBar.Core.Tests;

/// <summary>Settings → Appearance: the saved values read back, unknown ones fall back to the defaults, and every accent
/// keeps its text readable.</summary>
public class AppearanceTests
{
    [Fact]
    public void ModesRoundTrip()
    {
        foreach (var mode in Enum.GetValues<AppearanceMode>()) Assert.Equal(mode, Appearance.ParseMode(mode.RawValue()));
        Assert.Equal("system", AppearanceMode.System.RawValue());
        Assert.Equal("light", AppearanceMode.Light.RawValue());
        Assert.Equal("dark", AppearanceMode.Dark.RawValue());
    }

    [Fact]
    public void StylesRoundTrip()
    {
        foreach (var style in Enum.GetValues<AppearanceStyle>()) Assert.Equal(style, Appearance.ParseStyle(style.RawValue()));
        Assert.Equal("windows", AppearanceStyle.Windows.RawValue());
        Assert.Equal("glass", AppearanceStyle.Glass.RawValue());
    }

    [Fact]
    public void AccentsRoundTrip()
    {
        foreach (var accent in Enum.GetValues<AccentChoice>()) Assert.Equal(accent, Appearance.ParseAccent(accent.RawValue()));
        Assert.Equal(["bambu", "system", "blue", "purple", "pink", "orange", "graphite"],
            Enum.GetValues<AccentChoice>().Select(a => a.RawValue()));
    }

    /// <summary>A missing key, a typo, another case or the Mac's own style names fall back to the defaults.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Dark")]
    [InlineData("classic")]
    [InlineData("liquid")]
    public void UnknownValuesAreTheDefaults(string? raw)
    {
        Assert.Equal(AppearanceMode.System, Appearance.ParseMode(raw));
        Assert.Equal(AppearanceStyle.Windows, Appearance.ParseStyle(raw));
        Assert.Equal(AccentChoice.Bambu, Appearance.ParseAccent(raw));
    }

    [Fact]
    public void SettingsKeepTheValues()
    {
        var settings = new AppSettings(null);
        Assert.Equal(AppearanceStyle.Windows, Appearance.ParseStyle(settings.Get<string>(AppSettings.Keys.AppearanceStyle)));
        settings.Set(AppSettings.Keys.AppearanceStyle, AppearanceStyle.Glass.RawValue());
        settings.Set(AppSettings.Keys.AccentColor, AccentChoice.Pink.RawValue());
        Assert.Equal(AppearanceStyle.Glass, Appearance.ParseStyle(settings.Get<string>(AppSettings.Keys.AppearanceStyle)));
        Assert.Equal(AccentChoice.Pink, Appearance.ParseAccent(settings.Get<string>(AppSettings.Keys.AccentColor)));
    }

    [Fact]
    public void BambuKeepsTodaysGreens()
    {
        var bambu = AccentChoice.Bambu.Preset()!.Value;
        Assert.Equal(0x21CC6Bu, bambu.Fill(dark: true));
        Assert.Equal(0x007A3Du, bambu.Fill(dark: false));
        Assert.Equal(0x00994Au, bambu.Deep);
        // As before: near-black on the bright green, white on the deep one.
        Assert.True(bambu.DarkTextOn(dark: true));
        Assert.False(bambu.DarkTextOn(dark: false));
        Assert.Null(AccentChoice.System.Preset());
    }

    /// <summary>Text on an accent fill keeps WCAG AA (4.5:1) in both themes, for every preset.</summary>
    [Fact]
    public void TextOnEveryAccentFillReads()
    {
        foreach (var accent in Enum.GetValues<AccentChoice>())
        {
            if (accent.Preset() is not { } shades) continue;
            foreach (var dark in new[] { false, true })
            {
                var fill = shades.Fill(dark);
                var text = shades.DarkTextOn(dark) ? 0x000000u : 0xFFFFFFu;
                Assert.True(Appearance.Contrast(fill, text) >= 4.5, $"{accent} {(dark ? "dark" : "light")}: {Appearance.Contrast(fill, text):0.00}");
            }
        }
    }

    [Fact]
    public void ContrastIsWcags()
    {
        Assert.Equal(21, Appearance.Contrast(0x000000, 0xFFFFFF), 3);
        Assert.Equal(1, Appearance.Contrast(0x777777, 0x777777), 3);
        Assert.True(Appearance.PrefersDarkText(0xFFFF00));
        Assert.False(Appearance.PrefersDarkText(0x0000FF));
    }

    /// <summary>Windows' default blue: Explorer's palette gives Light2 for dark and Dark1 for light.</summary>
    [Fact]
    public void SystemAccentFromThePalette()
    {
        uint[] colors = [0x99EBFF, 0x4CC2FF, 0x0091F8, 0x0078D4, 0x005FB8, 0x004275, 0x002642, 0x000000];
        var palette = colors.SelectMany(c => new[] { (byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)0xFF }).ToArray();
        var shades = Appearance.FromWindows(palette, dwmAbgr: unchecked((int)0xFFD47800));
        Assert.Equal(0x4CC2FFu, shades.Bright);
        Assert.Equal(0x005FB8u, shades.Deep);
        Assert.Equal(0x005FB8u, shades.Fill(dark: false));
    }

    /// <summary>Without the palette, DWM's ABGR colour, lightened for dark and darkened for light.</summary>
    [Fact]
    public void SystemAccentFromDwm()
    {
        var shades = Appearance.FromWindows(null, dwmAbgr: unchecked((int)0xFFD47800));
        Assert.Equal(Appearance.Mix(0x0078D4, 0xFFFFFF, 0.4), shades.Bright);
        Assert.Equal(Appearance.Mix(0x0078D4, 0x000000, 0.2), shades.Deep);
        Assert.True(Luma(shades.Bright) > Luma(0x0078D4) && Luma(shades.Deep) < Luma(0x0078D4));
    }

    [Fact]
    public void SystemAccentFallsBackToBambu()
    {
        Assert.Equal(Appearance.Bambu, Appearance.FromWindows(null, null));
        Assert.Equal(Appearance.Bambu, Appearance.FromWindows([1, 2, 3], null));
    }

    private static double Luma(uint rgb) => Appearance.Luminance(rgb);
}
