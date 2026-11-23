using System.Windows;
using BambuBar.Core.App;
using Microsoft.Win32;

namespace BambuBar.Theme;

public enum ThemeVariant { Light, Dark, HighContrast }

/// <summary>Everything a view is built for: light, dark or high contrast, the style and the accent. A view rebuilds when
/// it changes.</summary>
public readonly record struct ThemeLook(ThemeVariant Variant, AppearanceStyle Style, AccentShades Accent);

/// <summary>
/// The app's theme: Settings → Appearance (mode, style, accent) over Windows' own ("Choose your app mode":
/// <c>AppsUseLightTheme</c>, the accent colour), high contrast over everything, and the animation setting, with a
/// notification when any of them changes. The taskbar follows the other switch (<c>SystemUsesLightTheme</c>), which
/// only the tray glyph and its menu read.
/// </summary>
public static class AppTheme
{
    private static bool _listening;
    private static ThemeLook? _last;
    private static event Action? ChangedInternal;
    private static AppSettings _settings = new(null);
    private static AppearanceMode _mode;
    private static AppearanceStyle _style;
    private static AccentChoice _accent;
    /// <summary>Windows' accent, read once and again when Windows says its colours changed.</summary>
    private static AccentShades? _systemAccent;

    /// <summary>Raised on the UI thread when the app theme, the appearance settings, Windows' accent or high contrast
    /// change.</summary>
    public static event Action? Changed
    {
        add
        {
            if (!_listening)
            {
                _listening = true;
                _last ??= Look;
                SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            }
            ChangedInternal += value;
        }
        remove => ChangedInternal -= value;
    }

    /// <summary>Reads Settings → Appearance from <paramref name="settings"/> and saves changes there. Until then (and in
    /// snapshots and checks that don't call it) the defaults apply, kept in memory.</summary>
    public static void Use(AppSettings settings)
    {
        _settings = settings;
        _mode = Appearance.ParseMode(settings.Get<string>(AppSettings.Keys.AppearanceMode));
        _style = Appearance.ParseStyle(settings.Get<string>(AppSettings.Keys.AppearanceStyle));
        _accent = Appearance.ParseAccent(settings.Get<string>(AppSettings.Keys.AccentColor));
        Notify();
    }

    /// <summary>Light, dark, or Windows' app mode (Settings → Appearance → Mode).</summary>
    public static AppearanceMode Mode
    {
        get => _mode;
        set { _mode = value; Save(AppSettings.Keys.AppearanceMode, value.RawValue()); }
    }

    /// <summary>Windows 11's look or Bambu Glass. Read when views are built (<see cref="Metrics"/>' radii,
    /// <see cref="ThemeResources"/>' brushes); a change rebuilds them.</summary>
    public static AppearanceStyle Style
    {
        get => _style;
        set { _style = value; Save(AppSettings.Keys.AppearanceStyle, value.RawValue()); }
    }

    public static AccentChoice AccentChoice
    {
        get => _accent;
        set { _accent = value; Save(AppSettings.Keys.AccentColor, value.RawValue()); }
    }

    /// <summary>The chosen accent's shades; Windows' own for <see cref="AccentChoice.System"/>.</summary>
    public static AccentShades Accent => Shades(_accent);

    /// <summary>An accent's shades (the Settings page's swatches).</summary>
    public static AccentShades Shades(AccentChoice choice) => choice.Preset() ?? (_systemAccent ??= SystemAccent());

    /// <summary>The Bambu Glass style is on (the Mac's gradients and radii).</summary>
    public static bool IsGlass => _style == AppearanceStyle.Glass;

    public static ThemeLook Look => new(Current, _style, Accent);

    public static ThemeVariant Current
    {
        get
        {
            if (SystemParameters.HighContrast) return ThemeVariant.HighContrast;
            return _mode switch
            {
                AppearanceMode.Light => ThemeVariant.Light,
                AppearanceMode.Dark => ThemeVariant.Dark,
                _ => SystemVariant(),
            };
        }
    }

    /// <summary>Windows' app mode, light where it can't be read.</summary>
    private static ThemeVariant SystemVariant()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return ThemeVariant.Light;
        }
    }

    /// <summary>Windows' accent colour (Settings → Personalisation → Colours), as Fluent shades it; Bambu green where it
    /// can't be read.</summary>
    private static AccentShades SystemAccent()
    {
        byte[]? palette = null;
        int? dwm = null;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                palette = key?.GetValue("AccentPalette") as byte[];
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                dwm = key?.GetValue("AccentColor") is int v ? v : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
        }
        return Appearance.FromWindows(palette, dwm);
    }

    /// <summary>"Show animations in Windows" is on (Settings → Accessibility → Visual effects).</summary>
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Windows 11 22H2 or later: the DWM system backdrop (Mica, acrylic) is available.</summary>
    public static bool HasSystemBackdrop => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>Windows 11: rounded corners on request, and Segoe Fluent Icons.</summary>
    public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private static void Save(string key, string raw)
    {
        _settings.Set(key, raw);
        Notify();
    }

    private static void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility
            or UserPreferenceCategory.VisualStyle)) return;
        _systemAccent = null;
        Notify();
    }

    /// <summary>Raises <see cref="Changed"/> when the look differs from the last one raised. Later on the UI thread,
    /// not now: a setting chosen in a menu re-themes after the click is done with the menu and the page it rebuilds.</summary>
    private static void Notify()
    {
        var now = Look;
        if (_last == now) return;
        _last = now;
        // SystemEvents raises on its own thread unless a window pumps it; hop to the UI thread either way.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) ChangedInternal?.Invoke();
        else dispatcher.BeginInvoke(() => ChangedInternal?.Invoke());
    }
}
