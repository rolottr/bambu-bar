using System.Windows;
using Microsoft.Win32;

namespace BambuBar.Theme;

public enum ThemeVariant { Light, Dark, HighContrast }

/// <summary>
/// The Windows app theme ("Choose your app mode": <c>AppsUseLightTheme</c>), high contrast, and the animation setting,
/// with a notification when any of them changes. The taskbar follows the other switch (<c>SystemUsesLightTheme</c>),
/// which only the tray glyph reads.
/// </summary>
public static class AppTheme
{
    private static bool _listening;
    private static ThemeVariant _last;
    private static event Action? ChangedInternal;

    /// <summary>Raised on the UI thread when the app theme or high contrast changes.</summary>
    public static event Action? Changed
    {
        add
        {
            if (!_listening)
            {
                _listening = true;
                _last = Current;
                SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            }
            ChangedInternal += value;
        }
        remove => ChangedInternal -= value;
    }

    public static ThemeVariant Current
    {
        get
        {
            if (SystemParameters.HighContrast) return ThemeVariant.HighContrast;
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
    }

    /// <summary>"Show animations in Windows" is on (Settings → Accessibility → Visual effects).</summary>
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Windows 11 22H2 or later: the DWM system backdrop (Mica, acrylic) is available.</summary>
    public static bool HasSystemBackdrop => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>Windows 11: rounded corners on request, and Segoe Fluent Icons.</summary>
    public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private static void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility
            or UserPreferenceCategory.VisualStyle)) return;
        var now = Current;
        if (now == _last) return;
        _last = now;
        // SystemEvents raises on its own thread unless a window pumps it; hop to the UI thread either way.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) ChangedInternal?.Invoke();
        else dispatcher.BeginInvoke(() => ChangedInternal?.Invoke());
    }
}
